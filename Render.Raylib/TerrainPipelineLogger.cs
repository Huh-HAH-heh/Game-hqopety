using System;
using System.IO;

namespace RimClone.Render.Raylib;

/// <summary>
/// Small synchronous diagnostics writer. It never creates a background task.
/// </summary>
internal sealed class TerrainPipelineLogger : IDisposable
{
    private readonly StreamWriter? _writer;
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

        try
        {
            string? directory = Path.GetDirectoryName(LogPath);
            if (!string.IsNullOrWhiteSpace(directory))
                Directory.CreateDirectory(directory);

            FileStream stream = new(
                LogPath,
                FileMode.Append,
                FileAccess.Write,
                FileShare.ReadWrite,
                16 * 1024,
                FileOptions.SequentialScan);
            _writer = new StreamWriter(stream);
        }
        catch (Exception exception)
        {
            Console.WriteLine(
                $"[TERRAIN-LOG] Could not open diagnostics file: {exception.Message}");
        }

        Write("LOGGER_START", $"path=\"{LogPath}\"");
    }

    public void Write(string category, string message)
    {
        if (_disposed || _writer == null)
            return;

        try
        {
            _writer.WriteLine(
                $"{DateTimeOffset.UtcNow:O} [{category}] {message}");
        }
        catch (IOException exception)
        {
            Console.WriteLine(
                $"[TERRAIN-LOG] Write failed: {exception.Message}");
        }
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;
        try
        {
            _writer?.Flush();
            _writer?.Dispose();
        }
        catch (IOException exception)
        {
            Console.WriteLine(
                $"[TERRAIN-LOG] Logger shutdown failed: {exception.Message}");
        }
    }
}
