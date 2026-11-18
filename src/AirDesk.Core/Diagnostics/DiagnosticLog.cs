using System;
using System.Diagnostics;
using System.IO;

namespace AirDesk;

internal static class DiagnosticLog
{
    static readonly object gate = new();

    public static void Write(string message)
    {
        lock (gate)
        {
            try
            {
                Directory.CreateDirectory(AtomicJson.Folder);
                string file = Path.Combine(AtomicJson.Folder, "cooling.log");
                var info = new FileInfo(file);
                if (info.Exists && info.Length > 1_000_000) File.Move(file, file + ".old", true);
                File.AppendAllText(file, $"{DateTimeOffset.Now:O} {message}{Environment.NewLine}");
            }
            catch (IOException error) { Trace.TraceError(error.Message); }
            catch (UnauthorizedAccessException error) { Trace.TraceError(error.Message); }
        }
    }
}
