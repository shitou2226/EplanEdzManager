using System.Collections.ObjectModel;
using System.Diagnostics;
using EplanEdzManager.Core;
using EplanEdzManager.Core.Archives;
using EplanEdzManager.Core.Diagnostics;
using EplanEdzManager.Core.Model;
using EplanEdzManager.Edz.Archives;
using EplanEdzManager.Edz.Manifest;
using EplanEdzManager.Edz.Parts;

namespace EplanEdzManager.Edz.Documents;

public sealed class EdzReadMetrics
{
    public EdzReadMetrics(double archiveOpenMilliseconds, double entryEnumerationMilliseconds, double manifestParseMilliseconds, double validationMilliseconds, double indexBuildMilliseconds, double totalOpenMilliseconds)
    {
        ArchiveOpenMilliseconds = archiveOpenMilliseconds;
        EntryEnumerationMilliseconds = entryEnumerationMilliseconds;
        ManifestParseMilliseconds = manifestParseMilliseconds;
        ValidationMilliseconds = validationMilliseconds;
        IndexBuildMilliseconds = indexBuildMilliseconds;
        TotalOpenMilliseconds = totalOpenMilliseconds;
    }

    public double ArchiveOpenMilliseconds { get; }
    public double EntryEnumerationMilliseconds { get; }
    public double ManifestParseMilliseconds { get; }
    public double ValidationMilliseconds { get; }
    public double IndexBuildMilliseconds { get; }
    public double TotalOpenMilliseconds { get; }
}

public sealed class EdzDocumentOpenResult
{
    public EdzDocumentOpenResult(EdzDocument? document, EdzFormatKind format, IEnumerable<DiagnosticRecord> diagnostics, EdzReadMetrics? metrics = null)
    {
        Document = document;
        Format = format;
        Diagnostics = new ReadOnlyCollection<DiagnosticRecord>(diagnostics.ToList());
        Metrics = metrics ?? new EdzReadMetrics(0, 0, 0, 0, 0, 0);
    }

    public EdzDocument? Document { get; }
    public EdzFormatKind Format { get; }
    public IReadOnlyList<DiagnosticRecord> Diagnostics { get; }
    public EdzReadMetrics Metrics { get; }
}

public sealed class EdzPackageResource
{
    public EdzPackageResource(EdzItemReference reference, EdzResourceEntry? entry, int referenceCount)
    {
        Reference = reference;
        Entry = entry;
        ReferenceCount = referenceCount;
    }

    public EdzItemReference Reference { get; }
    public EdzResourceEntry? Entry { get; }
    public int ReferenceCount { get; }
}

public sealed class EdzDocument : IDisposable
{
    private readonly IEdzArchive _archive;
    private readonly IReadOnlyList<DiagnosticRecord> _baseDiagnostics;
    private readonly Dictionary<string, EdzResourceEntry> _entryLookup;
    private readonly Dictionary<string, int> _referenceCounts;
    private bool _disposed;

    internal EdzDocument(
        IEdzArchive archive,
        EdzManifest manifest,
        EdzPartIndex parts,
        IReadOnlyList<DiagnosticRecord> diagnostics,
        Dictionary<string, EdzResourceEntry> entryLookup,
        Dictionary<string, int> referenceCounts,
        IReadOnlyList<string> unreferencedEntries,
        int missingReferenceCount)
    {
        _archive = archive;
        Manifest = manifest;
        Parts = parts;
        _baseDiagnostics = diagnostics;
        _entryLookup = entryLookup;
        _referenceCounts = referenceCounts;
        UnreferencedEntries = unreferencedEntries;
        MissingReferenceCount = missingReferenceCount;
    }

    public EdzArchiveInfo ArchiveInfo => _archive.GetArchiveInfo();
    public EdzManifest Manifest { get; }
    public EdzPartIndex Parts { get; }
    public int UnreferencedEntryCount => UnreferencedEntries.Count;
    public IReadOnlyList<string> UnreferencedEntries { get; }
    public int MissingReferenceCount { get; }

    public IReadOnlyList<DiagnosticRecord> Diagnostics => new ReadOnlyCollection<DiagnosticRecord>(
        _baseDiagnostics.Concat(Parts.Diagnostics).ToList());

    public IReadOnlyList<EdzResourceEntry> Entries => _archive.EnumerateEntries(CancellationToken.None);

    public IReadOnlyList<EdzPackageResource> GetResources(string packageKey)
    {
        ThrowIfDisposed();
        var package = Manifest.Packages.FirstOrDefault(candidate =>
            string.Equals(candidate.Key, packageKey, StringComparison.OrdinalIgnoreCase));
        if (package is null)
        {
            return Array.Empty<EdzPackageResource>();
        }

        return new ReadOnlyCollection<EdzPackageResource>(package.Items.Select(reference =>
        {
            var path = reference.ResolvedLocator?.ArchivePath;
            var entry = path is not null && _entryLookup.TryGetValue(path, out var found) ? found : null;
            var count = path is not null && _referenceCounts.TryGetValue(path, out var references) ? references : 0;
            return new EdzPackageResource(reference, entry, count);
        }).ToList());
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _archive.Dispose();
    }

    private void ThrowIfDisposed()
    {
        if (_disposed) throw new ObjectDisposedException(nameof(EdzDocument));
    }
}

public sealed class EdzDocumentReader
{
    private readonly IEdzArchiveReader _archiveReader;
    private readonly EdzManifestParser _manifestParser;
    private readonly IPartMetadataReader _partMetadataReader;

    public EdzDocumentReader(
        IEdzArchiveReader? archiveReader = null,
        EdzManifestParser? manifestParser = null,
        IPartMetadataReader? partMetadataReader = null)
    {
        _archiveReader = archiveReader ?? new SevenZipEdzArchiveReader();
        _manifestParser = manifestParser ?? new EdzManifestParser();
        _partMetadataReader = partMetadataReader ?? new PartXmlMetadataReader();
    }

    public EdzDocumentOpenResult Open(string path, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var totalTimer = Stopwatch.StartNew();
        var stepTimer = Stopwatch.StartNew();
        var archiveResult = _archiveReader.Open(path, cancellationToken);
        var archiveOpenMilliseconds = stepTimer.Elapsed.TotalMilliseconds;
        var entryEnumerationMilliseconds = 0d;
        var manifestParseMilliseconds = 0d;
        var validationMilliseconds = 0d;
        var indexBuildMilliseconds = 0d;
        if (archiveResult.Archive is null)
        {
            totalTimer.Stop();
            return new EdzDocumentOpenResult(null, archiveResult.Format, archiveResult.Diagnostics, Metrics());
        }

        var archive = archiveResult.Archive;
        var diagnostics = archiveResult.Diagnostics.ToList();
        try
        {
            stepTimer.Restart();
            var entries = archive.EnumerateEntries(cancellationToken);
            var entryLookup = new Dictionary<string, EdzResourceEntry>(StringComparer.OrdinalIgnoreCase);
            foreach (var entry in entries)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!entryLookup.ContainsKey(entry.Path))
                {
                    entryLookup.Add(entry.Path, entry);
                }
                else
                {
                    diagnostics.Add(new DiagnosticRecord(
                        DiagnosticSeverity.Error,
                        "EDZ302",
                        "The archive contains duplicate normalized entry paths.",
                        EvidenceLevel.Confirmed,
                        entryPath: entry.Path));
                }
            }
            entryEnumerationMilliseconds = stepTimer.Elapsed.TotalMilliseconds;

            var manifestEntry = entries.FirstOrDefault(entry =>
                string.Equals(entry.Path, "manifest.xml", StringComparison.OrdinalIgnoreCase));
            if (manifestEntry is null)
            {
                diagnostics.Add(new DiagnosticRecord(DiagnosticSeverity.Error, "EDZ101", "The EDZ archive does not contain manifest.xml.", EvidenceLevel.Confirmed));
                archive.Dispose();
                totalTimer.Stop();
                return new EdzDocumentOpenResult(null, archiveResult.Format, diagnostics, Metrics());
            }

            ManifestParseResult manifestResult;
            stepTimer.Restart();
            using (var manifestStream = archive.OpenEntryStream(manifestEntry.Path, cancellationToken))
            {
                manifestResult = _manifestParser.Parse(manifestStream, cancellationToken);
            }
            manifestParseMilliseconds = stepTimer.Elapsed.TotalMilliseconds;

            diagnostics.AddRange(manifestResult.Diagnostics);
            if (manifestResult.Manifest is null)
            {
                archive.Dispose();
                totalTimer.Stop();
                return new EdzDocumentOpenResult(null, archiveResult.Format, diagnostics, Metrics());
            }

            var manifest = manifestResult.Manifest;
            stepTimer.Restart();
            AddDuplicatePackageDiagnostics(manifest, diagnostics);
            var referenceCounts = BuildReferenceCounts(manifest);
            var missingReferenceCount = AddMissingReferenceDiagnostics(manifest, entryLookup, diagnostics);
            var unreferencedEntries = entries.Where(entry =>
                !string.Equals(entry.Path, manifestEntry.Path, StringComparison.OrdinalIgnoreCase)
                && !referenceCounts.ContainsKey(entry.Path))
                .Select(entry => entry.Path)
                .ToList()
                .AsReadOnly();
            validationMilliseconds = stepTimer.Elapsed.TotalMilliseconds;

            stepTimer.Restart();
            var sourcePath = archive.GetArchiveInfo().SourcePath;
            var indexEntries = manifest.Packages
                .Where(package => !string.IsNullOrWhiteSpace(package.Key))
                .Select(package => CreateLazyPart(package, archive, sourcePath))
                .ToList();
            var index = new EdzPartIndex(indexEntries);
            indexBuildMilliseconds = stepTimer.Elapsed.TotalMilliseconds;
            var document = new EdzDocument(
                archive,
                manifest,
                index,
                diagnostics.AsReadOnly(),
                entryLookup,
                referenceCounts,
                unreferencedEntries,
                missingReferenceCount);
            totalTimer.Stop();
            return new EdzDocumentOpenResult(document, archiveResult.Format, diagnostics, Metrics());
        }
        catch (OperationCanceledException)
        {
            archive.Dispose();
            throw;
        }
        catch (Exception exception) when (exception is not OutOfMemoryException and not StackOverflowException)
        {
            archive.Dispose();
            diagnostics.Add(new DiagnosticRecord(
                DiagnosticSeverity.Error,
                "EDZ001",
                "The EDZ archive could not be inspected safely.",
                EvidenceLevel.Confirmed,
                exceptionType: exception.GetType().FullName));
            totalTimer.Stop();
            return new EdzDocumentOpenResult(null, EdzFormatKind.CorruptedArchive, diagnostics, Metrics());
        }

        EdzReadMetrics Metrics() => new EdzReadMetrics(
            archiveOpenMilliseconds,
            entryEnumerationMilliseconds,
            manifestParseMilliseconds,
            validationMilliseconds,
            indexBuildMilliseconds,
            totalTimer.Elapsed.TotalMilliseconds);
    }

    private LazyPartIndexEntry CreateLazyPart(EdzPackage package, IEdzArchive archive, string sourcePath)
    {
        var partReference = package.Items.FirstOrDefault(item =>
            string.Equals(item.Type, "partxml", StringComparison.OrdinalIgnoreCase)
            && string.Equals(item.Name, "part", StringComparison.OrdinalIgnoreCase));

        return new LazyPartIndexEntry(package.Key!, package.Name, cancellationToken =>
        {
            var entryPath = partReference?.ResolvedLocator?.ArchivePath;
            if (entryPath is null)
            {
                return MissingPartResult(package.Key, partReference?.RawLocator);
            }

            try
            {
                using var stream = archive.OpenEntryStream(entryPath, cancellationToken);
                return _partMetadataReader.Read(stream, package.Key, sourcePath, entryPath, cancellationToken);
            }
            catch (Exception exception) when (exception is not OperationCanceledException and not OutOfMemoryException and not StackOverflowException)
            {
                return new PartMetadataReadResult(
                    null,
                    new[]
                    {
                        new DiagnosticRecord(
                            DiagnosticSeverity.Error,
                            "EDZ401",
                            "The part metadata entry could not be opened or parsed.",
                            EvidenceLevel.Confirmed,
                            package.Key,
                            entryPath,
                            exception.GetType().FullName)
                    });
            }
        });
    }

    private static PartMetadataReadResult MissingPartResult(string? packageKey, string? locator)
    {
        return new PartMetadataReadResult(
            null,
            new[]
            {
                new DiagnosticRecord(
                    DiagnosticSeverity.Error,
                    "EDZ402",
                    "The package does not contain a usable part metadata reference.",
                    EvidenceLevel.Confirmed,
                    packageKey,
                    locator)
            });
    }

    private static void AddDuplicatePackageDiagnostics(EdzManifest manifest, ICollection<DiagnosticRecord> diagnostics)
    {
        foreach (var group in manifest.Packages
                     .Where(package => !string.IsNullOrWhiteSpace(package.Key))
                     .GroupBy(package => package.Key!, StringComparer.OrdinalIgnoreCase)
                     .Where(group => group.Count() > 1))
        {
            diagnostics.Add(new DiagnosticRecord(
                DiagnosticSeverity.Error,
                "EDZ303",
                "The manifest contains a duplicate package key: " + group.Key,
                EvidenceLevel.Confirmed,
                group.Key));
        }
    }

    private static Dictionary<string, int> BuildReferenceCounts(EdzManifest manifest)
    {
        var counts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var path in manifest.Packages
                     .SelectMany(package => package.Items)
                     .Select(item => item.ResolvedLocator?.ArchivePath)
                     .Where(path => path is not null))
        {
            counts[path!] = counts.TryGetValue(path!, out var count) ? count + 1 : 1;
        }

        return counts;
    }

    private static int AddMissingReferenceDiagnostics(
        EdzManifest manifest,
        IReadOnlyDictionary<string, EdzResourceEntry> entries,
        ICollection<DiagnosticRecord> diagnostics)
    {
        var missing = 0;
        foreach (var package in manifest.Packages)
        {
            foreach (var item in package.Items)
            {
                var path = item.ResolvedLocator?.ArchivePath;
                if (path is null || entries.ContainsKey(path))
                {
                    continue;
                }

                missing++;
                diagnostics.Add(new DiagnosticRecord(
                    DiagnosticSeverity.Error,
                    "EDZ301",
                    "A manifest resource reference does not exist in the archive.",
                    EvidenceLevel.Confirmed,
                    package.Key,
                    path));
            }
        }

        return missing;
    }
}

