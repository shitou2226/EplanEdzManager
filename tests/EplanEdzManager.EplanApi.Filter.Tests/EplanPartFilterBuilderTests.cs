using System;
using System.Linq;
using EplanEdzManager.EplanApi;
using Xunit;

namespace EplanEdzManager.EplanApi.Filter.Tests;

public sealed class EplanPartFilterBuilderTests
{
    [Fact]
    public void Escapes_apostrophes_and_preserves_supported_special_characters()
    {
        var batches = new EplanPartFilterBuilder().BuildBatches(new[]
        {
            new EplanPartFilterTerm
            {
                Manufacturer = "厂`商 Ω-_/\"'",
                PartNumber = "P`中文 01-_/\"'",
                Variant = "V`中文 01-_/\"'"
            }
        });

        Assert.Single(batches);
        Assert.Equal("(manufacturer='厂`商 Ω-_/\"''' AND partnr='P`中文 01-_/\"''')", batches[0]);
    }

    [Theory]
    [InlineData(1, 1)]
    [InlineData(2, 1)]
    [InlineData(10, 1)]
    [InlineData(100, 1)]
    [InlineData(500, 5)]
    public void Batches_by_the_documented_safe_term_limit(int count, int expectedBatches)
    {
        var terms = Enumerable.Range(1, count)
            .Select(index => new EplanPartFilterTerm { Manufacturer = "M", PartNumber = "P" + index.ToString("D4") })
            .ToArray();

        var batches = new EplanPartFilterBuilder().BuildBatches(terms);

        Assert.Equal(expectedBatches, batches.Count);
        Assert.All(batches, filter => Assert.True(filter.Length <= EplanPartFilterBuilder.DefaultMaximumLength));
    }

    [Fact]
    public void Output_is_deterministic_and_duplicate_terms_are_removed()
    {
        var builder = new EplanPartFilterBuilder();
        var forward = new[]
        {
            new EplanPartFilterTerm { Manufacturer = "B", PartNumber = "2" },
            new EplanPartFilterTerm { Manufacturer = "A", PartNumber = "1" },
            new EplanPartFilterTerm { Manufacturer = "A", PartNumber = "1" }
        };

        var first = builder.BuildBatches(forward);
        var second = builder.BuildBatches(forward.Reverse());

        Assert.Equal(first, second);
        Assert.Equal("(manufacturer='A' AND partnr='1') OR (manufacturer='B' AND partnr='2')", first.Single());
    }

    [Fact]
    public void Rejects_empty_part_number_and_nul()
    {
        var builder = new EplanPartFilterBuilder();
        Assert.Throws<ArgumentException>(() => builder.BuildBatches(new[] { new EplanPartFilterTerm { PartNumber = " " } }));
        Assert.Throws<ArgumentException>(() => builder.BuildBatches(new[] { new EplanPartFilterTerm { PartNumber = "P\0X" } }));
    }
}
