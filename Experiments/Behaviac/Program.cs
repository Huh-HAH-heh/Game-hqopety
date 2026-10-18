using behaviac;

internal static class Program
{
    private static int Main()
    {
        string dataRoot = Path.Combine(AppContext.BaseDirectory, "BehaviacData")
            .Replace('\\', '/');

        try
        {
            // Configure all global settings before the workspace is first initialized.
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
                $"Behaviac smoke test: {(passed ? "PASS" : "FAIL")}; " +
                $"status={status}; ticks={ticks}; " +
                $"conditions={agent.ConditionCalls}; " +
                $"action1={agent.Action1Calls}; action3={agent.Action3Calls}");

            Workspace.Instance.Cleanup();
            return passed ? 0 : 1;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine($"Behaviac smoke test: FAIL; {exception}");
            try
            {
                Workspace.Instance.Cleanup();
            }
            catch
            {
                // Preserve the original failure in the test output.
            }

            return 1;
        }
    }
}

[TypeMetaInfo()]
internal sealed class CBTPlayer : Agent
{
    private int _frames;

    public int ConditionCalls { get; private set; }
    public int Action1Calls { get; private set; }
    public int Action3Calls { get; private set; }

    [MethodMetaInfo()]
    public bool Condition()
    {
        ConditionCalls++;
        _frames = 0;
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
        _frames++;
        return _frames == 3 ? EBTStatus.BT_SUCCESS : EBTStatus.BT_RUNNING;
    }
}
