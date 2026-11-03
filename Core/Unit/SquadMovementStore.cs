            int index = id.Index;
            Vector3 position = units.Position[index];
            center += new Vector2(position.X, position.Y);
            spacing = MathF.Max(spacing, MathF.Max(units.Width[index], units.Length[index]) * 1.15f);
            float effectiveSpeed = MathF.Max(0.1f, units.MoveSpeed[index]);
            if (units.Posture[index] == UnitPosture.Lying)
                effectiveSpeed *= 0.45f;
            slowestSpeed = MathF.Min(slowestSpeed, effectiveSpeed);
        }

        center /= members.Count;
        Vector2 goal = new(
            Math.Clamp(destination.X, 0.5f, worldMap.MaxTileX - 0.5f),
            Math.Clamp(destination.Y, 0.5f, worldMap.MaxTileY - 0.5f));
        Vector2 forward = goal - center;
        if (forward.LengthSquared() < 0.0001f)
            forward = Vector2.UnitX;
        else
            forward = Vector2.Normalize(forward);
        // Line and wedge retain the old individual landing positions. Each
        // pawn gets its own footprint-valid A* goal; no second steering force
        // tries to pull it away from that route.
        bool usesIndividualFormationGoals =
            formation is InfantryFormationType.Line or InfantryFormationType.Wedge;
        Vector2 side = new(-forward.Y, forward.X);
        Vector2 meanOffset = Vector2.Zero;
        if (usesIndividualFormationGoals)
        {
            for (int i = 0; i < members.Count; i++)
                meanOffset += InfantryFormation.GetOffset(i, members.Count, formation, spacing);
            meanOffset /= members.Count;
        }

        HashSet<int> reservedGoals = usesIndividualFormationGoals
            ? new HashSet<int>()
            : null!;
        Vector2[] goals = new Vector2[members.Count];
        Vector2[] memberOffsets = new Vector2[members.Count];
        for (int i = 0; i < members.Count; i++)
        {
            UnitId id = members[i];
            Vector2 localOffset = usesIndividualFormationGoals
                ? InfantryFormation.GetOffset(i, members.Count, formation, spacing) - meanOffset
                : Vector2.Zero;
            Vector2 requestedGoal = goal +
                side * localOffset.X -
                forward * localOffset.Y;
            requestedGoal.X = Math.Clamp(
                requestedGoal.X, 0.5f, worldMap.MaxTileX - 0.5f);
            requestedGoal.Y = Math.Clamp(
                requestedGoal.Y, 0.5f, worldMap.MaxTileY - 0.5f);

            if (!navigation.TryResolveAStarGoal(
                    units,
                    id.Index,
                    worldMap,
                    requestedGoal,
                    usesIndividualFormationGoals ? reservedGoals : null,
                    out Vector2 terminal))
            {
                terminal = ResolveNavigablePoint(
                    worldMap,