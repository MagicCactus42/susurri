namespace Susurri.Shared.Abstractions.Diagnostics;

public sealed record FatalErrorReport
{
    public required string Timestamp { get; init; }
    public required FatalErrorKind Kind { get; init; }
    public required ExceptionInfo Exception { get; init; }
    public required ProcessInfo Process { get; init; }
    public ActivityInfo? Activity { get; init; }
}

public sealed record ExceptionInfo(
    string Type,
    string Message,
    string StackTrace,
    ExceptionInfo? Inner);

public sealed record ProcessInfo(
    int Id,
    string? Version,
    double UptimeSeconds,
    long WorkingSetBytes,
    string OsDescription,
    string RuntimeVersion);

public sealed record ActivityInfo(
    string TraceId,
    string SpanId,
    string OperationName);
