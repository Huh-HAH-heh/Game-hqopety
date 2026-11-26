using System;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;

namespace RimClone.Render.Raylib;

internal sealed class TerrainPipelineLogger : IDisposable
{
    private readonly Channel<string> _messages =
        Channel.CreateUnbounded<string>(new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = false,
            AllowSynchronousContinuations = false
        });

    private readonly Task _writerTask;
    private int _enabled;
    private int _mode;
    private int _disposed;
    private long _sequence;
    private long _frame;

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
            Enqueue($"SESSION_START mode={Mode} path=\"{LogPath}\" cwd=\"{Environment.CurrentDirectory}\"");
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
            ? $"LOGGING_ENABLED mode={Mode} path=\"{LogPath}\""
            : "LOGGING_DISABLED");
    }

    public void SetMode(RenderLogMode mode)
    {
        if (Volatile.Read(ref _disposed) != 0)
            return;
        if (!Enum.IsDefined(mode))
            mode = RenderLogMode.Detailed;

        int previous = Interlocked.Exchange(ref _mode, (int)mode);
        if (previous != (int)mode && Enabled)
            Enqueue($"LOG_MODE_CHANGED old={(RenderLogMode)previous} new={mode}");
    }

    public void Write(string category, string message) =>
        Write(RenderLogMode.Detailed, category, message);

    public void Write(RenderLogMode minimumMode, string category, string message)
    {
        if (!IsEnabled(minimumMode) || Volatile.Read(ref _disposed) != 0)
            return;
        Enqueue($"{category} {message}");
    }

    public void Error(string category, string message) =>
        Write(RenderLogMode.ErrorsOnly, category, message);

    private void Enqueue(string message)
    {
        long sequence = Interlocked.Increment(ref _sequence);
        long frame = Interlocked.Read(ref _frame);
        string line =
            $"{DateTimeOffset.Now:O} seq={sequence:D10} frame={frame} thread={Environment.CurrentManagedThreadId} {message}";
        if (!_messages.Writer.TryWrite(line))
            Console.Error.WriteLine($"[RENDER-LOG] Queue closed; event not written: {message}");
    }

    private async Task WriteLoopAsync()
    {
        StreamWriter? writer = null;
        try
        {
            await foreach (string line in _messages.Reader.ReadAllAsync())
            {
                if (writer == null)
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
                    writer = new StreamWriter(stream, new UTF8Encoding(false), 64 * 1024);
                }

                await writer.WriteLineAsync(line);
                bool stateChanged =
                    line.EndsWith("LOGGING_DISABLED", StringComparison.Ordinal) ||
                    line.Contains(" LOG_MODE_CHANGED ", StringComparison.Ordinal) ||
                    line.EndsWith("SESSION_END", StringComparison.Ordinal);
                if (stateChanged || (Interlocked.Read(ref _sequence) & 31) == 0)
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
        if (Enabled)
            Enqueue("SESSION_END");

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
