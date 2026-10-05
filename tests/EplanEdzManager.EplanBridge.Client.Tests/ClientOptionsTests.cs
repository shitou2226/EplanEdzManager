using EplanEdzManager.EplanBridge.Client;
using Xunit;

namespace EplanEdzManager.EplanBridge.Client.Tests;

public sealed class ClientOptionsTests
{
    [Fact]
    public void Validate_rejects_missing_bridge_before_process_start()
    {
        var options = new EplanBridgeClientOptions
        {
            BridgeExecutablePath = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"), "missing.exe"),
            EplanVariantBinDirectory = Path.GetTempPath(),
            EplanPlatformBinDirectory = Path.GetTempPath()
        };
        Assert.Throws<FileNotFoundException>(options.Validate);
    }

    [Fact]
    public void Default_options_use_local_environment_only()
    {
        var options = EplanBridgeClientOptions.CreateDefault(AppContext.BaseDirectory);
        Assert.NotNull(options);
        Assert.True(options.ConnectionTimeout > TimeSpan.Zero);
        Assert.True(options.OperationIdleTimeout > options.ConnectionTimeout);
    }

    [Fact]
    public void Default_options_find_bridge_in_installed_sibling_layout()
    {
        var root = Path.Combine(Path.GetTempPath(), "EplanEdzManager.Tests", "Test User 中文 (portable)-_", Guid.NewGuid().ToString("N"));
        var desktop = Path.Combine(root, "Desktop");
        var bridge = Path.Combine(root, "Bridge");
        Directory.CreateDirectory(desktop);
        Directory.CreateDirectory(bridge);
        var executable = Path.Combine(bridge, "EplanEdzManager.EplanBridge.exe");
        File.WriteAllText(executable, "test");
        var previous = Environment.GetEnvironmentVariable("EPLAN_EDZ_MANAGER_BRIDGE_PATH");
        try
        {
            Environment.SetEnvironmentVariable("EPLAN_EDZ_MANAGER_BRIDGE_PATH", null);
            var options = EplanBridgeClientOptions.CreateDefault(desktop);
            Assert.Equal(executable, options.BridgeExecutablePath, ignoreCase: true);
        }
        finally
        {
            Environment.SetEnvironmentVariable("EPLAN_EDZ_MANAGER_BRIDGE_PATH", previous);
            Directory.Delete(root, recursive: true);
        }
    }
}
