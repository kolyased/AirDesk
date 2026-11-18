using System;
using System.Linq;

namespace AirDesk;

internal static class ControllerNames
{
    public static string? Match(string name)
    {
        string key = new(name.Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray());
        if (key.StartsWith("fancontrol", StringComparison.Ordinal)) return "FanControl";
        if (key.StartsWith("msiafterburner", StringComparison.Ordinal)) return "MSI Afterburner";
        if (key.StartsWith("argusmonitor", StringComparison.Ordinal)) return "Argus Monitor";
        return key switch
        {
            "speedfan" => "SpeedFan",
            "asusfancontrolservice" or "aifanspeed" or "asuscomsvc" => "ASUS Fan Xpert / AI Suite",
            "gigabytecontrolcenter" or "smartfan" or "svctune" => "GIGABYTE Control Center / Smart Fan",
            "icue" or "corsairservice" => "Corsair iCUE",
            "nzxtcam" => "NZXT CAM",
            _ => null
        };
    }
}
