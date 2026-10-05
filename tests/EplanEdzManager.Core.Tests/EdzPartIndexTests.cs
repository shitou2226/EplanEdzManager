using EplanEdzManager.Core.Model;
using Xunit;

namespace EplanEdzManager.Core.Tests;

public sealed class EdzPartIndexTests
{
    [Fact]
    public void Exact_package_lookup_materializes_only_requested_part()
    {
        var loads = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var index = CreateIndex(loads);

        var result = index.FindExact("CJ2M-CPU", CancellationToken.None);

        Assert.Equal("CJ2M-CPU", result?.PartNumber);
        Assert.Equal(1, loads["CJ2M-CPU"]);
        Assert.False(loads.ContainsKey("DR-100"));
    }

    [Fact]
    public void Contains_search_is_case_insensitive_across_supported_fields()
    {
        var index = CreateIndex(new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase));

        var byManufacturer = index.Search("omron", 20, CancellationToken.None);
        var byType = index.Search("power", 20, CancellationToken.None);

        Assert.Contains(byManufacturer, part => part.PartNumber == "CJ2M-CPU");
        Assert.Contains(byType, part => part.PartNumber == "DR-100");
    }

    [Fact]
    public void Search_prefilters_package_keys_before_loading_unrelated_metadata()
    {
        var loads = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var index = CreateIndex(loads);

        var results = index.Search("dr-100", 1, CancellationToken.None);

        Assert.Single(results);
        Assert.Equal(1, loads["DR-100"]);
        Assert.False(loads.ContainsKey("CJ2M-CPU"));
    }

    private static EdzPartIndex CreateIndex(IDictionary<string, int> loads)
    {
        return new EdzPartIndex(new[]
        {
            Entry("CJ2M-CPU", "OMRON", "Controller", loads),
            Entry("DR-100", "MEAN WELL", "Power Supply", loads)
        });
    }

    private static LazyPartIndexEntry Entry(string partNumber, string manufacturer, string typeNumber, IDictionary<string, int> loads)
    {
        return new LazyPartIndexEntry(partNumber, partNumber, cancellationToken =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            loads[partNumber] = loads.TryGetValue(partNumber, out var count) ? count + 1 : 1;
            return new PartMetadataReadResult(
                new PartRecord(manufacturer, partNumber, typeNumber, null, null, null, Array.Empty<string>(), partNumber, "sample.edz", partNumber + ".xml", new Dictionary<string, string>(), Array.Empty<UnknownXmlElement>()),
                Array.Empty<EplanEdzManager.Core.Diagnostics.DiagnosticRecord>());
        });
    }
}

