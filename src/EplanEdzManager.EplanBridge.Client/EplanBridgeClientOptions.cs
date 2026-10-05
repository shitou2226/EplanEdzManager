using System.Diagnostics;

namespace EplanEdzManager.EplanBridge.Client;

public sealed class EplanBridgeClientOptions
{
    public string BridgeExecutablePath { get; set; } = string.Empty;
    public string EplanVariantBinDirectory { get; set; } = string.Empty;
    public string EplanPlatformBinDirectory { get; set; } = string.Empty;
    public string TargetMasterDataRoot { get; set; } = string.Empty;
    public TimeSpan ConnectionTimeout { get; set; } = TimeSpan.FromSeconds(30);
    public TimeSpan CapabilityTimeout { get; set; } = TimeSpan.FromMinutes(2);
    public TimeSpan OperationIdleTimeout { get; set; } = TimeSpan.FromMinutes(20);
    public TimeSpan ShutdownTimeout { get; set; } = TimeSpan.FromSeconds(10);

    public static EplanBridgeClientOptions CreateDefault(string? startDirectory = null)
    {
        var executable = Environment.GetEnvironmentVariable("EPLAN_EDZ_MANAGER_BRIDGE_PATH") ?? string.Empty;
        var variant = Environment.GetEnvironmentVariable("EPLAN29_VARIANT_BIN_DIR") ?? string.Empty;
        var platform = Environment.GetEnvironmentVariable("EPLAN29_PLATFORM_BIN_DIR") ?? string.Empty;
        if (string.IsNullOrWhiteSpace(executable))
        {
            executable = FindBridgeExecutable(startDirectory ?? AppContext.BaseDirectory) ?? string.Empty;
        }

        return new EplanBridgeClientOptions
        {
            BridgeExecutablePath = executable,
            EplanVariantBinDirectory = variant,
            EplanPlatformBinDirectory = platform
        };
    }

    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(BridgeExecutablePath) || !File.Exists(BridgeExecutablePath))
            throw new FileNotFoundException("EPLAN Bridge executable was not found in the installed sibling Bridge directory or the explicit EPLAN_EDZ_MANAGER_BRIDGE_PATH.", BridgeExecutablePath);
        if (string.IsNullOrWhiteSpace(EplanVariantBinDirectory) || !Directory.Exists(EplanVariantBinDirectory))
            throw new DirectoryNotFoundException("EPLAN 2.9 variant bin directory was not found: " + EplanVariantBinDirectory);
        if (string.IsNullOrWhiteSpace(EplanPlatformBinDirectory) || !Directory.Exists(EplanPlatformBinDirectory))
            throw new DirectoryNotFoundException("EPLAN 2.9 platform bin directory was not found: " + EplanPlatformBinDirectory);
        if (!string.IsNullOrWhiteSpace(TargetMasterDataRoot) && !Directory.Exists(TargetMasterDataRoot))
            throw new DirectoryNotFoundException("The explicitly selected target EPLAN master-data root was not found: " + TargetMasterDataRoot);
        if (ConnectionTimeout <= TimeSpan.Zero || CapabilityTimeout <= TimeSpan.Zero || OperationIdleTimeout <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(ConnectionTimeout), "Bridge timeouts must be positive.");
    }

    internal static string CreatePipeName()
    {
        var session = Process.GetCurrentProcess().SessionId;
        return $"EplanEdzManager.S{session}.{Guid.NewGuid():N}";
    }

    private static string? FindBridgeExecutable(string startDirectory)
    {
        var direct = Path.Combine(startDirectory, "EplanEdzManager.EplanBridge.exe");
        if (File.Exists(direct)) return direct;

        var installedSibling = Path.GetFullPath(Path.Combine(startDirectory, "..", "Bridge", "EplanEdzManager.EplanBridge.exe"));
        if (File.Exists(installedSibling)) return installedSibling;

        return null;
    }
}
