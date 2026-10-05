using System.Text;
using EplanEdzManager.Core.Archives;
using EplanEdzManager.Core.Diagnostics;
using SharpCompress.Archives;
using SharpCompress.Archives.SevenZip;
using SharpCompress.Readers;

namespace EplanEdzManager.Edz.Archives;

public sealed class SevenZipEdzArchiveReader : IEdzArchiveReader
{
    private readonly Action? _afterArchiveOpened;

    public SevenZipEdzArchiveReader()
    {
    }

    internal SevenZipEdzArchiveReader(Action afterArchiveOpened)
    {
        _afterArchiveOpened = afterArchiveOpened ?? throw new ArgumentNullException(nameof(afterArchiveOpened));
    }

    public bool CanRead(string path) => EdzFormatDetector.Detect(path).Kind == EdzFormatKind.CurrentKnown7zEdz;

    public EdzArchiveOpenResult Open(string path, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var detection = EdzFormatDetector.Detect(path);
        if (detection.Kind != EdzFormatKind.CurrentKnown7zEdz)
        {
            var diagnostics = detection.Diagnostic is null
                ? Array.Empty<DiagnosticRecord>()
                : new[] { detection.Diagnostic };
            return new EdzArchiveOpenResult(detection.Kind, null, diagnostics);
        }

        FileStream? fileStream = null;
        IArchive? archive = null;
        try
        {
            fileStream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 64 * 1024, FileOptions.RandomAccess);
            archive = SevenZipArchive.OpenArchive(
                fileStream,
                new ReaderOptions { LeaveStreamOpen = true, LookForHeader = false, BufferSize = 64 * 1024 });
            _afterArchiveOpened?.Invoke();

            var opened = new SharpCompressEdzArchive(path, fileStream, archive, cancellationToken);
            fileStream = null;
            archive = null;
            return new EdzArchiveOpenResult(EdzFormatKind.CurrentKnown7zEdz, opened, opened.OpenDiagnostics);
        }
        catch (OperationCanceledException)
        {
            archive?.Dispose();
            fileStream?.Dispose();
            throw;
        }
        catch (Exception exception) when (exception is not OutOfMemoryException and not StackOverflowException)
        {
            archive?.Dispose();
            fileStream?.Dispose();
            return new EdzArchiveOpenResult(
                EdzFormatKind.CorruptedArchive,
                null,
                new[]
                {
                    new DiagnosticRecord(
                        DiagnosticSeverity.Error,
                        "EDZ001",
                        "The 7z EDZ container is damaged or cannot be opened.",
                        EvidenceLevel.Confirmed,
                        exceptionType: exception.GetType().FullName)
                });
        }
    }
}

internal sealed class SharpCompressEdzArchive : IEdzArchive
{
    private readonly FileStream _fileStream;
    private readonly IArchive _archive;
    private readonly Dictionary<string, IArchiveEntry> _entryLookup;
    private readonly IReadOnlyList<EdzResourceEntry> _entries;
    private int _entryStreamLeased;
    private bool _disposed;

    public SharpCompressEdzArchive(string sourcePath, FileStream fileStream, IArchive archive, CancellationToken cancellationToken)
    {
        SourcePath = sourcePath;
        _fileStream = fileStream;
        _archive = archive;
        var diagnostics = new List<DiagnosticRecord>();
        var entries = new List<EdzResourceEntry>();
        _entryLookup = new Dictionary<string, IArchiveEntry>(StringComparer.OrdinalIgnoreCase);

        ArchiveSafetyLimits.ValidateArchiveFlags(archive.IsSolid, archive.IsEncrypted);

        foreach (var entry in archive.Entries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ArchiveSafetyLimits.ValidateEntry(entries.Count + 1, entry.Size, entry.CompressedSize, entry.IsDirectory, entry.IsEncrypted);
            if (!TryNormalizeArchivePath(entry.Key, out var normalizedPath))
            {
                diagnostics.Add(new DiagnosticRecord(DiagnosticSeverity.Error, "EDZ204", "The archive contains an unsafe entry path.", EvidenceLevel.Confirmed, entryPath: entry.Key));
                continue;
            }

            if (_entryLookup.ContainsKey(normalizedPath))
            {
                diagnostics.Add(new DiagnosticRecord(DiagnosticSeverity.Error, "EDZ302", "The archive contains duplicate normalized entry paths.", EvidenceLevel.Confirmed, entryPath: normalizedPath));
                continue;
            }

            _entryLookup.Add(normalizedPath, entry);
            entries.Add(new EdzResourceEntry(normalizedPath, entry.Size, entry.CompressedSize, entry.CompressionType.ToString(), entry.IsDirectory, entry.IsEncrypted, entry.IsSolid));
        }

        ArchiveSafetyLimits.ValidateTotalUncompressedBytes(entries);

        _entries = entries.AsReadOnly();
        OpenDiagnostics = diagnostics.AsReadOnly();
    }

    public string SourcePath { get; }
    public IReadOnlyList<DiagnosticRecord> OpenDiagnostics { get; }

    public EdzArchiveInfo GetArchiveInfo()
    {
        ThrowIfDisposed();
        return new EdzArchiveInfo(SourcePath, _fileStream.Length, _entries.Count, _entries.Sum(entry => entry.Size), _archive.IsSolid, _archive.IsEncrypted, _entries.Select(entry => entry.CompressionMethod));
    }

    public IReadOnlyList<EdzResourceEntry> EnumerateEntries(CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        cancellationToken.ThrowIfCancellationRequested();
        return _entries;
    }

    public Stream OpenEntryStream(string archivePath, CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        cancellationToken.ThrowIfCancellationRequested();
        if (!TryNormalizeArchivePath(archivePath, out var normalizedPath))
        {
            throw new ArgumentException("The archive path is unsafe or invalid.", nameof(archivePath));
        }

        if (!_entryLookup.TryGetValue(normalizedPath, out var entry))
        {
            throw new KeyNotFoundException("The archive entry was not found: " + normalizedPath);
        }

        if (Interlocked.CompareExchange(ref _entryStreamLeased, 1, 0) != 0)
        {
            throw new InvalidOperationException("Only one entry stream may be read from an EDZ archive at a time.");
        }

        try
        {
            return new LeasedCancellationStream(entry.OpenEntryStream(), cancellationToken, () => Interlocked.Exchange(ref _entryStreamLeased, 0));
        }
        catch
        {
            Interlocked.Exchange(ref _entryStreamLeased, 0);
            throw;
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _archive.Dispose();
        _fileStream.Dispose();
    }

    private static bool TryNormalizeArchivePath(string? value, out string normalized)
    {
        normalized = (value ?? string.Empty).Normalize(NormalizationForm.FormC).Replace('\\', '/');
        if (normalized.Length == 0 || normalized.StartsWith("/", StringComparison.Ordinal) || normalized.StartsWith("//", StringComparison.Ordinal) || normalized.Contains("//") || normalized.IndexOf(':') >= 0 || normalized.Any(char.IsControl))
        {
            return false;
        }

        return normalized.Split('/').All(segment => segment.Length > 0 && segment != "." && segment != "..");
    }

    private void ThrowIfDisposed()
    {
        if (_disposed) throw new ObjectDisposedException(nameof(SharpCompressEdzArchive));
    }
}

internal static class ArchiveSafetyLimits
{
    internal const int MaximumEntryCount = 100_000;
    internal const long MaximumSingleEntryBytes = 1024L * 1024 * 1024;
    internal const long MaximumTotalUncompressedBytes = 4L * 1024 * 1024 * 1024;
    internal const long CompressionRatioCheckMinimumBytes = 1024L * 1024;
    internal const long MaximumCompressionRatio = 1000;

    internal static void ValidateArchiveFlags(bool isSolid, bool isEncrypted)
    {
        if (isEncrypted) throw new InvalidDataException("Encrypted EDZ archives are not supported.");
        if (isSolid) throw new InvalidDataException("Solid EDZ archives are not supported by the bounded beta reader.");
    }

    internal static void ValidateEntry(int entryNumber, long size, long compressedSize, bool isDirectory, bool isEncrypted)
    {
        if (entryNumber > MaximumEntryCount) throw new InvalidDataException("The EDZ archive exceeds the entry-count safety limit.");
        if (isEncrypted) throw new InvalidDataException("Encrypted EDZ entries are not supported.");
        if (size < 0 || compressedSize < 0) throw new InvalidDataException("The EDZ archive contains an entry with an invalid size.");
        if (isDirectory) return;
        if (size > MaximumSingleEntryBytes) throw new InvalidDataException("An EDZ entry exceeds the 1 GiB uncompressed-size safety limit.");
        if (size >= CompressionRatioCheckMinimumBytes && compressedSize > 0 && size / compressedSize > MaximumCompressionRatio)
            throw new InvalidDataException("An EDZ entry exceeds the compression-ratio safety limit.");
    }

    internal static void ValidateTotalUncompressedBytes(IEnumerable<EdzResourceEntry> entries)
    {
        long total = 0;
        foreach (var entry in entries)
        {
            if (entry.IsDirectory) continue;
            if (entry.Size > MaximumTotalUncompressedBytes - total)
                throw new InvalidDataException("The EDZ archive exceeds the 4 GiB total uncompressed-size safety limit.");
            total += entry.Size;
        }
    }
}

internal sealed class LeasedCancellationStream : Stream
{
    private readonly Stream _inner;
    private readonly CancellationToken _cancellationToken;
    private readonly Action _onDispose;
    private bool _disposed;

    public LeasedCancellationStream(Stream inner, CancellationToken cancellationToken, Action onDispose)
    {
        _inner = inner;
        _cancellationToken = cancellationToken;
        _onDispose = onDispose;
    }

    public override bool CanRead => _inner.CanRead;
    public override bool CanSeek => _inner.CanSeek;
    public override bool CanWrite => false;
    public override long Length => _inner.Length;
    public override long Position { get => _inner.Position; set => _inner.Position = value; }
    public override void Flush() => _inner.Flush();

    public override int Read(byte[] buffer, int offset, int count)
    {
        _cancellationToken.ThrowIfCancellationRequested();
        var read = _inner.Read(buffer, offset, count);
        _cancellationToken.ThrowIfCancellationRequested();
        return read;
    }

    public override long Seek(long offset, SeekOrigin origin) => _inner.Seek(offset, origin);
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        if (!_disposed && disposing)
        {
            _disposed = true;
            try { _inner.Dispose(); }
            finally { _onDispose(); }
        }

        base.Dispose(disposing);
    }
}

