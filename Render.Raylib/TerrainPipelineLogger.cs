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

    private enum LogTarget : byte { Summary, Detail, Error }
    private readonly record struct LogRecord(LogTarget Target, string Line);

    private readonly Channel<LogRecord> _messages =
        Channel.CreateBounded<LogRecord>(new BoundedChannelOptions(QueueCapacity)
        {
            SingleReader = true,
            SingleWriter = false,
            FullMode = BoundedChannelFullMode.Wait,
            AllowSynchronousContinuations = false
        });

    private readonly ConcurrentQueue<LogRecord> _critical = new();
    private readonly ConcurrentDictionary<string, byte> _reportedCapacityErrors = new(StringComparer.Ordinal);
    private int _suppressedCapacityErrors;
    private StreamWriter? _summaryWriter;
    private StreamWriter? _detailWriter;
    private StreamWriter? _errorWriter;
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
    public string DetailLogPath { get; }
    public string ErrorLogPath { get; }
    public bool Enabled => Volatile.Read(ref _enabled) != 0;
    public RenderLogMode Mode => (RenderLogMode)Volatile.Read(ref _mode);

    public TerrainPipelineLogger(bool enabled, RenderLogMode mode)
    {
        _enabled = enabled ? 1 : 0;
        _mode = Enum.IsDefined(mode) ? (int)mode : (int)RenderLogMode.Detailed;

        string root = ResolveProjectRoot();
        string directory = Path.Combine(root, "Logs");
        string fileName = "render-" +
            DateTimeOffset.Now.ToString("yyyyMMdd-HHmmss-fff", CultureInfo.InvariantCulture);
        LogPath = Path.Combine(directory, fileName + "-summary.log");
        DetailLogPath = Path.Combine(directory, fileName + "-detail.log");
        ErrorLogPath = Path.Combine(directory, fileName + "-errors.log");

        _writerTask = Task.Run(WriteLoopAsync);
        if (enabled)
            Enqueue("SESSION_START", $"utc={DateTimeOffset.UtcNow:O} mode={Mode} summary=\"{LogPath}\" detail=\"{DetailLogPath}\" errors=\"{ErrorLogPath}\"", critical: true);
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
            $"mode={Mode} summary=\"{LogPath}\" detail=\"{DetailLogPath}\" errors=\"{ErrorLogPath}\"",
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

        message ??= string.Empty;
        if (message.Contains("No reusable GPU page range", StringComparison.Ordinal))
        {
            int end = message.IndexOf(" retryFrame=", StringComparison.Ordinal);
            if (end < 0) end = message.IndexOf(" error=", StringComparison.Ordinal);
            if (end < 0) end = message.Length;
            string signature = category + "|" + message[..end];
            if (!_reportedCapacityErrors.TryAdd(signature, 0))
            {
                Interlocked.Increment(ref _suppressedCapacityErrors);
                return;
            }
        }

        string shortMessage = StripStackTrace(message);
        Enqueue(category, shortMessage, critical: true, LogTarget.Error);
        if (shortMessage.Length < message.Length)
            Enqueue(category + "_TRACE", message, critical: false, LogTarget.Detail);
    }

    private static string StripStackTrace(string message)
    {
        int stackStart = message.IndexOf("     at ", StringComparison.Ordinal);
        if (stackStart < 0)
            stackStart = message.IndexOf("\r\n", StringComparison.Ordinal);
        if (stackStart < 0)
            stackStart = message.IndexOf('\n');
        return stackStart > 0 ? message[..stackStart] : message;
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

    private void Enqueue(string category, string message, bool critical) =>
        Enqueue(category, message, critical, GetTarget(category));

    private void Enqueue(string category, string message, bool critical, LogTarget target)
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

        LogRecord record = new(target, line);
        if (!_messages.Writer.TryWrite(record))
        {
            if (critical)
                _critical.Enqueue(record);
            else
                Interlocked.Increment(ref _dropped);

            if (critical)
                _messages.Writer.TryWrite(new LogRecord(LogTarget.Summary, string.Empty));
        }
    }

    private static LogTarget GetTarget(string category) => category switch
    {
        "SESSION_START" or "SESSION_END" or "LOGGING_ENABLED" or "LOGGING_DISABLED" or
        "LOG_MODE" or "VIEWER_START" or "WORLD_SNAPSHOT_READY" or "WINDOW_CONFIG" or
        "WINDOW_READY" or "RENDERER_START" or "VOXEL_RENDERER_READY" or
        "TERRAIN_SYNCHRONIZE" or "TERRAIN_RENDERER_CREATED" or "VOXEL_WORLD_SNAPSHOT" or
        "VOXEL_LOD_INITIAL" or "VOXEL_LOD_PREPARE" or "VOXEL_LOD_READY_SWITCH" or
        "VOXEL_LOD_SWITCHED" or "VOXEL_LOD_CANCEL" or "FRAME" or "VOXEL_FRAME" or
        "FRAME_PHASES" or "TERRAIN_DRAW_END" or "RENDER_PASS" or "FRAME_SUMMARY" or
        "WORLD_SYNC" => LogTarget.Summary,
        _ => LogTarget.Detail
    };

    private async Task WriteLoopAsync()
    {
        int written = 0;
        try
        {
            await foreach (LogRecord record in _messages.Reader.ReadAllAsync())
            {
                while (_critical.TryDequeue(out LogRecord criticalRecord))
                {
                    await GetWriter(criticalRecord.Target).WriteLineAsync(criticalRecord.Line);
                    written++;
                }

                if (record.Line.Length > 0)
                {
                    await GetWriter(record.Target).WriteLineAsync(record.Line);
                    written++;
                }

                written += await WriteDroppedSummaryAsync();
                if ((written & 127) == 0)
                    await FlushWritersAsync();
            }
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine($"[RENDER-LOG] Could not write logs: {exception}");
        }
        finally
        {
            try
            {
                while (_critical.TryDequeue(out LogRecord criticalRecord))
                    await GetWriter(criticalRecord.Target).WriteLineAsync(criticalRecord.Line);

                written += await WriteDroppedSummaryAsync();
                await FlushWritersAsync();
                await DisposeWritersAsync();
            }
            catch (Exception exception)
            {
                Console.Error.WriteLine($"[RENDER-LOG] Flush failed: {exception}");
            }
        }
    }

    private StreamWriter GetWriter(LogTarget target)
    {
        return target switch
        {
            LogTarget.Summary => _summaryWriter ??= CreateWriter(LogPath),
            LogTarget.Detail => _detailWriter ??= CreateWriter(DetailLogPath),
            _ => _errorWriter ??= CreateWriter(ErrorLogPath)
        };
    }

    private static StreamWriter CreateWriter(string path)
    {
        string? directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrWhiteSpace(directory))
            Directory.CreateDirectory(directory);

        FileStream stream = new(
            path,
            FileMode.Append,
            FileAccess.Write,
            FileShare.ReadWrite,
            64 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);

        return new StreamWriter(stream, new UTF8Encoding(false), 64 * 1024);
    }

    private async Task<int> WriteDroppedSummaryAsync()
    {
        int dropped = Interlocked.Exchange(ref _dropped, 0);
        if (dropped <= 0)
            return 0;

        long sequence = Interlocked.Increment(ref _sequence);
        string line = $"{_clock.ElapsedMilliseconds,8}ms f{Interlocked.Read(ref _frame):D7} t{Environment.CurrentManagedThreadId:D2} #{sequence:D7} LOG_DROPPED count={dropped}";
        await GetWriter(LogTarget.Summary).WriteLineAsync(line);
        return 1;
    }

    private async Task FlushWritersAsync()
    {
        if (_summaryWriter != null) await _summaryWriter.FlushAsync();
        if (_detailWriter != null) await _detailWriter.FlushAsync();
        if (_errorWriter != null) await _errorWriter.FlushAsync();
    }

    private async Task DisposeWritersAsync()
    {
        if (_summaryWriter != null) await _summaryWriter.DisposeAsync();
        if (_detailWriter != null) await _detailWriter.DisposeAsync();
        if (_errorWriter != null) await _errorWriter.DisposeAsync();
        _summaryWriter = null;
        _detailWriter = null;
        _errorWriter = null;
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
        int suppressedCapacityErrors = Interlocked.Exchange(ref _suppressedCapacityErrors, 0);
        if (suppressedCapacityErrors > 0)
            Enqueue("ERRORS_SUPPRESSED", $"gpuPageCapacity duplicateRetries={suppressedCapacityErrors}", critical: true, LogTarget.Error);
        if (Enabled)
            Enqueue("SESSION_END", $"frame={Interlocked.Read(ref _frame)}", critical: true, LogTarget.Summary);

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
