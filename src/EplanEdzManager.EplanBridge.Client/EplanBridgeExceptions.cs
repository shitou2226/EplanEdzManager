namespace EplanEdzManager.EplanBridge.Client;

public class EplanBridgeException : Exception
{
    public EplanBridgeException(string errorCode, string message, string technicalDetails = "", string logPath = "", Exception? innerException = null)
        : base(message, innerException)
    {
        ErrorCode = errorCode;
        TechnicalDetails = technicalDetails;
        LogPath = logPath;
    }

    public string ErrorCode { get; }
    public string TechnicalDetails { get; }
    public string LogPath { get; }
}

public sealed class EplanBridgeProcessExitedException : EplanBridgeException
{
    public EplanBridgeProcessExitedException(int? exitCode, string diagnostics)
        : base("BRIDGE-CRASH", "EPLAN Bridge unexpectedly exited.", diagnostics)
    {
        ExitCode = exitCode;
    }

    public int? ExitCode { get; }
}
