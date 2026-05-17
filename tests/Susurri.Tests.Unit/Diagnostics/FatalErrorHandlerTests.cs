using System.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;
using Susurri.Shared.Abstractions.Diagnostics;
using Susurri.Shared.Infrastructure.Diagnostics;

namespace Susurri.Tests.Unit.Diagnostics;

public class FatalErrorHandlerTests : IDisposable
{
    private readonly string _tempDir;

    public FatalErrorHandlerTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"susurri-fatal-tests-{Guid.NewGuid():N}");
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
            Directory.Delete(_tempDir, recursive: true);
    }

    [Fact]
    public void BuildReport_CapturesExceptionTypeMessageStackAndKind()
    {
        var sut = NewHandler();
        var ex = MakeException();

        var report = sut.BuildReport(ex, FatalErrorKind.FatalError);

        report.Kind.ShouldBe(FatalErrorKind.FatalError);
        report.Exception.Type.ShouldBe(typeof(InvalidOperationException).FullName);
        report.Exception.Message.ShouldContain("operation failed");
        report.Exception.StackTrace.ShouldNotBeNullOrEmpty();
    }

    [Fact]
    public void BuildReport_CapturesInnerExceptionChain()
    {
        var sut = NewHandler();
        var inner = new ArgumentException("inner arg");
        var outer = new InvalidOperationException("outer", inner);

        var report = sut.BuildReport(outer, FatalErrorKind.FatalError);

        report.Exception.Inner.ShouldNotBeNull();
        report.Exception.Inner!.Type.ShouldBe(typeof(ArgumentException).FullName);
        report.Exception.Inner.Message.ShouldContain("inner arg");
    }

    [Fact]
    public void BuildReport_NoActivity_LeavesActivityNull()
    {
        Activity.Current.ShouldBeNull();
        var sut = NewHandler();

        var report = sut.BuildReport(MakeException(), FatalErrorKind.UnhandledException);

        report.Activity.ShouldBeNull();
    }

    [Fact]
    public void BuildReport_WithActivity_CapturesTraceAndSpanIds()
    {
        var sut = NewHandler();
        using var activity = new Activity("test.fatal").Start();

        var report = sut.BuildReport(MakeException(), FatalErrorKind.UnhandledException);

        report.Activity.ShouldNotBeNull();
        report.Activity!.OperationName.ShouldBe("test.fatal");
        report.Activity.TraceId.ShouldBe(activity.TraceId.ToString());
        report.Activity.SpanId.ShouldBe(activity.SpanId.ToString());
    }

    [Fact]
    public void BuildReport_RedactsKeyHexInExceptionMessage()
    {
        var sut = NewHandler();
        var keyBytes = new byte[32];
        for (int i = 0; i < 32; i++) keyBytes[i] = (byte)i;
        var hex = Convert.ToHexString(keyBytes);
        var ex = new InvalidOperationException($"Failed for key {hex}");

        var report = sut.BuildReport(ex, FatalErrorKind.FatalError);

        report.Exception.Message.ShouldNotContain(hex);
        report.Exception.Message.ShouldContain("<key:");
    }

    [Fact]
    public async Task ReportAsync_WritesReportToConfiguredDirectory()
    {
        var sut = NewHandler();

        await sut.ReportAsync(MakeException(), FatalErrorKind.FatalError);

        Directory.Exists(_tempDir).ShouldBeTrue();
        Directory.GetFiles(_tempDir, "*.json").Length.ShouldBe(1);
    }

    [Fact]
    public async Task ReportAsync_WithoutRemoteReporter_SucceedsSilently()
    {
        var sut = NewHandler();
        await Should.NotThrowAsync(async () => await sut.ReportAsync(MakeException(), FatalErrorKind.FatalError));
    }

    [Fact]
    public void Install_IsIdempotent()
    {
        var sut = NewHandler();
        sut.Install();
        sut.Install();
        sut.Install();
        sut.Dispose();
    }

    [Fact]
    public void ProcessInfo_HasNonZeroPidAndRuntime()
    {
        var sut = NewHandler();

        var report = sut.BuildReport(MakeException(), FatalErrorKind.FatalError);

        report.Process.Id.ShouldBe(Process.GetCurrentProcess().Id);
        report.Process.RuntimeVersion.ShouldNotBeNullOrEmpty();
        report.Process.OsDescription.ShouldNotBeNullOrEmpty();
    }

    private FatalErrorHandler NewHandler()
    {
        var writer = new CrashDumpWriter(_tempDir);
        return new FatalErrorHandler(writer, reporter: null, NullLogger<FatalErrorHandler>.Instance);
    }

    private static Exception MakeException()
    {
        try
        {
            throw new InvalidOperationException("operation failed in test");
        }
        catch (Exception ex)
        {
            return ex;
        }
    }
}
