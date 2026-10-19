using System;
using System.Collections.Generic;
using Core.Unit;

namespace RimClone.Render;

/// <summary>A player-created infantry squad. Members remain individual units.</summary>
public sealed class InfantrySquad
{
    private readonly List<UnitId> _members = new();

    public int Id { get; }
    public UnitId Commander { get; private set; }
    public IReadOnlyList<UnitId> Members => _members;

    internal InfantrySquad(int id, IReadOnlyList<UnitId> members)
    {
        Id = id;
        for (int i = 0; i < members.Count; i++)
            AddMember(members[i]);
    }

    internal bool Contains(UnitId unit) => _members.Contains(unit);

    internal void AddMember(UnitId unit)
    {
        if (_members.Contains(unit))
            return;

        _members.Add(unit);
        if (!Commander.IsValid)
            Commander = unit;
    }

    internal bool RemoveMember(UnitId unit)
    {
        if (!_members.Remove(unit))
            return false;

        if (Commander == unit)
            Commander = _members.Count > 0 ? _members[0] : default;

        return true;
    }
}

/// <summary>
/// Owns squad membership and Men of War-style number shortcuts. It does not
/// merge soldiers into a single simulation unit; combat remains per soldier.
/// </summary>
public sealed class InfantrySquadManager
{
    private readonly Dictionary<int, InfantrySquad> _squads = new();
    private readonly Dictionary<UnitId, int> _membership = new();
    private readonly Dictionary<int, int> _hotkeys = new();
    private int _nextId = 1;

    public IReadOnlyCollection<InfantrySquad> Squads => _squads.Values;

    public int GetSquadId(UnitId unit) =>
        _membership.TryGetValue(unit, out int squadId) ? squadId : 0;

    public InfantrySquad? GetSquad(int squadId) =>
        _squads.TryGetValue(squadId, out InfantrySquad? squad) ? squad : null;

    public bool TryGetSquad(UnitId unit, out InfantrySquad squad)
    {
        if (_membership.TryGetValue(unit, out int squadId) &&
            _squads.TryGetValue(squadId, out InfantrySquad? found))
        {
            squad = found;
            return true;
        }

        squad = null!;
        return false;
    }

    public int GetHotkeySquadId(int number) =>
        _hotkeys.TryGetValue(number, out int squadId) && _squads.ContainsKey(squadId)
            ? squadId
            : 0;

    public int Create(IReadOnlyList<UnitId> members)
    {
        List<UnitId> unique = new(members.Count);
        HashSet<UnitId> seen = new();
        for (int i = 0; i < members.Count; i++)
        {
            UnitId member = members[i];
            if (member.IsValid && seen.Add(member))
                unique.Add(member);
        }

        if (unique.Count == 0)
            return 0;

        // A soldier can belong to only one squad. Box-selecting an existing
        // squad's members creates a fresh squad with exactly that selection.
        for (int i = 0; i < unique.Count; i++)
            RemoveMember(unique[i]);

        int id = _nextId++;
        InfantrySquad squad = new(id, unique);
        _squads.Add(id, squad);
        for (int i = 0; i < unique.Count; i++)
            _membership[unique[i]] = id;

        return id;
    }

    public bool AddMember(int squadId, UnitId member)
    {
        if (!member.IsValid || !_squads.TryGetValue(squadId, out InfantrySquad? squad))
            return false;

        if (GetSquadId(member) == squadId)
            return true;

        RemoveMember(member);
        squad.AddMember(member);
        _membership[member] = squadId;
        return true;
    }

    public bool RemoveMember(UnitId member)
    {
        if (!_membership.Remove(member, out int squadId) ||
            !_squads.TryGetValue(squadId, out InfantrySquad? squad))
        {
            return false;
        }

        squad.RemoveMember(member);
        if (squad.Members.Count == 0)
            RemoveSquadRecord(squadId);

        return true;
    }

    public int DisbandContaining(IEnumerable<UnitId> members)
    {
        HashSet<int> ids = new();
        foreach (UnitId member in members)
        {
            int squadId = GetSquadId(member);
            if (squadId != 0)
                ids.Add(squadId);
        }

        int disbanded = 0;
        foreach (int squadId in ids)
        {
            if (Disband(squadId))
                disbanded++;
        }

        return disbanded;
    }

    public bool Disband(int squadId)
    {
        if (!_squads.TryGetValue(squadId, out InfantrySquad? squad))
            return false;

        for (int i = 0; i < squad.Members.Count; i++)
            _membership.Remove(squad.Members[i]);

        RemoveSquadRecord(squadId);
        return true;
    }

    public bool AssignHotkey(int number, int squadId)
    {
        if (number is < 1 or > 9 || !_squads.ContainsKey(squadId))
            return false;

        // Keep the shortcut unambiguous and let each squad have one number.
        List<int> oldKeys = new();
        foreach (KeyValuePair<int, int> pair in _hotkeys)
        {
            if (pair.Key == number || pair.Value == squadId)
                oldKeys.Add(pair.Key);
        }

        for (int i = 0; i < oldKeys.Count; i++)
            _hotkeys.Remove(oldKeys[i]);

        _hotkeys[number] = squadId;
        return true;
    }

    private void RemoveSquadRecord(int squadId)
    {
        _squads.Remove(squadId);
        List<int> oldKeys = new();
        foreach (KeyValuePair<int, int> pair in _hotkeys)
        {
            if (pair.Value == squadId)
                oldKeys.Add(pair.Key);
        }

        for (int i = 0; i < oldKeys.Count; i++)
            _hotkeys.Remove(oldKeys[i]);
    }
}
