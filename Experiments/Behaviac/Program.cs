using System;
using behaviac;
using Core.AI.Behaviac;

internal static class Program
{
    private static int Main(string[] args)
    {
        return Array.Exists(args, arg => string.Equals(arg, "--decision-test", StringComparison.Ordinal))
            ? RunDecisionTests()
            : RunXmlSmokeTest();
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
        var cases = new[]
        {
            new DecisionCase("dead overrides every other fact", false, true, true, true, true, BehaviacUnitIntent.Dead),
            new DecisionCase("cover seeking overrides visible target", true, true, true, true, true, BehaviacUnitIntent.SeekCover),
            new DecisionCase("visible target engages", true, false, false, true, false, BehaviacUnitIntent.EngageTarget),
            new DecisionCase("recent target is investigated", true, false, false, false, true, BehaviacUnitIntent.InvestigateLastKnownTarget),
            new DecisionCase("no target falls back to patrol", true, false, false, false, false, BehaviacUnitIntent.Patrol),
            new DecisionCase("suppression without cover does not force cover intent", true, true, false, true, false, BehaviacUnitIntent.EngageTarget)
        };

        bool allPassed = true;
        try
        {
            BehaviacRuntimeHost.Initialize(AppContext.BaseDirectory, Workspace.EFileFormat.EFF_cs);
            foreach (DecisionCase test in cases)
            {
                BehaviacUnitDecisionAgent agent =
                    BehaviacRuntimeHost.CreateAgent<BehaviacUnitDecisionAgent>("rimclone_unit_decision");
                agent.SetFacts(test.IsAlive, test.IsSuppressed, test.HasCover,
                    test.HasVisibleTarget, test.HasRecentTarget);

                EBTStatus status = BehaviacRuntimeHost.Tick(agent);
                bool passed = status == EBTStatus.BT_SUCCESS && agent.Intent == test.Expected;
                allPassed &= passed;
                Console.WriteLine(
                    $"Behaviac decision test: {(passed ? "PASS" : "FAIL")}; " +
                    $"{test.Name}; status={status}; intent={agent.Intent}; expected={test.Expected}");
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

    private sealed record DecisionCase(
        string Name,
        bool IsAlive,
        bool IsSuppressed,
        bool HasCover,
        bool HasVisibleTarget,
        bool HasRecentTarget,
        BehaviacUnitIntent Expected);
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
