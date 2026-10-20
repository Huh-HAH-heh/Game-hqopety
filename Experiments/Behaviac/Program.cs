using System;
using System.Diagnostics;
using behaviac;
using Core.AI.Behaviac;

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

    private static int RunDecisionTests()
    {
        DecisionCase[] cases = GetDecisionCases();
        bool allPassed = true;

        try
        {
            string dataRoot = Path.Combine(AppContext.BaseDirectory, "BehaviacData");
            BehaviacRuntimeHost.Initialize(dataRoot, Workspace.EFileFormat.EFF_xml);

            foreach (DecisionCase test in cases)
            {
                BehaviacUnitDecisionAgent agent =
                    BehaviacRuntimeHost.CreateAgent<BehaviacUnitDecisionAgent>("rimclone_unit_decision");
                agent.SetFacts(test.IsAlive, test.IsSuppressed, test.HasCover,
                    test.HasVisibleTarget, test.HasRecentTarget, test.IsLowHealth,
                    test.HasSuppressiveWeapon, test.CanFlank, test.IsFlanking,
                    test.ProneCombatUtility, test.IsProne, test.HasMoveOrder,
                    test.HasStopOrder, test.HasAttackOrder, test.MoveOrderUtility,
                    test.CombatUtility);

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
            Console.WriteLine($"Behaviac decision tests: {(allPassed ? "PASS" : "FAIL")}; {cases.Length} cases");
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

                    agents[unit].SetFacts(test.IsAlive, test.IsSuppressed, test.HasCover,
                        test.HasVisibleTarget, test.HasRecentTarget, test.IsLowHealth,
                        test.HasSuppressiveWeapon, test.CanFlank, test.IsFlanking,
                        test.ProneCombatUtility, test.IsProne, test.HasMoveOrder,
                        test.HasStopOrder, test.HasAttackOrder, test.MoveOrderUtility,
                        test.CombatUtility);
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
                    agent.SetFacts(test.IsAlive, test.IsSuppressed, test.HasCover,
                        test.HasVisibleTarget, test.HasRecentTarget, test.IsLowHealth,
                        test.HasSuppressiveWeapon, test.CanFlank, test.IsFlanking, test.ProneCombatUtility, test.IsProne);

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
        new("cover seeking overrides visible target and flank", true, true, true, true, true, BehaviacUnitIntent.SeekCover, true, false),
        new("low health prioritizes retreat over combat", true, false, false, true, true, BehaviacUnitIntent.RetreatToCover, true, false, true, false),
        new("flanker selects a flank maneuver", true, false, false, true, false, BehaviacUnitIntent.FlankTarget, true, false),
        new("ongoing flank survives closing distance", true, false, false, true, false, BehaviacUnitIntent.FlankTarget, false, true),
        new("ordinary unit engages visible target", true, false, false, true, false, BehaviacUnitIntent.EngageTarget),
        new("exposed unit chooses a prone firing position", true, false, false, true, false, BehaviacUnitIntent.ProneEngage, ProneCombatUtility: 0.75f),
        new("low prone utility keeps a normal firing posture", true, false, false, true, false, BehaviacUnitIntent.EngageTarget, ProneCombatUtility: 0.40f),
        new("already-prone unit keeps crawling as the target closes", true, false, false, true, false, BehaviacUnitIntent.ProneEngage, ProneCombatUtility: 0.10f, IsProne: true),
        new("lost target with suppressive weapon uses fire support", true, false, false, false, true, BehaviacUnitIntent.SuppressiveFire, false, false, false, true),
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
            BehaviacUnitIntent.SeekCover, HasStopOrder: true),
        new("held low-health unit may retreat when cover exists", true, false, true, true, true,
            BehaviacUnitIntent.RetreatToCover, HasStopOrder: true, IsLowHealth: true),
        new("held low-health unit stays held when no cover exists", true, false, false, true, true,
            BehaviacUnitIntent.HoldPositionEngage, HasStopOrder: true, IsLowHealth: true),
        new("move task wins when its utility beats visible combat", true, false, false, true, false,
            BehaviacUnitIntent.FollowMoveOrder, HasMoveOrder: true, MoveOrderUtility: 0.8f, CombatUtility: 0.4f),
        new("visible combat interrupts a low-utility move task", true, false, false, true, false,
            BehaviacUnitIntent.EngageTarget, HasMoveOrder: true, MoveOrderUtility: 0.2f, CombatUtility: 0.9f)
    ];

    private static BehaviacUnitIntent ResolveDirect(DecisionCase test)
    {
        if (!test.IsAlive)
            return BehaviacUnitIntent.Dead;

        if (test.IsSuppressed && test.HasCover)
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

        if (test.HasVisibleTarget && !test.IsSuppressed && (test.CanFlank || test.IsFlanking))
            return BehaviacUnitIntent.FlankTarget;

        if (test.HasVisibleTarget)
        {
            if (!test.IsSuppressed &&
                !test.IsLowHealth &&
                !test.CanFlank &&
                !test.IsFlanking &&
                (test.ProneCombatUtility >= 0.60f || test.IsProne))
            {
                return BehaviacUnitIntent.ProneEngage;
            }

            return BehaviacUnitIntent.EngageTarget;
        }

        if (test.HasRecentTarget && test.HasSuppressiveWeapon)
            return BehaviacUnitIntent.SuppressiveFire;

        if (test.HasRecentTarget)
            return BehaviacUnitIntent.InvestigateLastKnownTarget;

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
        float CombatUtility = 0f);
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
