namespace Susurri.Shared.Abstractions.Diagnostics;

public enum FatalErrorKind
{
    UnhandledException,
    UnobservedTaskException,
    FatalError,
}

public interface IFatalErrorHandler
{
    Task ReportAsync(Exception exception, FatalErrorKind kind, CancellationToken ct = default);
}
