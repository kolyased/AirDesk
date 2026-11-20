using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using AirDesk;
using Xunit;

public sealed class FrameCaptureTests
{
    const string Header = "ProcessID,SwapChainAddress,msBetweenPresents\n";
    const string Rows = "42,A,20\n42,A,20\n";

    [Fact]
    public async Task LiveCsvReportsStoppedWriterAndResumesWhenRowsReturn()
    {
        using var fixture = new CsvFixture(Header + Rows);
        using var capture = new FrameCapture(fixture.Path, 42);
        await WaitFor(() => capture.Snapshot.Status.Contains("Ожидание новых кадров", StringComparison.Ordinal));
        Assert.Null(capture.Current);
        await File.AppendAllTextAsync(fixture.Path, Rows);
        await WaitFor(() => capture.Current?.FramesPerSecond == 50);
        await WaitFor(() => capture.Current == null);
        Assert.Contains("консоли PresentMon", capture.Snapshot.Status);

        await File.AppendAllTextAsync(fixture.Path, Rows);
        await WaitFor(() => capture.Current?.FramesPerSecond == 50);
    }

    [Fact]
    public async Task MissingIncompleteAndInvalidCsvAreReportedAndReaderRecovers()
    {
        using var fixture = new CsvFixture();
        using var capture = new FrameCapture(fixture.Path, 42);
        await WaitFor(() => capture.Snapshot.Status.Contains("не найден", StringComparison.Ordinal));
        Assert.Null(capture.Current);

        await File.WriteAllTextAsync(fixture.Path, "ProcessID,");
        await WaitFor(() => capture.Snapshot.Status.Contains("полного заголовка", StringComparison.Ordinal));
        await File.WriteAllTextAsync(fixture.Path, "Application,ProcessID,CpuFrameTime\n");
        await WaitFor(() => capture.Snapshot.Status.Contains("--v1_metrics", StringComparison.Ordinal));

        await File.WriteAllTextAsync(fixture.Path, Header);
        await WaitFor(() => capture.Snapshot.Status.Contains("Ожидание новых кадров", StringComparison.Ordinal));
        await File.AppendAllTextAsync(fixture.Path, Rows);
        await WaitFor(() => capture.Current?.FramesPerSecond == 50);

        await File.WriteAllTextAsync(fixture.Path, "");
        await WaitFor(() => capture.Snapshot.Status.Contains("полного заголовка", StringComparison.Ordinal));
        Assert.Null(capture.Current);
    }

    [Fact]
    public async Task FileAccessFailureClearsTheRateAndRetriesTheSameFile()
    {
        using var fixture = new CsvFixture(Header);
        using var capture = new FrameCapture(fixture.Path, 42);
        await WaitFor(() => capture.Snapshot.Status.Contains("Ожидание новых кадров", StringComparison.Ordinal));
        await File.AppendAllTextAsync(fixture.Path, Rows);
        await WaitFor(() => capture.Current?.FramesPerSecond == 50);

        using (var writer = await LockForAppend(fixture.Path))
        {
            await writer.WriteAsync(System.Text.Encoding.UTF8.GetBytes(Rows));
            await WaitFor(() => capture.Snapshot.Status.Contains("прочитать CSV", StringComparison.Ordinal));
            Assert.Null(capture.Current);
            if (OperatingSystem.IsWindows()) Assert.Contains("--output_stdout", capture.Snapshot.Status);
        }

        await WaitFor(() => capture.Current?.FramesPerSecond == 50);
    }

    [Fact]
    public async Task UnreadablePathReportsAccessErrorBeforeAnyCsvIsParsed()
    {
        using var fixture = new CsvFixture();
        Directory.CreateDirectory(fixture.Path);
        try
        {
            using var capture = new FrameCapture(fixture.Path, 42);
            await WaitFor(() => capture.Snapshot.Status.Contains("Нет доступа к CSV", StringComparison.Ordinal));
            Assert.Null(capture.Snapshot.Frames);
        }
        finally { Directory.Delete(fixture.Path); }
    }

    [Fact]
    public async Task RowsForOtherProcessesAndInvalidFrameIntervalsHaveDistinctStatus()
    {
        using var fixture = new CsvFixture(Header);
        using var capture = new FrameCapture(fixture.Path, 42);
        await WaitFor(() => capture.Snapshot.Status.Contains("Ожидание новых кадров", StringComparison.Ordinal));

        await File.AppendAllTextAsync(fixture.Path, "43,A,20\n43,A,20\n");
        await WaitFor(() => capture.Snapshot.Status.Contains("Проверьте PID", StringComparison.Ordinal));
        Assert.Null(capture.Current);

        await File.AppendAllTextAsync(fixture.Path, "42,A,NaN\n42,A,Infinity\n");
        await WaitFor(() => capture.Snapshot.Status.Contains("интервалах", StringComparison.Ordinal));
        Assert.Null(capture.Current);

        await File.AppendAllTextAsync(fixture.Path, Rows);
        await WaitFor(() => capture.Current?.FramesPerSecond == 50);
    }

    [Fact]
    public async Task FastTruncateAndRewriteWithChangedHeaderIsRecognized()
    {
        using var fixture = new CsvFixture(Header);
        using var capture = new FrameCapture(fixture.Path, 42);
        await WaitFor(() => capture.Snapshot.Status.Contains("Ожидание новых кадров", StringComparison.Ordinal));
        await File.AppendAllTextAsync(fixture.Path, Rows);
        await WaitFor(() => capture.Current?.FramesPerSecond == 50);

        await File.WriteAllTextAsync(fixture.Path, "SwapChainAddress,ProcessID,msBetweenPresents\nA,42,40\nA,42,40\nA,42,40\n");
        await WaitFor(() => capture.Snapshot.Status.Contains("Ожидание новых кадров", StringComparison.Ordinal));
        await File.AppendAllTextAsync(fixture.Path, "A,42,40\nA,42,40\n");
        await WaitFor(() => capture.Current?.FramesPerSecond == 25);
    }

    [Fact]
    public void InvalidIntervalsCannotProduceAnInfiniteFrameRate()
    {
        var parser = new PresentMonFrames(Header.TrimEnd('\n'));
        Assert.Null(parser.Summarize(["42,A,1e-310", "42,A,1e-310"], 42));
    }

    static async Task<FileStream> LockForAppend(string path)
    {
        FileStream? writer = null;
        await WaitFor(() =>
        {
            try
            {
                writer = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.None);
                return true;
            }
            catch (IOException) { return false; }
        });
        return writer!;
    }

    static async Task WaitFor(Func<bool> condition, [CallerArgumentExpression(nameof(condition))] string description = "")
    {
        var elapsed = Stopwatch.StartNew();
        while (elapsed.Elapsed < TimeSpan.FromSeconds(8))
        {
            if (condition()) return;
            await Task.Delay(20);
        }
        Assert.Fail($"Timed out waiting for {description}.");
    }

    sealed class CsvFixture : IDisposable
    {
        readonly DirectoryInfo directory = Directory.CreateTempSubdirectory("airdesk-frames-");

        public string Path { get; }

        public CsvFixture(string? content = null)
        {
            Path = System.IO.Path.Combine(directory.FullName, "frames.csv");
            if (content != null) File.WriteAllText(Path, content);
        }

        public void Dispose()
        {
            File.Delete(Path);
            directory.Delete();
        }
    }
}
