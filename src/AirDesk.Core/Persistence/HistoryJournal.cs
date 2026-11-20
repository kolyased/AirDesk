using System;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text.Json;

namespace AirDesk;

internal static class HistoryJournal
{
    const int LegacyMaxBytes = 4 * 1024 * 1024;
    const int MaxUncompressedBytes = 64 * 1024 * 1024;
    const int Version = 2;

    sealed record LegacyJournal(int Version, HistoryPoint[] Points);
    sealed record Journal(int Version, Series[] Series);
    sealed record Series(SensorReference Sensor, string Name, string Unit, Sample[] Samples);
    readonly record struct Sample(long From, long At, float Mean, float Min, float Max, int Count, int Interval)
    {
        public HistoryPoint? ToPoint(Series series) => From < DateTimeOffset.MinValue.UtcTicks || At > DateTimeOffset.MaxValue.UtcTicks || From > At
            ? null
            : new(series.Sensor, series.Name, series.Unit, new DateTimeOffset(From, TimeSpan.Zero), new DateTimeOffset(At, TimeSpan.Zero),
                Mean, Min, Max, Count, Interval);
    }

    public static HistoryPoint[] ReadLegacy(string path)
    {
        using var stream = Open(path, LegacyMaxBytes);
        var document = JsonSerializer.Deserialize<LegacyJournal>(stream);
        return document is { Version: 1, Points: not null } ? document.Points : throw InvalidFormat();
    }

    public static HistoryPoint[] Read(string path)
    {
        using var stream = Open(path, HistoryModel.MaxBytes);
        using var gzip = new GZipStream(stream, CompressionMode.Decompress);
        using var json = new MemoryStream();
        var buffer = new byte[81920];
        int read;
        while ((read = gzip.Read(buffer)) > 0)
        {
            if (json.Length + read > MaxUncompressedBytes) throw TooLarge();
            json.Write(buffer, 0, read);
        }
        var document = JsonSerializer.Deserialize<Journal>(json.GetBuffer().AsSpan(0, (int)json.Length));
        if (document is not { Version: Version, Series: not null } || document.Series.Any(series => series?.Samples == null))
            throw InvalidFormat();
        if (document.Series.Sum(series => (long)series.Samples.Length) > HistoryModel.MaxPoints) throw TooLarge();
        return document.Series.SelectMany(series => series.Samples.Select(sample => sample.ToPoint(series))).OfType<HistoryPoint>().ToArray();
    }

    public static bool TryEncode(HistoryPoint[] points, MemoryStream compressed)
    {
        var series = points.GroupBy(point => (point.Sensor, point.Name, point.Unit)).Select(group =>
            new Series(group.Key.Sensor, group.Key.Name, group.Key.Unit,
                group.Select(point => new Sample(point.From.UtcTicks, point.At.UtcTicks, point.Mean, point.Min, point.Max,
                    point.Count, point.IntervalSeconds)).ToArray())).ToArray();
        byte[] json = JsonSerializer.SerializeToUtf8Bytes(new Journal(Version, series));
        if (json.Length > MaxUncompressedBytes) return false;
        compressed.SetLength(0);
        using (var gzip = new GZipStream(compressed, CompressionLevel.Fastest, leaveOpen: true)) gzip.Write(json);
        compressed.Position = 0;
        return compressed.Length <= HistoryModel.MaxBytes;
    }

    static FileStream Open(string path, int limit)
    {
        var stream = File.OpenRead(path);
        if (stream.Length <= limit) return stream;
        stream.Dispose();
        throw TooLarge();
    }

    static InvalidDataException TooLarge() => new("Файл истории превышает лимит.");
    static InvalidDataException InvalidFormat() => new("Неизвестный формат истории.");
}
