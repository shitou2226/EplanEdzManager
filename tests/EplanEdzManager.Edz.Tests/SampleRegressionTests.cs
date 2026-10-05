using System.Security.Cryptography;
using EplanEdzManager.Core.Archives;
using EplanEdzManager.Edz.Documents;
using Xunit;

namespace EplanEdzManager.Edz.Tests;

public sealed class SampleRegressionTests
{
    public static IEnumerable<object[]> Samples()
    {
        yield return new object[] { "德力西.edz", 386, 281, 1397, "3E0D916C247823695C128DE0A45CB0D96D057D08F0D78195F36AEA15AB4A1B70" };
        yield return new object[] { "明纬.edz", 69, 26, 130, "9C492F54E56B6CFFD44D1460A4EB4A1FA1CA7B129247414E8303AD05A31C6B09" };
        yield return new object[] { "欧姆龙.edz", 67749, 23937, 138567, "56087033D880E06E8101E40C9BF4048D3CD49B637FB45B4BAD421BA1890DB17E" };
    }

    [Theory]
    [MemberData(nameof(Samples))]
    [Trait("Category", "Integration")]
    public void Real_sample_matches_phase_zero_baseline_and_remains_unchanged(
        string sampleName,
        int expectedEntries,
        int expectedPackages,
        int expectedItemReferences,
        string expectedSha256)
    {
        var path = TestPaths.Sample(sampleName);
        var before = ComputeSha256(path);
        Assert.Equal(expectedSha256, before);

        var result = new EdzDocumentReader().Open(path, CancellationToken.None);
        Assert.Equal(EdzFormatKind.CurrentKnown7zEdz, result.Format);
        using (var document = Assert.IsType<EdzDocument>(result.Document))
        {
            Assert.Equal(expectedEntries, document.ArchiveInfo.EntryCount);
            Assert.Equal(expectedPackages, document.Manifest.Packages.Count);
            Assert.Equal(expectedItemReferences, document.Manifest.Packages.Sum(package => package.Items.Count));
            Assert.Equal(expectedPackages, document.Parts.Count);
            Assert.Equal(0, document.MissingReferenceCount);
            Assert.True(
                document.UnreferencedEntryCount == 0,
                "Unreferenced entries: " + string.Join(", ", document.UnreferencedEntries.Take(10)));

            var firstPackage = document.Manifest.Packages.First(package => !string.IsNullOrWhiteSpace(package.Key));
            var firstPart = document.Parts.FindExact(firstPackage.Key!, CancellationToken.None);
            Assert.Equal(firstPackage.Key, firstPart?.PartNumber);
        }

        Assert.Equal(before, ComputeSha256(path));
    }

    [Fact]
    [Trait("Category", "Integration")]
    public void Omron_package_key_search_finds_real_CJ2M_part_without_eager_full_xml_scan()
    {
        var result = new EdzDocumentReader().Open(TestPaths.Sample("欧姆龙.edz"), CancellationToken.None);
        using var document = Assert.IsType<EdzDocument>(result.Document);
        var expected = document.Manifest.Packages.First(package =>
            package.Key?.IndexOf("CJ2M", StringComparison.OrdinalIgnoreCase) >= 0);

        var matches = document.Parts.Search("CJ2M", 20, CancellationToken.None);

        Assert.Contains(matches, part => string.Equals(part.PartNumber, expected.Key, StringComparison.OrdinalIgnoreCase));
        Assert.True(matches.Count <= 20);
    }

    private static string ComputeSha256(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1024 * 1024, FileOptions.SequentialScan);
        return Convert.ToHexString(SHA256.HashData(stream));
    }
}

