using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;

namespace RimClone.Render.Raylib;

internal sealed class TerrainPipelineLogger : IDisposable
{
    private const int QueueCapacity = 8192;
    private const int NormalMessageLimit = 2048;
    private const int ErrorMessageLimit = 8192;

    private readonly Channel<string> _messages =
        Channel.CreateBounded<string>(new BoundedChannelOptions(QueueCapacity)
        {
            SingleReader = true,
            SingleWriter = false,
            FullMode = BoundedChannelFullMode.Wait,
            AllowSynchronousContinuations = false
        });

    private readonly ConcurrentQueue<string> _critical = new();
    private readonly ConcurrentDictionary<string, RateState> _rates = new(StringComparer.Ordinal);
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private readonly Task _writerTask;
    private int _enabled;
    private int _mode;
    private int _disposed;
    private int _dropped;
    private long _sequence;
    private long _frame;

    private sealed class RateState
    {
        public long WindowStartMs;
        public int Written;
        public int Suppressed;
    }

    public string LogPath { get; }
    public bool Enabled => Volatile.Read(ref _enabled) != 0;
    public RenderLogMode Mode => (RenderLogMode)Volatile.Read(ref _mode);

    public TerrainPipelineLogger(bool enabled, RenderLogMode mode)
    {
        _enabled = enabled ? 1 : 0;
        _mode = Enum.IsDefined(mode) ? (int)mode : (int)RenderLogMode.Detailed;

        string root = ResolveProjectRoot();
        string directory = Path.Combine(root, "Logs");
        string fileName = "render-" +
            DateTimeOffset.Now.ToString("yyyyMMdd-HHmmss-fff", CultureInfo.InvariantCulture) +
            ".log";
        LogPath = Path.Combine(directory, fileName);

        _writerTask = Task.Run(WriteLoopAsync);
        if (enabled)
            Enqueue("SESSION_START", $"utc={DateTimeOffset.UtcNow:O} mode={Mode} cwd=\"{Environment.CurrentDirectory}\"", critical: true);
    }

    public void SetFrame(long frame) => Interlocked.Exchange(ref _frame, frame);

    public bool IsEnabled(RenderLogMode minimumMode) =>
        Enabled && (int)Mode >= (int)minimumMode;

    public void SetEnabled(bool enabled)
    {
        if (Volatile.Read(ref _disposed) != 0)
            return;

        int next = enabled ? 1 : 0;
        int previous = Interlocked.Exchange(ref _enabled, next);
        if (previous == next)
            return;

        Enqueue(enabled
            ? "LOGGING_ENABLED"
            : "LOGGING_DISABLED",
            $"mode={Mode} path=\"{LogPath}\"",
            critical: !enabled);
    }

    public void SetMode(RenderLogMode mode)
    {
        if (Volatile.Read(ref _disposed) != 0)
            return;
        if (!Enum.IsDefined(mode))
            mode = RenderLogMode.Detailed;

        int previous = Interlocked.Exchange(ref _mode, (int)mode);
        if (previous != (int)mode && Enabled)
            Enqueue("LOG_MODE", $"old={(RenderLogMode)previous} new={mode}", critical: false);
    }

    public void Write(string category, string message) =>
        Write(RenderLogMode.Detailed, category, message);

    public void Write(RenderLogMode minimumMode, string category, string message)
    {
        if (!IsEnabled(minimumMode) || Volatile.Read(ref _disposed) != 0)
            return;
        if (!ShouldWrite(minimumMode, category))
            return;

        Enqueue(category, message, critical: false);
    }

    public void Error(string category, string message)
    {
        if (!Enabled || Volatile.Read(ref _disposed) != 0)
            return;
        Enqueue(category, message, critical: true);
    }

    private bool ShouldWrite(RenderLogMode minimumMode, string category)
    {
        int limit;
        if (minimumMode == RenderLogMode.FrameSummary || IsPerSecondSummary(category))
        {
            limit = 1;
        }
        else if (IsBurstCategory(category))
        {
            limit = Mode == RenderLogMode.Trace ? 4 : 2;
        }
        else if (minimumMode == RenderLogMode.Trace)
        {
            limit = 4;
        }
        else
        {
            return true;
        }

        RateState state = _rates.GetOrAdd(category, static _ => new RateState());
        long now = _clock.ElapsedMilliseconds;
        long windowStart = Volatile.Read(ref state.WindowStartMs);

        if (now - windowStart >= 1000 &&
            Interlocked.CompareExchange(ref state.WindowStartMs, now, windowStart) == windowStart)
        {
            int suppressed = Interlocked.Exchange(ref state.Suppressed, 0);
            Interlocked.Exchange(ref state.Written, 0);
            // Per-second summaries are intentionally sampled; logging their suppressed
            // repetitions only duplicates noise. Keep suppression reports for burst diagnostics.
            if (suppressed > 0 &&
                (IsBurstCategory(category) || Mode == RenderLogMode.Trace))
            {
                Enqueue("SUPPRESSED", $"category={category} count={suppressed}/s", critical: false);
            }
        }

        if (Interlocked.Increment(ref state.Written) <= limit)
            return true;

        Interlocked.Increment(ref state.Suppressed);
        return false;
    }

    private static bool IsPerSecondSummary(string category) => category switch
    {
        "FRAME" or "VOXEL_FRAME" or "TERRAIN_DRAW_END" or
        "FRAME_PHASES" or "RENDER_PASS" or "VOXEL_VISIBILITY" or
        "FRAME_SUMMARY" or "WORLD_SYNC" => true,
        _ => false
    };

    private static bool IsBurstCategory(string category) => category switch
    {
        "MESH_BUILD_REQUEST" or "MESH_BUILD_STARTED" or "MESH_BUILD_COMPLETED" or
        "MESH_BUILD_CANCELLED" or "MESH_COMPLETION_CANCELLED" or
        "MESH_RESULT_STALE" or "MESH_RESULT_ACCEPTED" or "MESH_BUILD_QUEUE_FULL" or
        "MESH_GPU_EVICT" or "GPU_MESH_UPLOAD" or "GPU_MESH_UNLOAD" or
        "REGION_MESH_REBUILT" or "REGION_CUT_MESH_REBUILT" or "FRAGMENT_GPU_UPLOAD" => true,
        _ => false
    };

    private void Enqueue(string category, string message, bool critical)
    {
        int maxLength = critical ? ErrorMessageLimit : NormalMessageLimit;
        string safeMessage = (message ?? string.Empty).Replace('\r', ' ').Replace('\n', ' ');
        if (safeMessage.Length > maxLength)
            safeMessage = safeMessage[..maxLength] + "...[truncated]";

        long sequence = Interlocked.Increment(ref _sequence);
        long frame = Interlocked.Read(ref _frame);
        string line = string.Create(
            CultureInfo.InvariantCulture,
            $"{_clock.ElapsedMilliseconds,8}ms f{frame:D7} t{Environment.CurrentManagedThreadId:D2} #{sequence:D7} {category} {safeMessage}");

        if (!_messages.Writer.TryWrite(line))
        {
            if (critical)
                _critical.Enqueue(line);
            else
                Interlocked.Increment(ref _dropped);

            if (critical)
                _messages.Writer.TryWrite(string.Empty);
        }
    }

    private async Task WriteLoopAsync()
    {
        StreamWriter? writer = null;
        int written = 0;

        try
        {
            await foreach (string line in _messages.Reader.ReadAllAsync())
            {
                writer ??= CreateWriter();

                while (_critical.TryDequeue(out string? criticalLine))
                {
                    await writer.WriteLineAsync(criticalLine);
                    written++;
                }

                if (line.Length > 0)
                {
                    await writer.WriteLineAsync(line);
                    written++;
                }

                WriteDroppedSummary(writer, ref written);
                if ((written & 127) == 0)
                    await writer.FlushAsync();
            }
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine($"[RENDER-LOG] Could not write '{LogPath}': {exception}");
        }
        finally
        {
            if (writer != null)
            {
                try
                {
                    while (_critical.TryDequeue(out string? criticalLine))
                        await writer.WriteLineAsync(criticalLine);

                    WriteDroppedSummary(writer, ref written);
                    await writer.FlushAsync();
                    await writer.DisposeAsync();
                }
                catch (Exception exception)
                {
                    Console.Error.WriteLine($"[RENDER-LOG] Flush failed: {exception}");
                }
            }
        }
    }

    private StreamWriter CreateWriter()
    {
        string? directory = Path.GetDirectoryName(LogPath);
        if (!string.IsNullOrWhiteSpace(directory))
            Directory.CreateDirectory(directory);

        FileStream stream = new(
            LogPath,
            FileMode.Append,
            FileAccess.Write,
            FileShare.ReadWrite,
            64 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);

        return new StreamWriter(stream, new UTF8Encoding(false), 64 * 1024);
    }

    private void WriteDroppedSummary(StreamWriter writer, ref int written)
    {
        int dropped = Interlocked.Exchange(ref _dropped, 0);
        if (dropped <= 0)
            return;

        long sequence = Interlocked.Increment(ref _sequence);
        string line = $"{_clock.ElapsedMilliseconds,8}ms f{Interlocked.Read(ref _frame):D7} t{Environment.CurrentManagedThreadId:D2} #{sequence:D7} LOG_DROPPED count={dropped}";
        writer.WriteLine(line);
        written++;
    }

    private void FlushSuppressed()
    {
        foreach (KeyValuePair<string, RateState> pair in _rates)
        {
            int suppressed = Interlocked.Exchange(ref pair.Value.Suppressed, 0);
            if (suppressed > 0)
                Enqueue("SUPPRESSED", $"category={pair.Key} count={suppressed}/s final", critical: false);
        }
    }

    private static string ResolveProjectRoot()
    {
        string[] starts = { AppContext.BaseDirectory, Environment.CurrentDirectory };
        foreach (string start in starts)
        {
            DirectoryInfo? current = new(Path.GetFullPath(start));
            while (current != null)
            {
                string path = current.FullName;
                if (Directory.Exists(Path.Combine(path, ".git")) ||
                    File.Exists(Path.Combine(path, ".git")) ||
                    File.Exists(Path.Combine(path, "RimClone.sln")) ||
                    File.Exists(Path.Combine(path, "RimClone.slnx")))
                    return path;
                current = current.Parent;
            }
        }

        return Path.GetFullPath(Environment.CurrentDirectory);
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;

        FlushSuppressed();
        if (Enabled)
            Enqueue("SESSION_END", $"frame={Interlocked.Read(ref _frame)}", critical: true);

        _messages.Writer.TryComplete();
        try
        {
            _writerTask.GetAwaiter().GetResult();
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine($"[RENDER-LOG] Shutdown failed: {exception}");
        }
    }
}
