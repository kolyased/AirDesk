using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace AirDesk;

internal sealed record FrameRate(double FramesPerSecond, double MedianMilliseconds, int Samples);

internal sealed class PresentMonFrames
{
    readonly int pidColumn, chainColumn, timeColumn;

    public PresentMonFrames(string header)
    {
        string[] columns = Split(header.TrimStart('\uFEFF'));
        pidColumn = Find("ProcessID");
        chainColumn = Find("SwapChainAddress");
        timeColumn = Find("MsBetweenPresents");
        if (pidColumn < 0 || chainColumn < 0 || timeColumn < 0)
            throw new FormatException("CSV PresentMon должен содержать ProcessID, SwapChainAddress и MsBetweenPresents. Используйте --v1_metrics.");

        int Find(string name) => Array.FindIndex(columns, value => string.Equals(value, name, StringComparison.OrdinalIgnoreCase));
    }

    public FrameRate? Summarize(IEnumerable<string> lines, int processId)
    {
        var chains = new Dictionary<string, List<double>>(StringComparer.OrdinalIgnoreCase);
        int maxColumn = Math.Max(timeColumn, Math.Max(pidColumn, chainColumn));
        foreach (string line in lines)
        {
            string[] fields = Split(line);
            if (fields.Length <= maxColumn || !int.TryParse(fields[pidColumn], NumberStyles.None, CultureInfo.InvariantCulture, out int pid) ||
                pid != processId || !double.TryParse(fields[timeColumn], NumberStyles.Float, CultureInfo.InvariantCulture, out double milliseconds) ||
                !double.IsFinite(milliseconds) || milliseconds is <= 0 or > 5000 || fields[chainColumn].Length == 0)
                continue;
            if (!chains.TryGetValue(fields[chainColumn], out var samples)) chains.Add(fields[chainColumn], samples = new());
            samples.Add(milliseconds);
        }

        var frames = chains.Values.MaxBy(values => values.Count);
        if (frames == null || frames.Count < 2) return null;
        frames.Sort();
        int middle = frames.Count / 2;
        double median = frames.Count % 2 == 0 ? (frames[middle - 1] + frames[middle]) / 2 : frames[middle];
        return new(1000 / frames.Average(), median, frames.Count);
    }

    static string[] Split(string row)
    {
        if (!row.Contains('"')) return row.Split(',');
        var fields = new List<string>();
        var field = new System.Text.StringBuilder();
        bool quoted = false;
        for (int index = 0; index < row.Length; index++)
        {
            char value = row[index];
            if (value == '"')
            {
                if (quoted && index + 1 < row.Length && row[index + 1] == '"') { field.Append('"'); index++; }
                else quoted = !quoted;
            }
            else if (value == ',' && !quoted) { fields.Add(field.ToString()); field.Clear(); }
            else field.Append(value);
        }
        if (quoted) return [];
        fields.Add(field.ToString());
        return fields.ToArray();
    }
}
