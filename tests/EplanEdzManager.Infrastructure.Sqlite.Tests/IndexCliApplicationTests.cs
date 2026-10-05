using System.Text.Json;
using EplanEdzIndex;
using EplanEdzManager.Infrastructure.Sqlite;
using Xunit;

namespace EplanEdzManager.Infrastructure.Sqlite.Tests;

public sealed class IndexCliApplicationTests
{
    [Fact]
    public async Task Cli_initializes_registers_searches_reports_stats_and_removes_directory()
    {
        using var workspace = new TemporaryWorkspace();
        var app = new IndexCliApplication();

        Assert.Equal(0, await RunAsync(app, "init", workspace.DatabasePath, "--json"));
        Assert.Equal(0, await RunAsync(app, "add-dir", workspace.DatabasePath, workspace.DirectoryPath, "--recursive", "--json"));

        var repository = new SqliteIndexRepository(workspace.DatabasePath);
        var directory = Assert.Single(await repository.GetDirectoriesAsync());
        await repository.ReplaceFileAsync(directory.Id, SqliteIndexRepositoryTests.CreateDocument(Path.Combine(workspace.DirectoryPath, "catalog.edz")));

        var search = await CaptureAsync(app, "search", workspace.DatabasePath, "power", "--manufacturer", "MEAN", "--resources", "present", "--json");
        Assert.Equal(0, search.ExitCode);
        using (var json = JsonDocument.Parse(search.Output))
        {
            Assert.Equal("DR-100-24", json.RootElement[0].GetProperty("partNumber").GetString());
        }

        var stats = await CaptureAsync(app, "stats", workspace.DatabasePath, "--json");
        using (var json = JsonDocument.Parse(stats.Output))
        {
            Assert.Equal(3, json.RootElement.GetProperty("partCount").GetInt32());
        }

        Assert.Equal(0, await RunAsync(app, "remove-dir", workspace.DatabasePath, workspace.DirectoryPath, "--json"));
        Assert.Equal(0, (await repository.GetStatisticsAsync()).PartCount);
    }

    private static async Task<int> RunAsync(IndexCliApplication app, params string[] args)
    {
        return (await CaptureAsync(app, args)).ExitCode;
    }

    private static async Task<(int ExitCode, string Output, string Error)> CaptureAsync(IndexCliApplication app, params string[] args)
    {
        using var output = new StringWriter();
        using var error = new StringWriter();
        var exitCode = await app.RunAsync(args, output, error, CancellationToken.None);
        return (exitCode, output.ToString(), error.ToString());
    }
}
