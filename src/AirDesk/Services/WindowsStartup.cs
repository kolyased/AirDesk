using System;
using System.IO;
using Microsoft.Win32;

namespace AirDesk;

internal static class WindowsStartup
{
    const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    const string Name = "AirDesk";

    public static string? Command => Environment.ProcessPath is { } path &&
        Path.GetFileName(path).Equals("AirDesk.exe", StringComparison.OrdinalIgnoreCase) ? $"\"{path}\" --autostart" : null;

    public static string? Read()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKey);
        return key?.GetValue(Name) as string;
    }

    public static void Set(bool enabled)
    {
        if (!enabled)
        {
            using var existing = Registry.CurrentUser.OpenSubKey(RunKey, writable: true);
            existing?.DeleteValue(Name, throwOnMissingValue: false);
            return;
        }
        string command = Command ?? throw new InvalidOperationException("Откройте AirDesk.exe, чтобы настроить автозапуск.");
        if (command.Length > 260) throw new InvalidOperationException("Путь к AirDesk.exe слишком длинный для автозапуска. Переместите приложение ближе к корню диска.");
        using var key = Registry.CurrentUser.CreateSubKey(RunKey) ?? throw new InvalidOperationException("Windows не разрешила изменить автозапуск.");
        key.SetValue(Name, command, RegistryValueKind.String);
    }
}
