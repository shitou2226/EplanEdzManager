using System.Text;
using EplanEdzManager.Core.Archives;
using EplanEdzManager.Edz.Archives;
using Xunit;

namespace EplanEdzManager.Edz.Tests;

public sealed class SevenZipEdzArchiveReaderTests
{
    [Fact]
    public void Open_returns_invalid_archive_diagnostic_for_corrupted_7z()
    {
        var path = CreateCorruptedEdz();
        try
        {
            var result = new SevenZipEdzArchiveReader().Open(path, CancellationToken.None);

            Assert.Null(result.Archive);
            Assert.Equal(EdzFormatKind.CorruptedArchive, result.Format);
            Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Code == "EDZ001");
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    [Trait("Category", "Integration")]
    public void Open_enumerates_real_non_solid_sample_and_reads_manifest_on_demand()
    {
        var sample = TestPaths.Sample("明纬.edz");
        var result = new SevenZipEdzArchiveReader().Open(sample, CancellationToken.None);
        using var archive = Assert.IsAssignableFrom<IEdzArchive>(result.Archive);

        var info = archive.GetArchiveInfo();
        var entries = archive.EnumerateEntries(CancellationToken.None);
        using var manifestStream = archive.OpenEntryStream("manifest.xml", CancellationToken.None);
        using var reader = new StreamReader(manifestStream, Encoding.UTF8, true, 4096, leaveOpen: false);
        var prefix = new char[64];
        var read = reader.Read(prefix, 0, prefix.Length);

        Assert.Equal(69, info.EntryCount);
        Assert.False(info.IsSolid);
        Assert.Equal(69, entries.Count);
        Assert.Contains(entries, entry => entry.Path == "manifest.xml");
        Assert.Contains("manifest", new string(prefix, 0, read), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    [Trait("Category", "Integration")]
    public void Dispose_releases_file_handle_so_copy_can_be_moved_and_deleted()
    {
        var directory = Path.Combine(Path.GetTempPath(), "EplanEdzManager.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var copy = Path.Combine(directory, "copy.edz");
        var moved = Path.Combine(directory, "moved.edz");
        File.Copy(TestPaths.Sample("明纬.edz"), copy);

        try
        {
            var result = new SevenZipEdzArchiveReader().Open(copy, CancellationToken.None);
            Assert.NotNull(result.Archive);
            result.Archive.Dispose();

            File.Move(copy, moved);
            File.Delete(moved);
            Assert.False(File.Exists(moved));
        }
        finally
        {
            if (File.Exists(copy)) File.Delete(copy);
            if (File.Exists(moved)) File.Delete(moved);
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    [Trait("Category", "Integration")]
    public void Cancellation_during_open_releases_file_handle()
    {
        var directory = Path.Combine(Path.GetTempPath(), "EplanEdzManager.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var copy = Path.Combine(directory, "cancel-copy.edz");
        var moved = Path.Combine(directory, "cancel-moved.edz");
        File.Copy(TestPaths.Sample("明纬.edz"), copy);

        try
        {
            using var cancellation = new CancellationTokenSource();
            var reader = new SevenZipEdzArchiveReader(cancellation.Cancel);

            Assert.Throws<OperationCanceledException>(() => reader.Open(copy, cancellation.Token));

            File.Move(copy, moved);
            File.Delete(moved);
            Assert.False(File.Exists(moved));
        }
        finally
        {
            if (File.Exists(copy)) File.Delete(copy);
            if (File.Exists(moved)) File.Delete(moved);
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void Safety_limits_reject_solid_encrypted_oversized_and_extreme_ratio_archives()
    {
        Assert.Throws<InvalidDataException>(() => ArchiveSafetyLimits.ValidateArchiveFlags(isSolid: true, isEncrypted: false));
        Assert.Throws<InvalidDataException>(() => ArchiveSafetyLimits.ValidateArchiveFlags(isSolid: false, isEncrypted: true));
        Assert.Throws<InvalidDataException>(() => ArchiveSafetyLimits.ValidateEntry(
            ArchiveSafetyLimits.MaximumEntryCount + 1, 1, 1, isDirectory: false, isEncrypted: false));
        Assert.Throws<InvalidDataException>(() => ArchiveSafetyLimits.ValidateEntry(
            1, ArchiveSafetyLimits.MaximumSingleEntryBytes + 1, 1, isDirectory: false, isEncrypted: false));
        Assert.Throws<InvalidDataException>(() => ArchiveSafetyLimits.ValidateEntry(
            1, ArchiveSafetyLimits.CompressionRatioCheckMinimumBytes, 1, isDirectory: false, isEncrypted: false));
    }

    [Fact]
    public void Safety_limits_allow_large_official_style_catalogs_but_keep_a_bounded_total()
    {
        var sixteenGiB = Enumerable.Range(1, 16)
            .Select(index => new EdzResourceEntry(
                $"resource-{index}.bin",
                1024L * 1024 * 1024,
                128L * 1024 * 1024,
                "LZMA2",
                isDirectory: false,
                isEncrypted: false,
                isSolid: false))
            .ToArray();
        var overLimit = sixteenGiB
            .Concat(Enumerable.Range(17, 17).Select(index => new EdzResourceEntry(
                $"resource-{index}.bin",
                1024L * 1024 * 1024,
                128L * 1024 * 1024,
                "LZMA2",
                isDirectory: false,
                isEncrypted: false,
                isSolid: false)))
            .ToArray();

        ArchiveSafetyLimits.ValidateTotalUncompressedBytes(sixteenGiB);
        Assert.Throws<InvalidDataException>(() => ArchiveSafetyLimits.ValidateTotalUncompressedBytes(overLimit));
    }

    private static string CreateCorruptedEdz()
    {
        var directory = Path.Combine(Path.GetTempPath(), "EplanEdzManager.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "corrupted.edz");
        File.WriteAllBytes(path, new byte[] { 0x37, 0x7A, 0xBC, 0xAF, 0x27, 0x1C, 0, 4, 0, 0, 0, 0 });
        return path;
    }
}

internal static class TestPaths
{
    public static string Sample(string name)
    {
        var fixtureRoot = Environment.GetEnvironmentVariable("EPLAN_EDZ_TEST_FIXTURE_DIR");
        if (!string.IsNullOrWhiteSpace(fixtureRoot))
        {
            var configured = Path.Combine(Path.GetFullPath(fixtureRoot), name);
            if (!File.Exists(configured)) throw new FileNotFoundException("Configured local fixture was not found.", configured);
            return configured;
        }
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            var candidate = Path.Combine(directory.FullName, "samples", name);
            if (File.Exists(candidate))
            {
                return candidate;
            }

            directory = directory.Parent;
        }

        throw new FileNotFoundException("Could not locate test sample " + name);
    }
}

