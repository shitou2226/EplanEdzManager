using EplanEdzManager.AddIn.Protocol;
using Xunit;

namespace EplanEdzManager.EplanAddIn.Tests;

public sealed class CompatibilityAndConfigurationTests
{
    [Theory]
    [InlineData("2.9.4.14642", true)]
    [InlineData("2.9.4.14642 x64", true)]
    [InlineData("2.9.0", false)]
    [InlineData("2.9.3.12345", false)]
    [InlineData("2.8.9", false)]
    [InlineData("2022.0", false)]
    [InlineData("not-a-version", false)]
    [InlineData("", false)]
    public void Only_the_verified_eplan_build_is_supported(string version, bool expected)
    {
        Assert.Equal(expected, EplanVersionCompatibility.IsSupported(version));
    }

    [Fact]
    public void Configuration_accepts_relative_sibling_desktop_path_from_addin_directory()
    {
        var root = Path.Combine(Path.GetTempPath(), "EplanEdzManager.Tests", Guid.NewGuid().ToString("N"), "安装 目录 (Beta)-_");
        var addIn = Path.Combine(root, "AddIn");
        var config = new AddInConfiguration { DesktopExecutablePath = @"..\Desktop\EplanEdzManager.Desktop.exe" };

        var result = config.Validate(addIn);

        Assert.True(result.IsValid);
        Assert.Equal(Path.Combine(root, "Desktop", "EplanEdzManager.Desktop.exe"), result.DesktopExecutablePath, ignoreCase: true);
    }

    [Fact]
    public void Configuration_accepts_absolute_desktop_path_without_searching_drives()
    {
        var path = Path.Combine(Path.GetPathRoot(Path.GetTempPath())!, "Apps", "EplanEdzManager.Desktop.exe");
        var config = new AddInConfiguration { DesktopExecutablePath = path };
        var result = config.Validate();
        Assert.True(result.IsValid);
        Assert.Equal(path, result.DesktopExecutablePath, ignoreCase: true);
    }

    [Fact]
    public void Configuration_rejects_executable_outside_sibling_desktop_directory()
    {
        var root = Path.Combine(Path.GetTempPath(), "EplanEdzManager.Tests", Guid.NewGuid().ToString("N"));
        var config = new AddInConfiguration { DesktopExecutablePath = Path.Combine(root, "Other", "EplanEdzManager.Desktop.exe") };
        var result = config.Validate(Path.Combine(root, "Installed", "AddIn"));
        Assert.False(result.IsValid);
        Assert.Contains("sibling", result.Error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Configuration_rejects_relative_path_without_an_install_root()
    {
        var config = new AddInConfiguration { DesktopExecutablePath = @"..\Desktop\EplanEdzManager.Desktop.exe" };

        var result = config.Validate();

        Assert.False(result.IsValid);
        Assert.Contains("installation directory", result.Error, StringComparison.OrdinalIgnoreCase);
    }
}
