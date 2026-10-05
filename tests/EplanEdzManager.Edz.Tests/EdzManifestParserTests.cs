using System.Text;
using EplanEdzManager.Core.Diagnostics;
using EplanEdzManager.Edz.Manifest;
using Xunit;

namespace EplanEdzManager.Edz.Tests;

public sealed class EdzManifestParserTests
{
    [Fact]
    public void Parse_builds_packages_and_preserves_unknown_manifest_data()
    {
        const string xml = """
            <?xml version="1.0" encoding="utf-8"?>
            <manifest version="2.0" vendor="future">
              <futureRootNode />
              <packages>
                <package type="part" key="P-1" name="Part 1" future="yes">
                  <futurePackageNode answer="42" />
                  <items>
                    <item type="partxml" name="part" locator="P-1.part.xml" extra="kept" />
                    <item type="futuretype" name="future" locator="asset.bin" />
                  </items>
                </package>
              </packages>
            </manifest>
            """;

        var result = Parse(xml);

        Assert.NotNull(result.Manifest);
        Assert.Equal("2.0", result.Manifest.Version);
        Assert.Equal("future", result.Manifest.UnknownAttributes["vendor"]);
        var package = Assert.Single(result.Manifest.Packages);
        Assert.Equal("P-1", package.Key);
        Assert.Equal("yes", package.UnknownAttributes["future"]);
        Assert.Equal(2, package.Items.Count);
        Assert.Equal("items/partxml/P-1.part.xml", package.Items[0].ResolvedLocator?.ArchivePath);
        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Code == "EDZ202");
        Assert.NotEmpty(result.Manifest.UnknownElements);
        Assert.NotEmpty(package.UnknownElements);
    }

    [Fact]
    public void Parse_keeps_item_but_reports_unsafe_locator()
    {
        const string xml = """
            <manifest version="2.0"><packages><package type="part" key="P-1" name="P-1"><items>
              <item type="partxml" name="part" locator="../escape.xml" />
            </items></package></packages></manifest>
            """;

        var result = Parse(xml);

        var item = Assert.Single(Assert.Single(result.Manifest!.Packages).Items);
        Assert.Null(item.ResolvedLocator);
        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Code == "EDZ201");
    }

    [Fact]
    public void Parse_blocks_dtd_and_external_entities()
    {
        const string xml = """
            <!DOCTYPE manifest [<!ENTITY xxe SYSTEM "file:///C:/Windows/win.ini">]>
            <manifest version="2.0"><packages><package type="part" key="P-1" name="&xxe;"><items /></package></packages></manifest>
            """;

        var result = Parse(xml);

        Assert.Null(result.Manifest);
        Assert.Contains(result.Diagnostics, diagnostic =>
            diagnostic.Severity >= DiagnosticSeverity.Error && diagnostic.Code == "EDZ102");
    }

    [Fact]
    public void Parse_returns_diagnostic_for_broken_xml()
    {
        var result = Parse("<manifest><packages>");

        Assert.Null(result.Manifest);
        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Code == "EDZ102");
    }

    [Fact]
    public void Parse_honors_pre_cancelled_token()
    {
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes("<manifest version=\"2.0\"><packages /></manifest>"));
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        Assert.Throws<OperationCanceledException>(() =>
            new EdzManifestParser().Parse(stream, cancellation.Token));
    }

    private static ManifestParseResult Parse(string xml)
    {
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(xml));
        return new EdzManifestParser().Parse(stream, CancellationToken.None);
    }
}

