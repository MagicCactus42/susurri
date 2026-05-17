using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;
using Susurri.Shared.Abstractions.Diagnostics;

namespace Susurri.Shared.Infrastructure.Diagnostics;

public sealed class FatalErrorHandler : IFatalErrorHandler, IDisposable
{
    private readonly CrashDumpWriter _writer;
    private readonly HttpCrashReporter? _reporter;
    private readonly ILogger<FatalErrorHandler> _logger;
    private readonly DateTimeOffset _startedAt = DateTimeOffset.UtcNow;

    private int _installed;
    private UnhandledExceptionEventHandler? _domainHandler;
    private EventHandler<UnobservedTaskExceptionEventArgs>? _taskHandler;

    public FatalErrorHandler(
        CrashDumpWriter writer,
        HttpCrashReporter? reporter,
        ILogger<FatalErrorHandler> logger)
    {
        _writer = writer;
        _reporter = reporter;
        _logger = logger;
    }

    public void Install()
    {
        if (Interlocked.Exchange(ref _installed, 1) != 0)
            return;

        _domainHandler = (_, e) =>
        {
            if (e.ExceptionObject is Exception ex)
            {
                try
                {
                    ReportAsync(ex, FatalErrorKind.UnhandledException).GetAwaiter().GetResult();
                }
                catch
                {
                }
            }
        };
        AppDomain.CurrentDomain.UnhandledException += _domainHandler;

        _taskHandler = (_, e) =>
        {
            _ = ReportAsync(e.Exception, FatalErrorKind.UnobservedTaskException);
            e.SetObserved();
        };
        TaskScheduler.UnobservedTaskException += _taskHandler;
    }

    public async Task ReportAsync(Exception exception, FatalErrorKind kind, CancellationToken ct = default)
    {
        FatalErrorReport report;
        try
        {
            report = BuildReport(exception, kind);
        }
        catch (Exception buildEx)
        {
            Console.Error.WriteLine($"FatalErrorHandler: failed to build report: {buildEx}");
            return;
        }

        try
        {
            var path = _writer.WriteReport(report);
            _logger.LogError(exception, "Fatal error ({Kind}) recorded to {Path}", kind, path);
        }
        catch (Exception writeEx)
        {
            Console.Error.WriteLine($"FatalErrorHandler: failed to write report: {writeEx}");
        }

        if (_reporter is not null)
        {
            try
            {
                await _reporter.PostAsync(report, ct).ConfigureAwait(false);
            }
            catch (Exception postEx)
            {
                Console.Error.WriteLine($"FatalErrorHandler: failed to POST report: {postEx}");
            }
        }
    }

    public FatalErrorReport BuildReport(Exception exception, FatalErrorKind kind)
    {
        var activity = Activity.Current;
        var process = Process.GetCurrentProcess();
        var version = Assembly.GetEntryAssembly()?.GetName().Version?.ToString();

        return new FatalErrorReport
        {
            Timestamp = DateTimeOffset.UtcNow.ToString("O"),
            Kind = kind,
            Exception = BuildExceptionInfo(exception),
            Process = new ProcessInfo(
                Id: process.Id,
                Version: version,
                UptimeSeconds: Math.Round((DateTimeOffset.UtcNow - _startedAt).TotalSeconds, 3),
                WorkingSetBytes: process.WorkingSet64,
                OsDescription: RuntimeInformation.OSDescription,
                RuntimeVersion: RuntimeInformation.FrameworkDescription),
            Activity = activity is null
                ? null
                : new ActivityInfo(
                    TraceId: activity.TraceId.ToString(),
                    SpanId: activity.SpanId.ToString(),
                    OperationName: activity.OperationName),
        };
    }

    private static ExceptionInfo BuildExceptionInfo(Exception exception)
    {
        return new ExceptionInfo(
            Type: exception.GetType().FullName ?? exception.GetType().Name,
            Message: CrashReportRedactor.Redact(exception.Message),
            StackTrace: CrashReportRedactor.Redact(exception.StackTrace ?? string.Empty),
            Inner: exception.InnerException is { } inner ? BuildExceptionInfo(inner) : null);
    }

    public void Dispose()
    {
        if (_domainHandler is not null)
            AppDomain.CurrentDomain.UnhandledException -= _domainHandler;
        if (_taskHandler is not null)
            TaskScheduler.UnobservedTaskException -= _taskHandler;
        _reporter?.Dispose();
    }
}
