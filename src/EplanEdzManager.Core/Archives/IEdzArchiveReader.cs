using System.Collections.ObjectModel;
using EplanEdzManager.Core.Diagnostics;

namespace EplanEdzManager.Core.Archives;

public enum EdzFormatKind
{
    CurrentKnown7zEdz,
    UnknownArchive,
    InvalidFile,
    NotEdz,
    CorruptedArchive
}

public sealed class EdzArchiveInfo
{
    public EdzArchiveInfo(
        string sourcePath,
        long fileSize,
        int entryCount,
        long totalUncompressedSize,
        bool isSolid,
        bool isEncrypted,
        IEnumerable<string> compressionMethods)
    {
        SourcePath = sourcePath;
        FileSize = fileSize;
        EntryCount = entryCount;
        TotalUncompressedSize = totalUncompressedSize;
        IsSolid = isSolid;
        IsEncrypted = isEncrypted;
        CompressionMethods = new ReadOnlyCollection<string>(compressionMethods.Distinct(StringComparer.Ordinal).ToList());
    }

    public string SourcePath { get; }
    public long FileSize { get; }
    public int EntryCount { get; }
    public long TotalUncompressedSize { get; }
    public bool IsSolid { get; }
    public bool IsEncrypted { get; }
    public IReadOnlyList<string> CompressionMethods { get; }
}

public sealed class EdzResourceEntry
{
    public EdzResourceEntry(string path, long size, long compressedSize, string compressionMethod, bool isDirectory, bool isEncrypted, bool isSolid)
    {
        Path = path;
        Size = size;
        CompressedSize = compressedSize;
        CompressionMethod = compressionMethod;
        IsDirectory = isDirectory;
        IsEncrypted = isEncrypted;
        IsSolid = isSolid;
    }

    public string Path { get; }
    public long Size { get; }
    public long CompressedSize { get; }
    public string CompressionMethod { get; }
    public bool IsDirectory { get; }
    public bool IsEncrypted { get; }
    public bool IsSolid { get; }
}

public interface IEdzArchive : IDisposable
{
    EdzArchiveInfo GetArchiveInfo();
    IReadOnlyList<EdzResourceEntry> EnumerateEntries(CancellationToken cancellationToken);
    Stream OpenEntryStream(string archivePath, CancellationToken cancellationToken);
}

public interface IEdzArchiveReader
{
    bool CanRead(string path);
    EdzArchiveOpenResult Open(string path, CancellationToken cancellationToken);
}

public sealed class EdzArchiveOpenResult
{
    public EdzArchiveOpenResult(EdzFormatKind format, IEdzArchive? archive, IEnumerable<DiagnosticRecord> diagnostics)
    {
        Format = format;
        Archive = archive;
        Diagnostics = new ReadOnlyCollection<DiagnosticRecord>(diagnostics.ToList());
    }

    public EdzFormatKind Format { get; }
    public IEdzArchive? Archive { get; }
    public IReadOnlyList<DiagnosticRecord> Diagnostics { get; }
}

