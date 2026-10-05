using System.Text;
using EplanEdzManager.Core.Diagnostics;
using EplanEdzManager.Core.Model;
using EplanEdzManager.Edz.Parts;
using Xunit;

namespace EplanEdzManager.Edz.Tests;

public sealed class PartXmlMetadataReaderTests
{
    [Fact]
    public void Read_returns_required_phase_one_metadata_and_preserves_unknowns()
    {
        const string xml = """
            <?xml version="1.0" encoding="utf-8"?>
            <partsmanagement count="1" type="EPLAN.PartsManagement">
              <part P_ARTICLE_PARTNR="P-1"
                    P_ARTICLE_MANUFACTURER="MFR"
                    P_ARTICLE_TYPENR="TYPE-A"
                    P_ARTICLE_ORDERNR="ORDER-1"
                    P_ARTICLE_DESCR1="Description"
                    P_ARTICLE_PRODUCTTOPGROUP="T"
                    P_ARTICLE_PRODUCTGROUP="G"
                    P_ARTICLE_PRODUCTSUBGROUP="S"
                    P_FUTURE="preserved">
                <variant P_ARTICLE_VARIANT="1"><functiontemplate /></variant>
                <futureMetadata value="42" />
              </part>
            </partsmanagement>
            """;

        var result = Read(xml);

        Assert.NotNull(result.Part);
        Assert.Equal("MFR", result.Part.Manufacturer);
        Assert.Equal("P-1", result.Part.PartNumber);
        Assert.Equal("TYPE-A", result.Part.TypeNumber);
        Assert.Equal("ORDER-1", result.Part.OrderNumber);
        Assert.Equal("Description", result.Part.Description);
        Assert.Equal("T/G/S", result.Part.ProductGroup);
        Assert.Equal("1", result.Part.Variant);
        Assert.Equal("PKG-1", result.Part.PackageKey);
        Assert.Equal("sample.edz", result.Part.SourceEdz);
        Assert.Equal("items/partxml/P-1.part.xml", result.Part.RawMetadataReference);
        Assert.Equal("preserved", result.Part.UnknownAttributes["P_FUTURE"]);
        Assert.NotEmpty(result.Part.UnknownElements);
    }

    [Fact]
    public void Read_allows_missing_optional_fields()
    {
        const string xml = "<partsmanagement><part P_ARTICLE_PARTNR=\"P-2\"><variant /></part></partsmanagement>";

        var result = Read(xml);

        Assert.NotNull(result.Part);
        Assert.Null(result.Part.Manufacturer);
        Assert.Null(result.Part.TypeNumber);
        Assert.Null(result.Part.Description);
    }

    [Fact]
    public void Read_blocks_dtd_and_returns_diagnostic()
    {
        const string xml = "<!DOCTYPE x [<!ENTITY xxe SYSTEM \"file:///C:/Windows/win.ini\">]><partsmanagement><part P_ARTICLE_PARTNR=\"&xxe;\" /></partsmanagement>";

        var result = Read(xml);

        Assert.Null(result.Part);
        Assert.Contains(result.Diagnostics, diagnostic =>
            diagnostic.Code == "EDZ401" && diagnostic.Severity >= DiagnosticSeverity.Error);
    }

    private static PartMetadataReadResult Read(string xml)
    {
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(xml));
        return new PartXmlMetadataReader().Read(
            stream,
            "PKG-1",
            "sample.edz",
            "items/partxml/P-1.part.xml",
            CancellationToken.None);
    }
}

