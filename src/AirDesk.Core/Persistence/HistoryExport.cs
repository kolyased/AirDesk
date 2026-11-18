using System;
using System.Globalization;
using System.IO;
using System.Text;

namespace AirDesk;

internal static class HistoryExport
{
    public static void Save(string path, HistoryPoint[] rows)
    {
        AtomicJson.RequireExternalDestination(path);
        if (Path.GetExtension(path).Equals(".json", StringComparison.OrdinalIgnoreCase))
        {
            AtomicJson.Write(path, new { Version = 1, ExportedAt = DateTimeOffset.UtcNow, Points = rows });
            return;
        }
        if (!Path.GetExtension(path).Equals(".csv", StringComparison.OrdinalIgnoreCase))
            throw new IOException("Выберите расширение CSV или JSON.");
        AtomicJson.WriteFile(path, stream =>
        {
            using var writer = new StreamWriter(stream, new UTF8Encoding(true), leaveOpen: true);
            writer.WriteLine("from,at,device,device_id,sensor,sensor_id,unit,mean,min,max,count");
            foreach (var row in rows)
                writer.WriteLine(string.Join(',', row.From.ToString("O"), row.At.ToString("O"), Csv(row.Sensor.DeviceName),
                    Csv(row.Sensor.DeviceId), Csv(row.Name), Csv(row.Sensor.Id), Csv(row.Unit),
                    Number(row.Mean), Number(row.Min), Number(row.Max), row.Count.ToString(CultureInfo.InvariantCulture)));
        });
    }

    static string Number(float value) => value.ToString("R", CultureInfo.InvariantCulture);

    static string Csv(string value)
    {
        if (value.TrimStart() is { Length: > 0 } trimmed && trimmed[0] is '=' or '+' or '-' or '@') value = "'" + value;
        return "\"" + value.Replace("\"", "\"\"") + "\"";
    }
}
