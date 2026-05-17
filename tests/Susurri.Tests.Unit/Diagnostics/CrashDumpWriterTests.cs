using System.Text.Json;
using Shouldly;
using Susurri.Shared.Abstractions.Diagnostics;
using Susurri.Shared.Infrastructure.Diagnostics;

namespace Susurri.Tests.Unit.Diagnostics;

public class CrashDumpWriterTests : IDisposable
{
    private readonly string _tempDir;

    public CrashDumpWriterTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"susurri-crash-tests-{Guid.NewGuid():N}");
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
            Directory.Delete(_tempDir, recursive: true);
    }

    [Fact]
    public void WriteReport_CreatesDirectory_IfMissing()
    {
        Directory.Exists(_tempDir).ShouldBeFalse();
        var sut = new CrashDumpWriter(_tempDir);

        var path = sut.WriteReport(BuildReport());

        Directory.Exists(_tempDir).ShouldBeTrue();
        File.Exists(path).ShouldBeTrue();
    }

    [Fact]
    public void WriteReport_WritesJsonWithExpectedFields()
    {
        var sut = new CrashDumpWriter(_tempDir);
        var report = BuildReport();

        var path = sut.WriteReport(report);

        var content = File.ReadAllText(path);
        using var doc = JsonDocument.Parse(content);
        var root = doc.RootElement;
        root.GetProperty("timestamp").GetString().ShouldNotBeNullOrEmpty();
        root.GetProperty("kind").GetString().ShouldBe("UnhandledException");
        root.GetProperty("exception").GetProperty("type").GetString().ShouldBe("System.InvalidOperationException");
        root.GetProperty("process").GetProperty("id").GetInt32().ShouldBeGreaterThan(0);
    }

    [Fact]
    public void WriteReport_TwoConcurrentReports_ProduceDistinctFilenames()
    {
        var sut = new CrashDumpWriter(_tempDir);
        var report = BuildReport();

        var p1 = sut.WriteReport(report);
        var p2 = sut.WriteReport(report);

        p1.ShouldNotBe(p2);
        File.Exists(p1).ShouldBeTrue();
        File.Exists(p2).ShouldBeTrue();
    }

    [Fact]
    public void WriteReport_NullActivity_OmittedFromJson()
    {
        var sut = new CrashDumpWriter(_tempDir);
        var report = BuildReport();

        var path = sut.WriteReport(report);
        var content = File.ReadAllText(path);

        using var doc = JsonDocument.Parse(content);
        doc.RootElement.TryGetProperty("activity", out _).ShouldBeFalse();
    }

    [Fact]
    public void GetDefaultDirectory_ContainsSusurriAndCrashes()
    {
        var path = CrashDumpWriter.GetDefaultDirectory();
        path.ShouldContain("Susurri");
        path.ShouldContain("crashes");
    }

    [Fact]
    public void Constructor_NullOrEmptyDirectory_FallsBackToDefault()
    {
        var sut1 = new CrashDumpWriter(null);
        var sut2 = new CrashDumpWriter("");
        var sut3 = new CrashDumpWriter("   ");

        sut1.Directory.ShouldBe(CrashDumpWriter.GetDefaultDirectory());
        sut2.Directory.ShouldBe(CrashDumpWriter.GetDefaultDirectory());
        sut3.Directory.ShouldBe(CrashDumpWriter.GetDefaultDirectory());
    }

    private static FatalErrorReport BuildReport()
    {
        return new FatalErrorReport
        {
            Timestamp = DateTimeOffset.UtcNow.ToString("O"),
            Kind = FatalErrorKind.UnhandledException,
            Exception = new ExceptionInfo("System.InvalidOperationException", "test message", "stack", null),
            Process = new ProcessInfo(
                Id: System.Diagnostics.Process.GetCurrentProcess().Id,
                Version: "0.1.0",
                UptimeSeconds: 1.0,
                WorkingSetBytes: 1024L * 1024L,
                OsDescription: "Linux test",
                RuntimeVersion: ".NET test"),
            Activity = null,
        };
    }
}
