using System;
using System.Diagnostics;
using behaviac;
using Core.AI;
using Core.AI.Behaviac;
using Core.AI.Perception;
using Core.Unit;

internal static class Program
{
    private const int BenchmarkUnits = 800;
    private const int BenchmarkTicks = 100;
    private const int WarmupTicks = 5;

    private static int Main(string[] args)
    {
        if (Array.Exists(args, arg => string.Equals(arg, "--decision-test", StringComparison.Ordinal)))
            return RunDecisionTests();

        if (Array.Exists(args, arg => string.Equals(arg, "--decision-benchmark", StringComparison.Ordinal)))
            return RunDecisionBenchmark();

        return RunXmlSmokeTest();
    }

    private static int RunXmlSmokeTest()
    {
        string dataRoot = Path.Combine(AppContext.BaseDirectory, "BehaviacData")
            .Replace('\\', '/');

        try
        {
            Config.IsLogging = false;
            Config.IsSocketing = false;
            Config.IsHotReload = false;

            if (!Agent.RegisterInstanceName<CBTPlayer>())
                throw new InvalidOperationException("Behaviac could not register CBTPlayer.");

            Workspace.Instance.FilePath = dataRoot;
            Workspace.Instance.FileFormat = Workspace.EFileFormat.EFF_xml;

            var agent = new CBTPlayer();
            if (!agent.btload("demo_running"))
                throw new InvalidOperationException("Behaviac failed to load the sample behavior tree.");

            agent.btsetcurrent("demo_running");

            EBTStatus status = EBTStatus.BT_RUNNING;
            int ticks = 0;
            while (status == EBTStatus.BT_RUNNING && ticks < 64)
            {
                status = agent.btexec();
                ticks++;
            }

            bool passed =
                status == EBTStatus.BT_SUCCESS &&
                agent.ConditionCalls == 3 &&
                agent.Action1Calls == 3 &&
                agent.Action3Calls == 9;

            Console.WriteLine(
                $"Behaviac XML smoke test: {(passed ? "PASS" : "FAIL")}; " +
                $"status={status}; ticks={ticks}; " +
                $"conditions={agent.ConditionCalls}; " +
                $"action1={agent.Action1Calls}; action3={agent.Action3Calls}");

            Workspace.Instance.Cleanup();
            return passed ? 0 : 1;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine($"Behaviac XML smoke test: FAIL; {exception}");
            try { Workspace.Instance.Cleanup(); } catch { }
            return 1;
        }
    }

    private static bool RunTacticalUtilityTests()
    {
        (string Name, float Actual, float Expected)[] tests =
        [
            ("move utility at destination", UnitTacticalUtility.EvaluateMoveOrder(0f, false, false), 0.50f),
            ("move utility reaches distance cap", UnitTacticalUtility.EvaluateMoveOrder(24f, false, false), 0.80f),
            ("move utility remains capped at long distance", UnitTacticalUtility.EvaluateMoveOrder(100f, false, false), 0.80f),
            ("suppression penalizes movement intent", UnitTacticalUtility.EvaluateMoveOrder(24f, true, false), 0.68f),
            ("low health penalizes movement intent", UnitTacticalUtility.EvaluateMoveOrder(24f, false, true), 0.70f),
            ("suppression and low health stack", UnitTacticalUtility.EvaluateMoveOrder(24f, true, true), 0.58f),
            ("no visible target means no immediate combat utility", UnitTacticalUtility.EvaluateVisibleCombat(5f, false), 0f),
            ("close combat utility", UnitTacticalUtility.EvaluateVisibleCombat(6f, true), 0.96f),
            ("medium combat utility upper boundary", UnitTacticalUtility.EvaluateVisibleCombat(12f, true), 0.84f),
            ("long combat utility upper boundary", UnitTacticalUtility.EvaluateVisibleCombat(24f, true), 0.68f),
            ("distant combat utility", UnitTacticalUtility.EvaluateVisibleCombat(24.01f, true), 0.54f),
            ("exposed prone firing utility at minimum distance", UnitTacticalUtility.EvaluateProneFiring(9f, true, 1f), 0.35f),
            ("exposed distant prone firing utility", UnitTacticalUtility.EvaluateProneFiring(140f, true, 1f), 1f),
            ("cover lowers prone-position advantage at long range", UnitTacticalUtility.EvaluateProneFiring(140f, false, 1f), 0.80f),
            ("missing cover makes cover action infeasible", UnitTacticalUtility.EvaluateCover(5.8f, false, false, false, false), 0f),
            ("immediate danger overrides utility trade-off", UnitTacticalUtility.EvaluateCover(0f, true, true, false, false), 1f),
            ("panic makes available cover the top action", UnitTacticalUtility.EvaluateCover(5.8f, true, false, true, false), 1f),
            ("mild suppression gives cover a low score", UnitTacticalUtility.EvaluateCover(1.8f, true, false, false, false), 0.18f),
            ("flank gains utility when far outside weapon range", UnitTacticalUtility.EvaluateFlanking(45f, 18f, true), 0.628125f),
            ("infeasible flank has no utility", UnitTacticalUtility.EvaluateFlanking(45f, 18f, false), 0f),
            ("investigation rewards credible nearby contact", UnitTacticalUtility.EvaluateInvestigation(0.8f, 20f), 0.740625f),
            ("weak distant sound is not worth investigating", UnitTacticalUtility.EvaluateInvestigation(0.16f, 64f), 0.02f)
        ];

        bool passedAll = true;
        foreach ((string name, float actual, float expected) in tests)
        {
            bool passed = MathF.Abs(actual - expected) <= 0.0001f;
            passedAll &= passed;
            Console.WriteLine(
                $"Tactical utility test: {(passed ? "PASS" : "FAIL")}; " +
                $"{name}; actual={actual:F3}; expected={expected:F3}");
        }

        return passedAll;
    }

    private static bool RunTacticalContactMemoryTests()
    {
        bool passedAll = true;
        int checks = 0;

        void Check(string name, bool passed)
        {
            checks++;
            passedAll &= passed;
            Console.WriteLine($"Tactical contact test: {(passed ? "PASS" : "FAIL")}; {name}");
        }

        UnitId target = new(11, 2);
        var memory = new TacticalContactMemory();
        TacticalContact? visible = memory.ObserveVisual(
            target,
            new System.Numerics.Vector3(10f, 20f, 0f),
            0.1f,
            memoryLifetimeSeconds: 5f,
            positionErrorRadius: 0.25f);

        Check("visual observation stores an identified, confirmed contact",
            visible != null &&
            visible.Kind == TacticalContactKind.VisualConfirmed &&
            visible.SubjectUnit.HasValue &&
            visible.SubjectUnit.Value.Equals(target) &&
            visible.PositionErrorRadius == 0.25f);

        memory.AdvanceTime(1.11f);
        Check("old visual confirmation downgrades to last-known",
            memory.TryGetContact(target, out TacticalContact lastKnown) &&
            lastKnown.Kind == TacticalContactKind.LastKnown);

        float effectiveConfidence = lastKnown.GetEffectiveConfidence(2.1f, 2f);
        Check("confidence decays from event time with the configured half-life",
            MathF.Abs(effectiveConfidence - 0.5f) < 0.002f);

        bool markedLost = memory.MarkVisualLost(target, 1.2f, 4f);
        bool markedLostAgain = memory.MarkVisualLost(target, 1.3f, 4f);
        Check("visual-loss transition extends memory once and is idempotent",
            markedLost &&
            !markedLostAgain &&
            memory.TryGetContact(target, out TacticalContact afterLoss) &&
            afterLoss.Kind == TacticalContactKind.LastKnown &&
            MathF.Abs(afterLoss.ExpiresAtSeconds - 5.2f) < 0.001f);

        memory.AdvanceTime(5.21f);
        Check("expired contacts are removed",
            !memory.TryGetContact(target, out _) && memory.Contacts.Count == 0);

        var audioMemory = new TacticalContactMemory();
        var firstShot = new TacticalStimulus(
            TacticalStimulusType.Gunshot,
            new System.Numerics.Vector3(50f, 50f, 0f),
            1f,
            intensity: 0.8f,
            positionErrorRadius: 8f,
            confidence: 0.45f);
        TacticalContact? suspected = audioMemory.Record(firstShot, 1f, 5f);

        var nearbyShot = new TacticalStimulus(
            TacticalStimulusType.Gunshot,
            new System.Numerics.Vector3(55f, 50f, 0f),
            1.2f,
            intensity: 0.7f,
            positionErrorRadius: 8f,
            confidence: 0.4f);
        TacticalContact? merged = audioMemory.Record(nearbyShot, 1.2f, 5f);

        Check("unknown stimuli create and merge an uncertain contact",
            suspected != null &&
            merged != null &&
            suspected.ContactId == merged.ContactId &&
            merged.Kind == TacticalContactKind.Suspected &&
            !merged.SubjectUnit.HasValue &&
            MathF.Abs(merged.EstimatedPosition.X - 55f) < 0.001f &&
            MathF.Abs(merged.Confidence - 0.45f) < 0.001f &&
            audioMemory.Contacts.Count == 1);

        var distantShot = new TacticalStimulus(
            TacticalStimulusType.Gunshot,
            new System.Numerics.Vector3(100f, 100f, 0f),
            1.3f,
            positionErrorRadius: 3f,
            confidence: 0.5f);
        audioMemory.Record(distantShot, 1.3f, 5f);
        Check("spatially separate unknown events remain separate contacts",
            audioMemory.Contacts.Count == 2);

        var resolvingMemory = new TacticalContactMemory();
        TacticalContact? audioContact = resolvingMemory.Record(
            new TacticalStimulus(
                TacticalStimulusType.Gunshot,
                new System.Numerics.Vector3(40f, 40f, 0f),
                1f,
                positionErrorRadius: 10f,
                confidence: 0.4f),
            1f,
            5f);
        UnitId resolvedTarget = new(6, 2);
        TacticalContact? resolvedContact = resolvingMemory.Record(
            new TacticalStimulus(
                TacticalStimulusType.AllyContactReport,
                new System.Numerics.Vector3(44f, 43f, 0f),
                1.2f,
                positionErrorRadius: 2f,
                confidence: 0.7f,
                subjectUnit: resolvedTarget),
            1.2f,
            5f);
        Check("identified report does not guess the identity of an earlier uncertain sound",
            audioContact != null &&
            resolvedContact != null &&
            audioContact.ContactId != resolvedContact.ContactId &&
            !audioContact.SubjectUnit.HasValue &&
            resolvedContact.SubjectUnit.HasValue &&
            resolvedContact.SubjectUnit.Value.Equals(resolvedTarget) &&
            resolvedContact.Kind == TacticalContactKind.Reported &&
            resolvingMemory.Contacts.Count == 2);

        UnitId reporter = new(4, 1);
        UnitId reportedTarget = new(8, 3);
        var reportMemory = new TacticalContactMemory();
        var report = new TacticalStimulus(
            TacticalStimulusType.AllyContactReport,
            new System.Numerics.Vector3(80f, 80f, 0f),
            3f,
            intensity: 0.7f,
            positionErrorRadius: 6f,
            confidence: 0.6f,
            subjectUnit: reportedTarget,
            reporterUnit: reporter);
        TacticalContact? reported = reportMemory.Record(report, 3.2f, 5f);
        Check("ally reports preserve subject identity without claiming direct sight",
            reported != null &&
            reported.Kind == TacticalContactKind.Reported &&
            reported.SubjectUnit.HasValue &&
            reported.SubjectUnit.Value.Equals(reportedTarget) &&
            reported.ReporterUnit.HasValue &&
            reported.ReporterUnit.Value.Equals(reporter));

        var clamped = new TacticalStimulus(
            TacticalStimulusType.Footstep,
            System.Numerics.Vector3.Zero,
            0f,
            intensity: 2f,
            confidence: -1f);
        Check("stimulus intensity and confidence are clamped",
            clamped.Intensity == 1f && clamped.Confidence == 0f);

        var staleMemory = new TacticalContactMemory();
        var staleEvent = new TacticalStimulus(
            TacticalStimulusType.Gunshot,
            System.Numerics.Vector3.Zero,
            1f,
            confidence: 0.8f);
        TacticalContact? stale = staleMemory.Record(staleEvent, 10f, 4f);
        Check("delayed, expired events cannot revive stale knowledge",
            stale == null && staleMemory.Contacts.Count == 0);

        audioMemory.Forget(new UnitId(99, 1));
        audioMemory.Clear();
        var investigationMemory = new TacticalContactMemory();
        TacticalContact? investigationContact = investigationMemory.Record(
            new TacticalStimulus(
                TacticalStimulusType.Gunshot,
                new System.Numerics.Vector3(12f, 15f, 0f),
                1f,
                positionErrorRadius: 6f,
                confidence: 0.8f),
            1f,
            4f);
        bool markedInvestigated = investigationContact != null &&
            investigationMemory.MarkInvestigated(investigationContact.ContactId);
        Check("a handled contact is not immediately investigated again",
            markedInvestigated &&
            investigationContact!.IsInvestigated &&
            !investigationMemory.MarkInvestigated(long.MaxValue));

        Check("contact memory clear resets its contacts and simulation clock",
            audioMemory.Contacts.Count == 0 && audioMemory.CurrentTimeSeconds == 0f);

        Console.WriteLine(
            $"Tactical contact memory tests: {(passedAll ? "PASS" : "FAIL")}; {checks} cases");
        return passedAll;
    }

    private static bool RunSuppressionResponseTests()
    {
        bool passedAll = true;
        int checks = 0;

        void Check(string name, bool passed)
        {
            checks++;
            passedAll &= passed;
            Console.WriteLine($"Suppression response test: {(passed ? "PASS" : "FAIL")}; {name}");
        }

        var skills = new UnitSkillStore(2);
        skills.SetSuppressionResistance(0, 0f);
        skills.SetSuppressionResistance(1, 0.95f);
        var response = new UnitSuppressionStore(2);
        response.BindSkillStore(skills);
        response.InitializeUnit(0);
        response.InitializeUnit(1);

        response.AddAlertness(0, 0.7f, new System.Numerics.Vector3(10f, 10f, 0f));
        Check("awareness alone does not cause suppression or panic",
            response.IsAlerted(0) && response.GetState(0) == UnitSuppressionState.Calm &&
            response.Value[0] == 0f);

        response.Add(0, 3f, new System.Numerics.Vector3(10f, 10f, 0f));
        response.Add(1, 3f, new System.Numerics.Vector3(10f, 10f, 0f));
        Check("individual resistance lowers suppression buildup",
            response.Value[0] > response.Value[1] &&
            response.GetState(0) == UnitSuppressionState.Suppressed &&
            response.GetState(1) == UnitSuppressionState.Calm);

        response.Add(0, 6f, new System.Numerics.Vector3(10f, 10f, 0f));
        Check("panic requires sustained high suppression",
            response.GetState(0) == UnitSuppressionState.Panicked);

        response.Value[0] = UnitSuppressionStore.PanicRecoveryThreshold + 0.2f;
        Check("panic uses hysteresis instead of immediately clearing below its entry threshold",
            response.GetState(0) == UnitSuppressionState.Panicked);

        response.Value[0] = UnitSuppressionStore.SuppressionRecoveryThreshold - 0.1f;
        Check("response returns from panic to calm after pressure recedes",
            response.GetState(0) == UnitSuppressionState.Calm);

        response.InitializeUnit(0);
        response.SetInCover(0, true);
        response.Add(0, 2f, System.Numerics.Vector3.Zero);
        float coveredSuppression = response.Value[0];
        response.SetInCover(0, false);
        response.Add(0, 2f, System.Numerics.Vector3.Zero);
        Check("being in cover reduces suppression buildup",
            coveredSuppression < response.Value[0]);

        Console.WriteLine($"Suppression response tests: {(passedAll ? "PASS" : "FAIL")}; {checks} cases");
        return passedAll;
    }

    private static bool RunSquadTacticalCoordinatorTests()
    {
        bool passedAll = true;
        int checks = 0;
        void Check(string name, bool passed)
        {
            checks++;
            passedAll &= passed;
            Console.WriteLine($"Squad coordinator test: {(passed ? "PASS" : "FAIL")}; {name}");
        }

        UnitId leader = new(1, 1), wingman = new(2, 1), other = new(3, 1), target = new(10, 1);
        var coordinator = new SquadTacticalCoordinator();
        coordinator.SynchronizeMemberships([
            new SquadMembershipSnapshot(1, new[] { leader, wingman }),
            new SquadMembershipSnapshot(2, new[] { other })
        ]);

        Check("membership stays in the assigned squad",
            coordinator.IsMemberOfSquad(leader, 1) &&
            coordinator.IsMemberOfSquad(wingman, 1) &&
            !coordinator.IsMemberOfSquad(wingman, 2) &&
            coordinator.IsMemberOfSquad(other, 2));

        Check("only one squad member owns the same flank opportunity",
            coordinator.TryClaimFlank(leader, target, 1f) &&
            !coordinator.TryClaimFlank(wingman, target, 1.25f));

        coordinator.Advance(2.01f);
        Check("flank ownership expires and can be reassigned",
            coordinator.TryClaimFlank(wingman, target, 2.01f));

        coordinator.SynchronizeMemberships([
            new SquadMembershipSnapshot(1, new[] { leader }),
            new SquadMembershipSnapshot(2, new[] { other })
        ]);
        Check("detached member loses squad membership",
            !coordinator.IsMemberOfAnySquad(wingman) &&
            !coordinator.IsMemberOfSquad(wingman, 1));

        Check("removed ownership does not block the remaining member",
            coordinator.TryClaimFlank(leader, target, 2.1f));

        Console.WriteLine($"Squad coordinator tests: {(passedAll ? "PASS" : "FAIL")}; {checks} cases");
        return passedAll;
    }

    private static int RunDecisionTests()
    {
        DecisionCase[] cases = GetDecisionCases();
        bool allPassed = RunTacticalUtilityTests();
        allPassed &= RunTacticalContactMemoryTests();
        allPassed &= RunSuppressionResponseTests();
        allPassed &= RunSquadTacticalCoordinatorTests();

        try
        {
            string dataRoot = Path.Combine(AppContext.BaseDirectory, "BehaviacData");
            BehaviacRuntimeHost.Initialize(dataRoot, Workspace.EFileFormat.EFF_xml);

            foreach (DecisionCase test in cases)
            {
                BehaviacUnitDecisionAgent agent =
                    BehaviacRuntimeHost.CreateAgent<BehaviacUnitDecisionAgent>("rimclone_unit_decision");
                SetAgentFacts(agent, test);

                EBTStatus status = BehaviacRuntimeHost.Tick(agent);
                BehaviacUnitIntent directIntent = ResolveDirect(test);
                bool passed = status == EBTStatus.BT_SUCCESS &&
                    agent.Intent == test.Expected &&
                    directIntent == test.Expected;
                allPassed &= passed;

                Console.WriteLine(
                    $"Behaviac decision test: {(passed ? "PASS" : "FAIL")}; " +
                    $"{test.Name}; status={status}; intent={agent.Intent}; " +
                    $"direct={directIntent}; expected={test.Expected}");
            }

            BehaviacRuntimeHost.Shutdown();
            Console.WriteLine(
                $"Behaviac decision tests: {(allPassed ? "PASS" : "FAIL")}; " +
                $"{cases.Length} tree cases + tactical utility formula tests");
            return allPassed ? 0 : 1;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine($"Behaviac decision tests: FAIL; {exception}");
            try { BehaviacRuntimeHost.Shutdown(); } catch { }
            return 1;
        }
    }

    private static int RunDecisionBenchmark()
    {
        DecisionCase[] cases = GetDecisionCases();
        var agents = new BehaviacUnitDecisionAgent[BenchmarkUnits];
        string dataRoot = Path.Combine(AppContext.BaseDirectory, "BehaviacData");

        try
        {
            BehaviacRuntimeHost.Initialize(dataRoot, Workspace.EFileFormat.EFF_xml);

            for (int unit = 0; unit < agents.Length; unit++)
                agents[unit] = BehaviacRuntimeHost.CreateAgent<BehaviacUnitDecisionAgent>("rimclone_unit_decision");

            // Warm both paths, create all agents up front, and keep setup out of the measured section.
            for (int tick = 0; tick < WarmupTicks; tick++)
            {
                for (int unit = 0; unit < BenchmarkUnits; unit++)
                {
                    DecisionCase test = cases[(unit + tick) % cases.Length];
                    _ = ResolveDirect(test);

                    SetAgentFacts(agents[unit], test);
                    _ = BehaviacRuntimeHost.Tick(agents[unit]);
                }
            }

            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();

            long directChecksum = 0;
            var directWatch = Stopwatch.StartNew();
            for (int tick = 0; tick < BenchmarkTicks; tick++)
            {
                for (int unit = 0; unit < BenchmarkUnits; unit++)
                {
                    DecisionCase test = cases[(unit + tick) % cases.Length];
                    directChecksum += (int)ResolveDirect(test);
                }
            }
            directWatch.Stop();

            int mismatches = 0;
            string firstMismatch = string.Empty;
            long behaviacChecksum = 0;
            long allocatedBefore = GC.GetAllocatedBytesForCurrentThread();
            var behaviacWatch = Stopwatch.StartNew();

            for (int tick = 0; tick < BenchmarkTicks; tick++)
            {
                for (int unit = 0; unit < BenchmarkUnits; unit++)
                {
                    DecisionCase test = cases[(unit + tick) % cases.Length];
                    BehaviacUnitDecisionAgent agent = agents[unit];
                    SetAgentFacts(agent, test);

                    EBTStatus status = BehaviacRuntimeHost.Tick(agent);
                    behaviacChecksum += (int)agent.Intent;

                    if (status != EBTStatus.BT_SUCCESS || agent.Intent != test.Expected)
                    {
                        mismatches++;
                        if (firstMismatch.Length == 0)
                            firstMismatch = $"unit={unit}; tick={tick}; status={status}; intent={agent.Intent}; expected={test.Expected}";
                    }
                }
            }

            behaviacWatch.Stop();
            long allocatedBytes = GC.GetAllocatedBytesForCurrentThread() - allocatedBefore;

            long decisions = (long)BenchmarkUnits * BenchmarkTicks;
            double directNsPerDecision = directWatch.Elapsed.TotalMilliseconds * 1_000_000.0 / decisions;
            double behaviacNsPerDecision = behaviacWatch.Elapsed.TotalMilliseconds * 1_000_000.0 / decisions;
            double ratio = directWatch.Elapsed.TotalMilliseconds > 0
                ? behaviacWatch.Elapsed.TotalMilliseconds / directWatch.Elapsed.TotalMilliseconds
                : 0;

            bool passed = mismatches == 0 && directChecksum == behaviacChecksum;
            Console.WriteLine(
                $"Behaviac decision benchmark: {(passed ? "PASS" : "FAIL")}; " +
                $"units={BenchmarkUnits}; ticks={BenchmarkTicks}; decisions={decisions}; " +
                $"direct={directWatch.Elapsed.TotalMilliseconds:F2} ms ({directNsPerDecision:F0} ns/decision); " +
                $"behaviac={behaviacWatch.Elapsed.TotalMilliseconds:F2} ms ({behaviacNsPerDecision:F0} ns/decision); " +
                $"ratio={ratio:F2}x; allocated={allocatedBytes} B; " +
                $"mismatches={mismatches}; checksum={directChecksum}/{behaviacChecksum}");

            if (!passed && firstMismatch.Length > 0)
                Console.Error.WriteLine($"First Behaviac benchmark mismatch: {firstMismatch}");

            BehaviacRuntimeHost.Shutdown();
            return passed ? 0 : 1;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine($"Behaviac decision benchmark: FAIL; {exception}");
            try { BehaviacRuntimeHost.Shutdown(); } catch { }
            return 1;
        }
    }

    private static DecisionCase[] GetDecisionCases() =>
    [
        new("dead overrides every other fact", false, true, true, true, true, BehaviacUnitIntent.Dead, true, true),
        new("cover seeking overrides visible target and flank", true, true, true, true, true,
            BehaviacUnitIntent.SeekCover, true, false, CombatUtility: 0.90f, CoverUtility: 1f),
        new("low health prioritizes retreat over combat", true, false, false, true, true, BehaviacUnitIntent.RetreatToCover, true, false, true, false),
        new("flanker wins when its utility beats firing", true, false, false, true, false,
            BehaviacUnitIntent.FlankTarget, true, false, CombatUtility: 0.54f, FlankUtility: 0.75f),
        new("ongoing flank survives closing distance", true, false, false, true, false,
            BehaviacUnitIntent.FlankTarget, false, true, CombatUtility: 0.95f, FlankUtility: 0f),
        new("ordinary unit engages visible target", true, false, false, true, false, BehaviacUnitIntent.EngageTarget),
        new("prone position wins when its utility exceeds the entry threshold", true, false, false, true, false,
            BehaviacUnitIntent.ProneEngage, ProneCombatUtility: 0.99f, CombatUtility: 0.54f),
        new("prone loses when standing fire has better utility", true, false, false, true, false,
            BehaviacUnitIntent.EngageTarget, ProneCombatUtility: 0.60f, CombatUtility: 0.84f),
        new("already-prone unit stays prone while target remains visible", true, false, false, true, false,
            BehaviacUnitIntent.ProneEngage, ProneCombatUtility: 0.10f, IsProne: true),
        new("lost target with suppressive weapon uses fire support", true, false, false, false, true,
            BehaviacUnitIntent.SuppressiveFire, HasSuppressiveWeapon: true),
        new("recent target without suppressive weapon is investigated", true, false, false, false, true, BehaviacUnitIntent.InvestigateLastKnownTarget),
        new("no target falls back to patrol", true, false, false, false, false, BehaviacUnitIntent.Patrol),
        new("suppression without cover does not force cover intent", true, true, false, true, false, BehaviacUnitIntent.EngageTarget),
        new("suppression interrupts an ongoing flank", true, true, false, true, false, BehaviacUnitIntent.EngageTarget, true, true),
        new("attack order continues toward the last known objective", true, false, false, false, true,
            BehaviacUnitIntent.AttackOrder, HasSuppressiveWeapon: true, HasAttackOrder: true),
        new("attack order engages a newly visible threat", true, false, false, true, false,
            BehaviacUnitIntent.EngageTarget, HasAttackOrder: true),
        new("hold order fires without advancing on a visible hostile", true, false, false, true, false,
            BehaviacUnitIntent.HoldPositionEngage, HasStopOrder: true),
        new("hold order beats suppressive fire at a lost target", true, false, false, false, true,
            BehaviacUnitIntent.HoldPosition, HasSuppressiveWeapon: true, HasStopOrder: true),
        new("hold order can yield to suppression cover", true, true, true, true, false,
            BehaviacUnitIntent.SeekCover, HasStopOrder: true, CombatUtility: 0.68f, CoverUtility: 0.84f),
        new("held low-health unit may retreat when cover exists", true, false, true, true, true,
            BehaviacUnitIntent.RetreatToCover, HasStopOrder: true, IsLowHealth: true),
        new("held low-health unit stays held when no cover exists", true, false, false, true, true,
            BehaviacUnitIntent.HoldPositionEngage, HasStopOrder: true, IsLowHealth: true),
        new("move task wins when its utility beats visible combat", true, false, false, true, false,
            BehaviacUnitIntent.FollowMoveOrder, HasMoveOrder: true, MoveOrderUtility: 0.8f, CombatUtility: 0.4f),
        new("visible combat interrupts a low-utility move task", true, false, false, true, false,
            BehaviacUnitIntent.EngageTarget, HasMoveOrder: true, MoveOrderUtility: 0.2f, CombatUtility: 0.9f),
        new("unknown sound with awareness is investigated", true, false, false, false, false,
            BehaviacUnitIntent.InvestigateAudibleContact, HasAudibleContact: true, IsAlerted: true,
            InvestigationUtility: 0.70f),
        new("faint sound without alertness does not trigger investigation", true, false, false, false, false,
            BehaviacUnitIntent.Patrol, HasAudibleContact: true, InvestigationUtility: 0.70f),
        new("move order outranks an uncertain sound", true, false, false, false, false,
            BehaviacUnitIntent.FollowMoveOrder, HasMoveOrder: true, MoveOrderUtility: 0.8f,
            HasAudibleContact: true, IsAlerted: true, InvestigationUtility: 0.70f),
        new("hold order outranks an uncertain sound", true, false, false, false, false,
            BehaviacUnitIntent.HoldPosition, HasStopOrder: true, HasAudibleContact: true, IsAlerted: true,
            InvestigationUtility: 0.70f),
        new("identified last-known contact outranks an uncertain sound", true, false, false, false, true,
            BehaviacUnitIntent.InvestigateLastKnownTarget, HasAudibleContact: true, IsAlerted: true,
            InvestigationUtility: 0.70f),
        new("immediate danger selects cover when an option exists", true, false, true, false, false,
            BehaviacUnitIntent.SeekCover, HasImmediateDanger: true),
        new("immediate danger without a cover option does not invent a cover goal", true, false, false, false, false,
            BehaviacUnitIntent.Patrol, HasImmediateDanger: true)
    ];

    private static void SetAgentFacts(BehaviacUnitDecisionAgent agent, DecisionCase test)
    {
        agent.SetFacts(
            isAlive: test.IsAlive,
            isSuppressed: test.IsSuppressed,
            hasCover: test.HasCover,
            hasVisibleTarget: test.HasVisibleTarget,
            hasRecentTarget: test.HasRecentTarget,
            isLowHealth: test.IsLowHealth,
            hasSuppressiveWeapon: test.HasSuppressiveWeapon,
            canFlank: test.CanFlank,
            isFlanking: test.IsFlanking,
            proneCombatUtility: test.ProneCombatUtility,
            isProne: test.IsProne,
            hasMoveOrder: test.HasMoveOrder,
            hasStopOrder: test.HasStopOrder,
            hasAttackOrder: test.HasAttackOrder,
            moveOrderUtility: test.MoveOrderUtility,
            combatUtility: test.CombatUtility,
            hasAudibleContact: test.HasAudibleContact,
            isInImmediateDanger: test.HasImmediateDanger,
            isAlerted: test.IsAlerted,
            coverUtility: test.CoverUtility,
            flankUtility: test.FlankUtility,
            investigationUtility: test.InvestigationUtility);
    }

    private static BehaviacUnitIntent ResolveDirect(DecisionCase test)
    {
        if (!test.IsAlive)
            return BehaviacUnitIntent.Dead;

        if (test.HasCover &&
            (test.HasImmediateDanger ||
             (test.IsSuppressed && test.CoverUtility > test.CombatUtility)))
            return BehaviacUnitIntent.SeekCover;

        if (test.IsLowHealth && (test.HasVisibleTarget || test.HasRecentTarget) &&
            (!test.HasStopOrder || test.HasCover))
            return BehaviacUnitIntent.RetreatToCover;

        if (test.HasAttackOrder && !test.HasVisibleTarget)
            return BehaviacUnitIntent.AttackOrder;

        if (test.HasStopOrder)
            return test.HasVisibleTarget
                ? BehaviacUnitIntent.HoldPositionEngage
                : BehaviacUnitIntent.HoldPosition;

        if (test.HasMoveOrder && test.MoveOrderUtility >= test.CombatUtility)
            return BehaviacUnitIntent.FollowMoveOrder;

        bool flankWins = test.CanFlank &&
            test.FlankUtility > test.CombatUtility + UnitTacticalUtility.FlankUtilityAdvantage;
        if (test.HasVisibleTarget && !test.IsSuppressed &&
            (test.IsFlanking || flankWins))
            return BehaviacUnitIntent.FlankTarget;

        if (test.HasVisibleTarget)
        {
            bool shouldRemainProne =
                !test.IsSuppressed &&
                !test.IsLowHealth &&
                !test.IsFlanking &&
                (test.IsProne ||
                 (test.ProneCombatUtility >= 0.60f &&
                  test.ProneCombatUtility > test.CombatUtility &&
                  (!test.CanFlank ||
                   test.FlankUtility <= test.CombatUtility + UnitTacticalUtility.FlankUtilityAdvantage)));

            return shouldRemainProne
                ? BehaviacUnitIntent.ProneEngage
                : BehaviacUnitIntent.EngageTarget;
        }

        if (test.HasRecentTarget && test.HasSuppressiveWeapon)
            return BehaviacUnitIntent.SuppressiveFire;

        if (test.HasRecentTarget)
            return BehaviacUnitIntent.InvestigateLastKnownTarget;

        if (test.IsAlerted && test.HasAudibleContact && !test.IsSuppressed && !test.IsLowHealth &&
            test.InvestigationUtility >= UnitTacticalUtility.MinimumInvestigationUtility)
            return BehaviacUnitIntent.InvestigateAudibleContact;

        return BehaviacUnitIntent.Patrol;
    }

    private sealed record DecisionCase(
        string Name,
        bool IsAlive,
        bool IsSuppressed,
        bool HasCover,
        bool HasVisibleTarget,
        bool HasRecentTarget,
        BehaviacUnitIntent Expected,
        bool CanFlank = false,
        bool IsFlanking = false,
        bool IsLowHealth = false,
        bool HasSuppressiveWeapon = false,
        float ProneCombatUtility = 0f,
        bool IsProne = false,
        bool HasMoveOrder = false,
        bool HasStopOrder = false,
        bool HasAttackOrder = false,
        float MoveOrderUtility = 0f,
        float CombatUtility = 0f,
        bool HasAudibleContact = false,
        bool HasImmediateDanger = false,
        bool IsAlerted = false,
        float CoverUtility = 0f,
        float FlankUtility = 0f,
        float InvestigationUtility = 0f);
}

[TypeMetaInfo()]
internal sealed class CBTPlayer : Agent
{
    [MemberMetaInfo()]
    public uint m_iBaseSpeed;

    [MemberMetaInfo()]
    public int m_Frames;

    public int ConditionCalls { get; private set; }
    public int Action1Calls { get; private set; }
    public int Action3Calls { get; private set; }

    [MethodMetaInfo()]
    public bool Condition()
    {
        ConditionCalls++;
        m_iBaseSpeed = 0;
        m_Frames = 0;
        return true;
    }

    [MethodMetaInfo()]
    public EBTStatus Action1()
    {
        Action1Calls++;
        return EBTStatus.BT_SUCCESS;
    }

    [MethodMetaInfo()]
    public EBTStatus Action3()
    {
        Action3Calls++;
        m_Frames++;
        return m_Frames == 3 ? EBTStatus.BT_SUCCESS : EBTStatus.BT_RUNNING;
    }
}
