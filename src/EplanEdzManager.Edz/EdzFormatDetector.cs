using EplanEdzManager.Core.Diagnostics;
using EplanEdzManager.Core.Archives;

namespace EplanEdzManager.Edz;

public sealed class EdzFormatDetectionResult
{
    public EdzFormatDetectionResult(EdzFormatKind kind, DiagnosticRecord? diagnostic = null)
    {
        Kind = kind;
        Diagnostic = diagnostic;
    }

    public EdzFormatKind Kind { get; }

    public DiagnosticRecord? Diagnostic { get; }
}

public static class EdzFormatDetector
{
    private static readonly byte[] SevenZipSignature = { 0x37, 0x7A, 0xBC, 0xAF, 0x27, 0x1C };

    public static EdzFormatDetectionResult Detect(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            return Error(EdzFormatKind.InvalidFile, "EDZ002", "The EDZ file does not exist.");
        }

        if (!string.Equals(Path.GetExtension(path), ".edz", StringComparison.OrdinalIgnoreCase))
        {
            return Error(EdzFormatKind.NotEdz, "EDZ003", "The file extension is not .edz.");
        }

        try
        {
            var buffer = new byte[SevenZipSignature.Length];
            using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                var read = stream.Read(buffer, 0, buffer.Length);
                if (read != buffer.Length)
                {
                    return Error(EdzFormatKind.InvalidFile, "EDZ004", "The EDZ file is too short to contain a supported signature.");
                }
            }

            return buffer.SequenceEqual(SevenZipSignature)
                ? new EdzFormatDetectionResult(EdzFormatKind.CurrentKnown7zEdz)
                : Error(EdzFormatKind.UnknownArchive, "EDZ005", "The .edz container signature is not supported.");
        }
        catch (UnauthorizedAccessException exception)
        {
            return Error(EdzFormatKind.InvalidFile, "EDZ006", "Access to the EDZ file was denied.", exception);
        }
        catch (IOException exception)
        {
            return Error(EdzFormatKind.InvalidFile, "EDZ007", "The EDZ file could not be read.", exception);
        }
    }

    private static EdzFormatDetectionResult Error(
        EdzFormatKind kind,
        string code,
        string message,
        Exception? exception = null)
    {
        return new EdzFormatDetectionResult(
            kind,
            new DiagnosticRecord(
                DiagnosticSeverity.Error,
                code,
                message,
                EvidenceLevel.Confirmed,
                exceptionType: exception?.GetType().FullName));
    }
}

