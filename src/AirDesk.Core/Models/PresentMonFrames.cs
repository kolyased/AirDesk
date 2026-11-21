using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace AirDesk;

internal sealed record FrameRate(double FramesPerSecond, double MedianMilliseconds, int Samples);

internal enum FrameBatchStatus { Measured, MissingPid, InvalidIntervals, InsufficientSamples }
internal sealed record FrameBatch(FrameRate? Rate, FrameBatchStatus Status);

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

    public FrameRate? Summarize(IEnumerable<string> lines, int processId) => Analyze(lines, processId).Rate;

    public FrameBatch Analyze(IEnumerable<string> lines, int processId)
    {
        var chains = new Dictionary<string, List<double>>(StringComparer.OrdinalIgnoreCase);
        int maxColumn = Math.Max(timeColumn, Math.Max(pidColumn, chainColumn));
        bool hasTargetRows = false;
        foreach (string line in lines)
        {
            string[] fields = Split(line);
            if (fields.Length <= pidColumn ||
                !int.TryParse(fields[pidColumn], NumberStyles.None, CultureInfo.InvariantCulture, out int pid) || pid != processId)
                continue;

            hasTargetRows = true;
            if (fields.Length <= maxColumn || fields[chainColumn].Length == 0 ||
                !double.TryParse(fields[timeColumn], NumberStyles.Float, CultureInfo.InvariantCulture, out double milliseconds) ||
                !double.IsFinite(milliseconds) || milliseconds is <= 0 or > 5000 || !double.IsFinite(1000 / milliseconds))
                continue;

            if (!chains.TryGetValue(fields[chainColumn], out var samples)) chains.Add(fields[chainColumn], samples = new());
            samples.Add(milliseconds);
        }

        var frames = chains.Values.MaxBy(values => values.Count);
        if (frames == null) return new(null, hasTargetRows ? FrameBatchStatus.InvalidIntervals : FrameBatchStatus.MissingPid);
        if (frames.Count < 2) return new(null, FrameBatchStatus.InsufficientSamples);
        frames.Sort();
        int middle = frames.Count / 2;
        double median = frames.Count % 2 == 0 ? (frames[middle - 1] + frames[middle]) / 2 : frames[middle];
        double rate = 1000 / frames.Average();
        return double.IsFinite(rate) ? new(new FrameRate(rate, median, frames.Count), FrameBatchStatus.Measured)
            : new(null, FrameBatchStatus.InvalidIntervals);
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
