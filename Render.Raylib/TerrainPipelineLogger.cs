using System;
using System.IO;
using System.Threading.Channels;
using System.Threading.Tasks;

namespace RimClone.Render.Raylib;

/// <summary>
/// Bounded asynchronous terrain diagnostics sink. Rendering and worker threads
/// only enqueue short messages; disk I/O is performed by a dedicated writer.
/// </summary>
internal sealed class TerrainPipelineLogger : IDisposable
{
    private readonly Channel<string> _messages =
        Channel.CreateBounded<string>(new BoundedChannelOptions(2048)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = true,
            SingleWriter = false
        });

    private readonly Task _writerTask;
    private bool _disposed;

    public string LogPath { get; }

    public TerrainPipelineLogger()
    {
        string root = Environment.GetFolderPath(
            Environment.SpecialFolder.LocalApplicationData);
        if (string.IsNullOrWhiteSpace(root))
            root = AppContext.BaseDirectory;

        LogPath = Path.Combine(
            root, "RimClone", "logs", "terrain-pipeline.log");
        _writerTask = Task.Run(WriteLoopAsync);
        Write("LOGGER_START", $"path=\"{LogPath}\"");
    }

    public void Write(string category, string message)
    {
        if (_disposed)
            return;

        string line =
            $"{DateTimeOffset.UtcNow:O} [{category}] {message}";
        _messages.Writer.TryWrite(line);
    }

    private async Task WriteLoopAsync()
    {
        try
        {
            string? directory = Path.GetDirectoryName(LogPath);
            if (!string.IsNullOrWhiteSpace(directory))
                Directory.CreateDirectory(directory);

            await using FileStream stream = new(
                LogPath,
                FileMode.Append,
                FileAccess.Write,
                FileShare.ReadWrite,
                16 * 1024,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            await using StreamWriter writer = new(stream);

            int pending = 0;
            await foreach (string line in _messages.Reader.ReadAllAsync())
            {
                await writer.WriteLineAsync(line);
                pending++;
                if (pending < 16)
                    continue;

                await writer.FlushAsync();
                pending = 0;
            }

            await writer.FlushAsync();
        }
        catch (Exception exception)
        {
            Console.WriteLine(
                $"[TERRAIN-LOG] Could not write diagnostics file: {exception.Message}");
        }
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;
        _messages.Writer.TryComplete();
        try
        {
            _writerTask.GetAwaiter().GetResult();
        }
        catch (Exception exception)
        {
            Console.WriteLine(
                $"[TERRAIN-LOG] Logger shutdown failed: {exception.Message}");
        }
    }
}
