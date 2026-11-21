using System;
using System.Buffers.Binary;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text.Json;

namespace AirDesk;

internal sealed class UnsupportedHistoryVersionException(long version)
    : Exception($"Версия {version} файла истории требует более новой версии AirDesk.");

internal static class HistoryJournal
{
    const int LegacyMaxBytes = 4 * 1024 * 1024;
    const int MaxUncompressedBytes = 64 * 1024 * 1024;
    const int Version = 2;
    static readonly uint[] crc32Table = BuildCrc32Table();

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
        using var json = JsonDocument.Parse(stream);
        CheckVersion(json.RootElement, 1);
        var document = json.RootElement.Deserialize<LegacyJournal>();
        return document is { Version: 1, Points: not null } ? document.Points : throw InvalidFormat();
    }

    public static HistoryPoint[] Read(string path)
    {
        using var stream = Open(path, HistoryModel.MaxBytes);
        // GZipStream can return all the decompressed bytes even when the trailer is truncated.
        Span<byte> trailer = stackalloc byte[2 * sizeof(uint)];
        if (stream.Length < 10 + trailer.Length) throw InvalidCompression();
        stream.Seek(-trailer.Length, SeekOrigin.End);
        stream.ReadExactly(trailer);
        stream.Position = 0;

        using var gzip = new GZipStream(stream, CompressionMode.Decompress);
        using var json = new MemoryStream();
        var buffer = new byte[81920];
        uint crc = uint.MaxValue;
        int read;
        while ((read = gzip.Read(buffer)) > 0)
        {
            if (json.Length + read > MaxUncompressedBytes) throw TooLarge();
            var chunk = buffer.AsSpan(0, read);
            foreach (byte value in chunk) crc = crc32Table[(crc ^ value) & 0xFF] ^ (crc >> 8);
            json.Write(chunk);
        }
        using var parsed = JsonDocument.Parse(json.GetBuffer().AsMemory(0, (int)json.Length));
        CheckVersion(parsed.RootElement, Version);
        if (BinaryPrimitives.ReadUInt32LittleEndian(trailer) != ~crc ||
            BinaryPrimitives.ReadUInt32LittleEndian(trailer[sizeof(uint)..]) != json.Length)
            throw InvalidCompression();

        var document = parsed.RootElement.Deserialize<Journal>();
        if (document is not { Version: Version, Series: not null } || document.Series.Any(series => series?.Samples == null))
            throw InvalidFormat();
        if (document.Series.Sum(series => (long)series.Samples.Length) > HistoryModel.MaxPoints) throw TooLarge();
        return document.Series.SelectMany(series => series.Samples.Select(sample => sample.ToPoint(series))).OfType<HistoryPoint>().ToArray();
    }

    static uint[] BuildCrc32Table()
    {
        const uint polynomial = 0xEDB88320;
        var table = new uint[256];
        for (uint value = 0; value < table.Length; value++)
        {
            uint crc = value;
            for (int bit = 0; bit < 8; bit++)
                crc = (crc >> 1) ^ ((crc & 1) == 0 ? 0 : polynomial);
            table[value] = crc;
        }
        return table;
    }

    static void CheckVersion(JsonElement root, int current)
    {
        if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty("Version", out var property) &&
            property.ValueKind == JsonValueKind.Number && property.TryGetInt64(out long version) && version > current)
            throw new UnsupportedHistoryVersionException(version);
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
    static InvalidDataException InvalidCompression() => new("Сжатый файл истории повреждён.");
    static InvalidDataException InvalidFormat() => new("Неизвестный формат истории.");
}
