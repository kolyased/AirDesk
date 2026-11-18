using System;
using System.IO;
using System.Text.Json;

namespace AirDesk;

internal static class AtomicJson
{
    internal static readonly string Folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AirDesk");

    public static void RequireExternalDestination(string destination)
    {
        string relative = Path.GetRelativePath(Folder, destination);
        if (!Path.IsPathRooted(relative) && relative != ".." && !relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal))
            throw new IOException("Для экспорта выберите файл вне папки данных AirDesk.");
    }

    public static void Write<T>(string path, T value, bool backup = false) =>
        WriteFile(path, stream => JsonSerializer.Serialize(stream, value), backup);

    public static void WriteFile(string path, Action<Stream> write, bool backup = false)
    {
        path = Path.GetFullPath(path);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        string temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                write(stream);
                stream.Flush(flushToDisk: true);
            }
            if (backup && File.Exists(path)) File.Replace(temporary, path, path + ".bak", ignoreMetadataErrors: true);
            else File.Move(temporary, path, overwrite: true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}
