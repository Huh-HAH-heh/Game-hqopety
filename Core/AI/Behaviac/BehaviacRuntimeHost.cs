using System;
using System.IO;
using System.Collections.Generic;
using behaviac;

namespace Core.AI.Behaviac;

/// <summary>
/// Owns Behaviac's process-wide workspace for the live UnitAiSystem decision tree.
/// </summary>
public static class BehaviacRuntimeHost
{
    private static readonly object Sync = new();
    private static string? _behaviorRoot;
    private static bool _initialized;
    private static Workspace.EFileFormat _fileFormat;
    private static readonly HashSet<Type> RegisteredAgentTypes = new();

    public static bool IsInitialized
    {
        get
        {
            lock (Sync)
                return _initialized;
        }
    }

    /// <summary>
    /// Initializes the XML behavior assets shipped with RimClone.
    /// The tree structure is data; RimClone-specific facts and actions are registered below.
    /// </summary>
    public static void InitializeDefaultBehavior()
    {
        Initialize(Path.Combine(AppContext.BaseDirectory, "BehaviacData"), Workspace.EFileFormat.EFF_xml);
    }

    public static void Initialize(string behaviorRoot, Workspace.EFileFormat fileFormat = Workspace.EFileFormat.EFF_xml)
    {
        if (string.IsNullOrWhiteSpace(behaviorRoot))
            throw new ArgumentException("A Behaviac behavior directory is required.", nameof(behaviorRoot));

        string fullPath = Path.GetFullPath(behaviorRoot).Replace('\\', '/');
        if (!Directory.Exists(fullPath))
            throw new DirectoryNotFoundException($"Behaviac behavior directory was not found: {fullPath}");

        lock (Sync)
        {
            if (_initialized)
            {
                if (!string.Equals(_behaviorRoot, fullPath, StringComparison.OrdinalIgnoreCase) || _fileFormat != fileFormat)
                    throw new InvalidOperationException("Behaviac workspace is already initialized with different settings.");
                return;
            }

            // Behaviac configuration must be set before Workspace first initializes.
            Config.IsLogging = false;
            Config.IsSocketing = false;
            Config.IsHotReload = false;
            Workspace.Instance.FilePath = fullPath;
            Workspace.Instance.FileFormat = fileFormat;
            Workspace.Instance.MetaFile = string.Empty;

            // Let Behaviac initialize comparer/computer registries and register its
            // standard metadata exactly once before binding RimClone's XML methods.
            if (!Workspace.Instance.TryInit())
                throw new InvalidOperationException("Behaviac workspace initialization failed.");

            RegisterDecisionAgentMetadata();

            _behaviorRoot = fullPath;
            _fileFormat = fileFormat;
            _initialized = true;
        }
    }

    private static void RegisterDecisionAgentMetadata()
    {
        const string agentTypeName = nameof(BehaviacUnitDecisionAgent);
        uint classId = Utils.MakeVariableId(agentTypeName);
        var meta = new AgentMeta();
        AgentMeta._AgentMetas_[classId] = meta;
        AgentMeta.Register<BehaviacUnitDecisionAgent>(agentTypeName);

        RegisterCondition(meta, nameof(BehaviacUnitDecisionAgent.ShouldReturnDead),
            self => self.ShouldReturnDead());
        RegisterCondition(meta, nameof(BehaviacUnitDecisionAgent.ShouldSeekCover),
            self => self.ShouldSeekCover());
        RegisterCondition(meta, nameof(BehaviacUnitDecisionAgent.ShouldRetreatToCover),
            self => self.ShouldRetreatToCover());
        RegisterCondition(meta, nameof(BehaviacUnitDecisionAgent.ShouldFlankVisibleTarget),
            self => self.ShouldFlankVisibleTarget());
        RegisterCondition(meta, nameof(BehaviacUnitDecisionAgent.ShouldEngageVisibleTarget),
            self => self.ShouldEngageVisibleTarget());
        RegisterCondition(meta, nameof(BehaviacUnitDecisionAgent.ShouldSuppressLastKnownTarget),
            self => self.ShouldSuppressLastKnownTarget());
        RegisterCondition(meta, nameof(BehaviacUnitDecisionAgent.ShouldInvestigateLastKnownTarget),
            self => self.ShouldInvestigateLastKnownTarget());

        RegisterAction(meta, nameof(BehaviacUnitDecisionAgent.SelectDead),
            self => self.SelectDead());
        RegisterAction(meta, nameof(BehaviacUnitDecisionAgent.SelectSeekCover),
            self => self.SelectSeekCover());
        RegisterAction(meta, nameof(BehaviacUnitDecisionAgent.SelectRetreatToCover),
            self => self.SelectRetreatToCover());
        RegisterAction(meta, nameof(BehaviacUnitDecisionAgent.SelectFlankTarget),
            self => self.SelectFlankTarget());
        RegisterAction(meta, nameof(BehaviacUnitDecisionAgent.SelectEngageTarget),
            self => self.SelectEngageTarget());
        RegisterAction(meta, nameof(BehaviacUnitDecisionAgent.SelectSuppressiveFire),
            self => self.SelectSuppressiveFire());
        RegisterAction(meta, nameof(BehaviacUnitDecisionAgent.SelectInvestigateLastKnownTarget),
            self => self.SelectInvestigateLastKnownTarget());
        RegisterAction(meta, nameof(BehaviacUnitDecisionAgent.SelectPatrol),
            self => self.SelectPatrol());
    }

    private static void RegisterCondition(
        AgentMeta meta,
        string methodName,
        Func<BehaviacUnitDecisionAgent, bool> condition)
    {
        meta.RegisterMethod(
            Utils.MakeVariableId(methodName),
            new CAgentMethod<bool>(self => condition((BehaviacUnitDecisionAgent)self)));
    }

    private static void RegisterAction(
        AgentMeta meta,
        string methodName,
        Func<BehaviacUnitDecisionAgent, EBTStatus> action)
    {
        meta.RegisterMethod(
            Utils.MakeVariableId(methodName),
            new CAgentMethod<EBTStatus>(self => action((BehaviacUnitDecisionAgent)self)));
    }

    public static TAgent CreateAgent<TAgent>(string behaviorName)
        where TAgent : Agent, new()
    {
        if (string.IsNullOrWhiteSpace(behaviorName))
            throw new ArgumentException("A behavior name is required.", nameof(behaviorName));

        lock (Sync)
        {
            if (!_initialized)
                throw new InvalidOperationException("Initialize BehaviacRuntimeHost before creating an agent.");

            if (RegisteredAgentTypes.Add(typeof(TAgent)))
                Agent.RegisterInstanceName<TAgent>();

            var agent = new TAgent();
            if (!agent.btload(behaviorName))
                throw new InvalidOperationException($"Behaviac failed to load behavior '{behaviorName}'.");

            agent.btsetcurrent(behaviorName);
            return agent;
        }
    }

    public static EBTStatus Tick(Agent agent)
    {
        ArgumentNullException.ThrowIfNull(agent);
        lock (Sync)
        {
            if (!_initialized)
                throw new InvalidOperationException("BehaviacRuntimeHost is not initialized.");

            return agent.btexec();
        }
    }

    public static void Shutdown()
    {
        lock (Sync)
        {
            if (!_initialized)
                return;

            Workspace.Instance.Cleanup();
            _behaviorRoot = null;
            _initialized = false;
        }
    }
}
