using System;
using System.IO;
using System.Collections.Generic;
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

            _behaviorRoot = fullPath;
            _fileFormat = fileFormat;
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
