using System;
using System.Diagnostics;
using System.IO;
using System.Security.Principal;
using Microsoft.Win32;

namespace AirDesk;

internal enum DriverRegistration { Missing, FileUnavailable, Registered, Unknown }

internal sealed record DriverStatus(DriverRegistration Registration, string? Version, bool Elevated)
{
    public string Description => Registration switch
    {
        DriverRegistration.Missing => "PawnIO не зарегистрирован. Доступные датчики продолжают работать.",
        DriverRegistration.FileUnavailable => "Служба PawnIO зарегистрирована, но файл драйвера или его версия недоступны.",
        DriverRegistration.Registered => $"PawnIO {Version}: обнаружены служба и файл. Доступ к датчикам проверяется при опросе.",
        _ => "AirDesk не смог прочитать сведения о PawnIO."
    };

    public static bool IsElevated()
    {
        using var identity = WindowsIdentity.GetCurrent();
        return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
    }

    public static DriverStatus Read()
    {
        bool elevated = false;
        try
        {
            elevated = IsElevated();
            using var service = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Services\PawnIO");
            if (service == null) return new(DriverRegistration.Missing, null, elevated);
            string? image = service.GetValue("ImagePath") as string;
            if (image == null) return new(DriverRegistration.FileUnavailable, null, elevated);

            string path = Environment.ExpandEnvironmentVariables(image.Trim().Trim('"'));
            const string root = @"\SystemRoot\";
            if (path.StartsWith(root, StringComparison.OrdinalIgnoreCase))
                path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), path[root.Length..]);
            if (path.StartsWith(@"\??\", StringComparison.Ordinal)) path = path[4..];
            string? version = Path.IsPathFullyQualified(path) && File.Exists(path) ? FileVersionInfo.GetVersionInfo(path).FileVersion : null;
            return new(version == null ? DriverRegistration.FileUnavailable : DriverRegistration.Registered, version, elevated);
        }
        catch (Exception error)
        {
            DiagnosticLog.Write(error.ToString());
            return new(DriverRegistration.Unknown, null, elevated);
        }
    }
}
