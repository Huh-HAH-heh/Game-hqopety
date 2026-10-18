using System;
using System.IO;
using behaviac;

namespace Core.AI.Behaviac;

/// <summary>
/// Owns Behaviac's process-wide workspace. This is opt-in infrastructure only;
/// the current UnitAiSystem does not call it yet.
/// </summary>
public static class BehaviacRuntimeHost
{
    private static readonly object Sync = new();
    private static string? _behaviorRoot;
    private static bool _initialized;

    public static bool IsInitialized
    {
        get
        {
            lock (Sync)
                return _initialized;
        }
    }

    public static void Initialize(string behaviorRoot)
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
                if (!string.Equals(_behaviorRoot, fullPath, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException("Behaviac workspace is already initialized with a different behavior directory.");
                return;
            }

            // Behaviac configuration must be set before Workspace first initializes.
            Config.IsLogging = false;
            Config.IsSocketing = false;
            Config.IsHotReload = false;
            Workspace.Instance.FilePath = fullPath;
            Workspace.Instance.FileFormat = Workspace.EFileFormat.EFF_xml;

            _behaviorRoot = fullPath;
            _initialized = true;
        }
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
