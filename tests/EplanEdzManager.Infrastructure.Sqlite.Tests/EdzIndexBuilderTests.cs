using System.Security.Cryptography;
using EplanEdzManager.Infrastructure.Sqlite;
using Xunit;

namespace EplanEdzManager.Infrastructure.Sqlite.Tests;

public sealed class EdzIndexBuilderTests
{
    [Fact]
    [Trait("Category", "Integration")]
    public async Task Real_edz_is_indexed_incrementally_and_deleted_file_is_pruned_without_modifying_source()
    {
        using var workspace = new TemporaryWorkspace();
        var sample = FindSample("明纬.edz");
        var originalHash = ComputeSha256(sample);
        var copy = Path.Combine(workspace.DirectoryPath, "明纬.edz");
        File.Copy(sample, copy);
        var repository = new SqliteIndexRepository(workspace.DatabasePath);
        var builder = new EdzIndexBuilder(repository);

        var first = await builder.ScanDirectoryAsync(workspace.DirectoryPath, recursive: false);
        Assert.Equal(1, first.Indexed);
        Assert.Equal(0, first.Failed);
        Assert.Equal(26, (await repository.GetStatisticsAsync()).PartCount);
        Assert.Contains(await repository.SearchAsync(new PartSearchQuery(PartNumber: "DR-100-24")), result => result.PartNumber == "DR-100-24");

        var second = await builder.ScanDirectoryAsync(workspace.DirectoryPath, recursive: false);
        Assert.Equal(0, second.Indexed);
        Assert.Equal(1, second.Skipped);

        File.Delete(copy);
        var third = await builder.ScanDirectoryAsync(workspace.DirectoryPath, recursive: false);
        Assert.Equal(1, third.Removed);
        Assert.Equal(0, (await repository.GetStatisticsAsync()).PartCount);
        Assert.Equal(originalHash, ComputeSha256(sample));
    }

    private static string FindSample(string name)
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
            if (File.Exists(candidate)) return candidate;
            directory = directory.Parent;
        }

        throw new FileNotFoundException("Could not locate test sample " + name);
    }

    private static string ComputeSha256(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1024 * 1024, FileOptions.SequentialScan);
        return Convert.ToHexString(SHA256.HashData(stream));
    }
}
