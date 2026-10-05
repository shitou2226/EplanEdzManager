using EplanEdzManager.Core;
using Xunit;

namespace EplanEdzManager.Core.Tests;

public sealed class EdzLocatorTests
{
    [Fact]
    public void TryCreate_normalizes_safe_relative_locator()
    {
        var created = EdzLocator.TryCreate("partxml", "folder\\A.part.xml", out var locator, out var diagnostic);

        Assert.True(created);
        Assert.NotNull(locator);
        Assert.Null(diagnostic);
        Assert.Equal("folder/A.part.xml", locator.Locator);
        Assert.Equal("items/partxml/folder/A.part.xml", locator.ArchivePath);
    }

    [Theory]
    [InlineData("../secret.xml")]
    [InlineData("..\\secret.xml")]
    [InlineData("/absolute.xml")]
    [InlineData("\\\\server\\share.xml")]
    [InlineData("C:\\secret.xml")]
    [InlineData("folder//file.xml")]
    [InlineData("%2e%2e%2fsecret.xml")]
    [InlineData("%252e%252e%255csecret.xml")]
    public void TryCreate_rejects_dangerous_locator(string value)
    {
        var created = EdzLocator.TryCreate("partxml", value, out var locator, out var diagnostic);

        Assert.False(created);
        Assert.Null(locator);
        Assert.Equal("EDZ201", diagnostic?.Code);
    }

    [Fact]
    public void TryCreate_preserves_unicode_after_normalization()
    {
        var created = EdzLocator.TryCreate("picture", "图像/产品.jpg", out var locator, out _);

        Assert.True(created);
        Assert.Equal("items/picture/图像/产品.jpg", locator?.ArchivePath);
    }

    [Fact]
    public void TryCreate_allows_safe_literal_percent_encoded_filename_but_does_not_decode_it()
    {
        var created = EdzLocator.TryCreate("partxml", "220V%2F220V,300VA.part.xml", out var locator, out _);

        Assert.True(created);
        Assert.Equal("items/partxml/220V%2F220V,300VA.part.xml", locator?.ArchivePath);
    }
}

