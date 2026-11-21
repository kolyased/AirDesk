using System;

namespace AirDesk;

internal static class ControllerNames
{
    public static string? Match(ReadOnlySpan<char> name)
    {
        if (name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) name = name[..^4];
        Span<char> key = stackalloc char[32];
        int length = 0;
        foreach (char character in name)
        {
            if (character is '.' or ' ' or '_' or '-') continue;
            if (length == key.Length) return null;
            key[length++] = char.ToLowerInvariant(character);
        }

        return key[..length] switch
        {
            "fancontrol" or "fancontrolservice" => "FanControl",
            "msiafterburner" => "MSI Afterburner",
            "argusmonitor" or "argusmonitorservice" => "Argus Monitor",
            "speedfan" => "SpeedFan",
            "asusfancontrolservice" or "aifanspeed" or "asuscomsvc" => "ASUS Fan Xpert / AI Suite",
            "gigabytecontrolcenter" or "smartfan" or "svctune" => "GIGABYTE Control Center / Smart Fan",
            "icue" or "corsairservice" => "Corsair iCUE",
            "nzxtcam" or "camhelper" => "NZXT CAM",
            _ => null
        };
    }
}
