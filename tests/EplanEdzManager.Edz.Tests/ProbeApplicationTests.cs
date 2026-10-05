using System.Text.Json;
using EplanEdzProbe;
using Xunit;

namespace EplanEdzManager.Edz.Tests;

public sealed class ProbeApplicationTests
{
    [Fact]
    [Trait("Category", "Integration")]
    public async Task Inspect_json_uses_stable_envelope_and_real_archive_counts()
    {
        using var output = new StringWriter();
        using var error = new StringWriter();

        var exitCode = await new ProbeApplication().RunAsync(
            new[] { "inspect", TestPaths.Sample("明纬.edz"), "--json" },
            output,
            error,
            CancellationToken.None);

        using var json = JsonDocument.Parse(output.ToString());
        Assert.Equal(0, exitCode);
        Assert.Equal("1.0", json.RootElement.GetProperty("schemaVersion").GetString());
        Assert.Equal("inspect", json.RootElement.GetProperty("command").GetString());
        Assert.True(json.RootElement.GetProperty("success").GetBoolean());
        Assert.Equal(69, json.RootElement.GetProperty("data").GetProperty("entryCount").GetInt32());
        Assert.Equal(26, json.RootElement.GetProperty("data").GetProperty("packageCount").GetInt32());
        Assert.Equal(string.Empty, error.ToString());
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task Parts_json_honors_limit_without_parsing_every_part()
    {
        using var output = new StringWriter();
        using var error = new StringWriter();

        var exitCode = await new ProbeApplication().RunAsync(
            new[] { "parts", TestPaths.Sample("明纬.edz"), "--limit", "2", "--json" },
            output,
            error,
            CancellationToken.None);

        using var json = JsonDocument.Parse(output.ToString());
        Assert.Equal(0, exitCode);
        Assert.Equal(2, json.RootElement.GetProperty("data").GetArrayLength());
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task Parts_human_output_has_readable_table_columns()
    {
        using var output = new StringWriter();
        using var error = new StringWriter();

        var exitCode = await new ProbeApplication().RunAsync(
            new[] { "parts", TestPaths.Sample("明纬.edz"), "--limit", "1" },
            output,
            error,
            CancellationToken.None);

        Assert.Equal(0, exitCode);
        Assert.Contains("Manufacturer\tPartNumber\tTypeNumber\tDescription", output.ToString());
    }
}

