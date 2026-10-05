namespace EplanEdzManager.Core.Diagnostics;

public enum DiagnosticSeverity
{
    Information,
    Warning,
    Error,
    Fatal
}

public enum EvidenceLevel
{
    Confirmed,
    Likely,
    Hypothesis,
    Unknown
}

public sealed class DiagnosticRecord
{
    public DiagnosticRecord(
        DiagnosticSeverity severity,
        string code,
        string message,
        EvidenceLevel evidenceLevel,
        string? packageKey = null,
        string? entryPath = null,
        string? exceptionType = null)
    {
        Severity = severity;
        Code = code ?? throw new ArgumentNullException(nameof(code));
        Message = message ?? throw new ArgumentNullException(nameof(message));
        EvidenceLevel = evidenceLevel;
        PackageKey = packageKey;
        EntryPath = entryPath;
        ExceptionType = exceptionType;
    }

    public DiagnosticSeverity Severity { get; }

    public string Code { get; }

    public string Message { get; }

    public string? PackageKey { get; }

    public string? EntryPath { get; }

    public string? ExceptionType { get; }

    public EvidenceLevel EvidenceLevel { get; }
}

