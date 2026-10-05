using System.Text;
using EplanEdzManager.Core.Archives;
using EplanEdzManager.Edz.Documents;
using Xunit;

namespace EplanEdzManager.Edz.Tests;

public sealed class EdzDocumentReaderTests
{
    [Fact]
    public void Open_reports_missing_manifest_without_throwing()
    {
        var archive = new FakeArchive(new Dictionary<string, byte[]>());
        var result = new EdzDocumentReader(new FakeArchiveReader(archive)).Open("sample.edz", CancellationToken.None);

        Assert.Null(result.Document);
        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Code == "EDZ101");
        Assert.True(archive.Disposed);
    }

    [Fact]
    public void Open_builds_index_without_opening_part_xml_until_requested()
    {
        var archive = ValidArchive();
        var result = new EdzDocumentReader(new FakeArchiveReader(archive)).Open("sample.edz", CancellationToken.None);
        using var document = Assert.IsType<EdzDocument>(result.Document);

        Assert.Equal(new[] { "manifest.xml" }, archive.OpenedPaths);

        var part = document.Parts.FindExact("P-1", CancellationToken.None);

        Assert.Equal("P-1", part?.PartNumber);
        Assert.Equal(new[] { "manifest.xml", "items/partxml/P-1.part.xml" }, archive.OpenedPaths);
    }

    [Fact]
    public void Open_reports_missing_references_and_duplicate_package_keys()
    {
        const string manifest = """
            <manifest version="2.0"><packages>
              <package type="part" key="P-1" name="one"><items><item type="picture" name="picturefile" locator="missing.jpg" /></items></package>
              <package type="part" key="p-1" name="duplicate"><items /></package>
            </packages></manifest>
            """;
        var archive = new FakeArchive(new Dictionary<string, byte[]>
        {
            ["manifest.xml"] = Encoding.UTF8.GetBytes(manifest)
        });

        var result = new EdzDocumentReader(new FakeArchiveReader(archive)).Open("sample.edz", CancellationToken.None);
        using var document = Assert.IsType<EdzDocument>(result.Document);

        Assert.Contains(document.Diagnostics, diagnostic => diagnostic.Code == "EDZ301");
        Assert.Contains(document.Diagnostics, diagnostic => diagnostic.Code == "EDZ303");
    }

    [Fact]
    public void Open_reports_duplicate_archive_paths()
    {
        var result = new EdzDocumentReader(new FakeArchiveReader(new DuplicatePathArchive()))
            .Open("sample.edz", CancellationToken.None);
        using var document = Assert.IsType<EdzDocument>(result.Document);

        Assert.Contains(document.Diagnostics, diagnostic => diagnostic.Code == "EDZ302");
    }

    private static FakeArchive ValidArchive()
    {
        const string manifest = """
            <manifest version="2.0"><packages><package type="part" key="P-1" name="P-1"><items>
              <item type="partxml" name="part" locator="P-1.part.xml" />
            </items></package></packages></manifest>
            """;
        const string part = "<partsmanagement><part P_ARTICLE_PARTNR=\"P-1\" P_ARTICLE_MANUFACTURER=\"MFR\"><variant P_ARTICLE_VARIANT=\"1\" /></part></partsmanagement>";
        return new FakeArchive(new Dictionary<string, byte[]>
        {
            ["manifest.xml"] = Encoding.UTF8.GetBytes(manifest),
            ["items/partxml/P-1.part.xml"] = Encoding.UTF8.GetBytes(part)
        });
    }
}

internal sealed class FakeArchiveReader : IEdzArchiveReader
{
    private readonly IEdzArchive _archive;

    public FakeArchiveReader(IEdzArchive archive) => _archive = archive;

    public bool CanRead(string path) => true;

    public EdzArchiveOpenResult Open(string path, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return new EdzArchiveOpenResult(EdzFormatKind.CurrentKnown7zEdz, _archive, Array.Empty<EplanEdzManager.Core.Diagnostics.DiagnosticRecord>());
    }
}

internal sealed class FakeArchive : IEdzArchive
{
    private readonly Dictionary<string, byte[]> _entries;

    public FakeArchive(Dictionary<string, byte[]> entries) => _entries = entries;

    public bool Disposed { get; private set; }

    public List<string> OpenedPaths { get; } = new List<string>();

    public EdzArchiveInfo GetArchiveInfo() => new EdzArchiveInfo("sample.edz", 0, _entries.Count, _entries.Sum(entry => entry.Value.LongLength), false, false, new[] { "LZMA" });

    public IReadOnlyList<EdzResourceEntry> EnumerateEntries(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return _entries.Select(entry => new EdzResourceEntry(entry.Key, entry.Value.LongLength, entry.Value.LongLength, "LZMA", false, false, false)).ToList();
    }

    public Stream OpenEntryStream(string archivePath, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        OpenedPaths.Add(archivePath);
        return new MemoryStream(_entries[archivePath], writable: false);
    }

    public void Dispose() => Disposed = true;
}

internal sealed class DuplicatePathArchive : IEdzArchive
{
    private static readonly byte[] Manifest = Encoding.UTF8.GetBytes("<manifest version=\"2.0\"><packages /></manifest>");

    public EdzArchiveInfo GetArchiveInfo() => new EdzArchiveInfo("sample.edz", 0, 3, Manifest.Length, false, false, new[] { "LZMA" });

    public IReadOnlyList<EdzResourceEntry> EnumerateEntries(CancellationToken cancellationToken) => new[]
    {
        new EdzResourceEntry("manifest.xml", Manifest.Length, Manifest.Length, "LZMA", false, false, false),
        new EdzResourceEntry("items/picture/a.jpg", 1, 1, "LZMA", false, false, false),
        new EdzResourceEntry("ITEMS/PICTURE/A.JPG", 1, 1, "LZMA", false, false, false)
    };

    public Stream OpenEntryStream(string archivePath, CancellationToken cancellationToken) => new MemoryStream(Manifest, writable: false);

    public void Dispose() { }
}

