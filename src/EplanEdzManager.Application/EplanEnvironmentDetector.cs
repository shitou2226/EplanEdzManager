using System.Diagnostics;
using Microsoft.Win32;

namespace EplanEdzManager.Application;

public sealed class EplanEnvironmentDetector
{
    private static readonly string[] ExecutableNames = { "EPLAN.exe" };
    private static readonly string[] ApiAssemblyNames = { "Eplan.EplApi.AFu.dll", "Eplan.EplApi.Baseu.dll" };

    public EplanEnvironmentInfo Detect(ApplicationSettings? settings = null, bool includeMachineDiscovery = true)
    {
        settings ??= new ApplicationSettings();
        var configured = CreateCandidate(settings.EplanPlatformBinDirectory, settings.EplanVariantBinDirectory, "Application settings");
        if (configured is not null)
            return InspectCandidate(configured);

        if (includeMachineDiscovery)
        {
            var environment = CreateCandidate(
                Environment.GetEnvironmentVariable("EPLAN29_PLATFORM_BIN_DIR"),
                Environment.GetEnvironmentVariable("EPLAN29_VARIANT_BIN_DIR"),
                "Environment variables");
            if (environment is not null)
                return InspectCandidate(environment);

            var candidates = new List<DetectionCandidate>();
            candidates.AddRange(ReadRegistryCandidates());

            foreach (var candidate in candidates.DistinctBy(item => (item.Platform, item.Variant), CandidateComparer.Instance))
            {
                var inspected = InspectCandidate(candidate);
                if (inspected.Detected) return inspected;
            }
        }

        return new EplanEnvironmentInfo(false, "None", string.Empty, string.Empty, string.Empty, string.Empty,
            Environment.Is64BitOperatingSystem, EplanCompatibility.Offline,
            new[] { "EPLAN was not detected. Catalog, Search, My Library and resource export remain available in Offline Mode." });
    }

    public static EplanCompatibility ClassifyCompatibility(string? version)
    {
        if (string.IsNullOrWhiteSpace(version)) return EplanCompatibility.InvalidConfiguration;
        if (string.Equals(NormalizeVersion(version), ProductInfo.VerifiedEplanVersion, StringComparison.Ordinal))
            return EplanCompatibility.Verified;
        if (Version.TryParse(NormalizeVersion(version), out var parsed) && parsed.Major == 2 && parsed.Minor == 9)
            return EplanCompatibility.DetectedButUnverified;
        return EplanCompatibility.Unsupported;
    }

    public static bool IsBridgeAllowed(EplanCompatibility compatibility) => compatibility == EplanCompatibility.Verified;

    private static EplanEnvironmentInfo InspectCandidate(DetectionCandidate candidate)
    {
        var platform = NormalizeDirectory(candidate.Platform);
        var variant = NormalizeDirectory(candidate.Variant);
        var executable = string.IsNullOrWhiteSpace(platform)
            ? null
            : ExecutableNames.Select(name => Path.Combine(platform, name)).FirstOrDefault(File.Exists);
        var api = string.IsNullOrWhiteSpace(platform)
            ? null
            : ApiAssemblyNames.Select(name => Path.Combine(platform, name)).FirstOrDefault(File.Exists);
        if (api is null && !string.IsNullOrWhiteSpace(variant))
            api = ApiAssemblyNames.Select(name => Path.Combine(variant, name)).FirstOrDefault(File.Exists);
        if (executable is null || api is null)
        {
            return new EplanEnvironmentInfo(false, candidate.Source, string.Empty, platform, variant, string.Empty,
                Environment.Is64BitOperatingSystem, EplanCompatibility.InvalidConfiguration,
                new[] { "EPLAN.exe or required EPLAN API assemblies were not found in the detected Bin directories." });
        }

        var eplanVersion = ReadFileVersion(executable);
        var apiVersion = ReadFileVersion(api);
        var compatibility = ClassifyCompatibility(eplanVersion);
        var messages = new List<string>();
        var variantMissing = !Directory.Exists(variant);
        var versionMismatch = !string.IsNullOrWhiteSpace(apiVersion)
            && !string.Equals(NormalizeVersion(apiVersion), NormalizeVersion(eplanVersion), StringComparison.Ordinal);
        if (variantMissing) messages.Add("The EPLAN Electric P8 variant Bin directory was not found.");
        if (versionMismatch)
            messages.Add("EPLAN executable and API file versions differ; Bridge use is disabled until the paths are corrected.");
        if (variantMissing || versionMismatch) compatibility = EplanCompatibility.InvalidConfiguration;
        if (compatibility == EplanCompatibility.DetectedButUnverified)
            messages.Add("This is an EPLAN 2.9.x installation, but only 2.9.4.14642 is verified by this beta.");
        if (compatibility == EplanCompatibility.Unsupported)
            messages.Add("EPLAN 2022+ and non-2.9 releases are unsupported by this build.");
        if (messages.Count == 0) messages.Add("EPLAN Platform and API metadata passed the local detection checks.");
        return new EplanEnvironmentInfo(true, candidate.Source, eplanVersion, platform, variant, apiVersion,
            Environment.Is64BitOperatingSystem, compatibility, messages);
    }

    private static IEnumerable<DetectionCandidate> ReadRegistryCandidates()
    {
        if (!OperatingSystem.IsWindows()) yield break;
        var views = Environment.Is64BitOperatingSystem
            ? new[] { RegistryView.Registry64, RegistryView.Registry32 }
            : new[] { RegistryView.Registry32 };
        foreach (var hive in new[] { RegistryHive.LocalMachine, RegistryHive.CurrentUser })
        foreach (var view in views)
        {
            RegistryKey? baseKey = null;
            RegistryKey? uninstall = null;
            try
            {
                baseKey = RegistryKey.OpenBaseKey(hive, view);
                uninstall = baseKey.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall");
                if (uninstall is null) continue;
                foreach (var name in uninstall.GetSubKeyNames())
                {
                    using var key = uninstall.OpenSubKey(name);
                    var displayName = key?.GetValue("DisplayName") as string;
                    if (string.IsNullOrWhiteSpace(displayName)
                        || displayName.IndexOf("EPLAN", StringComparison.OrdinalIgnoreCase) < 0
                        || displayName.IndexOf("2.9", StringComparison.OrdinalIgnoreCase) < 0) continue;
                    var location = key?.GetValue("InstallLocation") as string;
                    var icon = key?.GetValue("DisplayIcon") as string;
                    foreach (var candidate in ExpandRegistryLocation(location, icon)) yield return candidate;
                }
            }
            finally
            {
                uninstall?.Dispose();
                baseKey?.Dispose();
            }
        }
    }

    private static IEnumerable<DetectionCandidate> ExpandRegistryLocation(string? installLocation, string? displayIcon)
    {
        var locations = new List<string>();
        if (!string.IsNullOrWhiteSpace(installLocation)) locations.Add(installLocation.Trim(' ', '"'));
        if (!string.IsNullOrWhiteSpace(displayIcon))
        {
            var iconPath = displayIcon.Split(',')[0].Trim(' ', '"');
            var iconDirectory = Path.GetDirectoryName(iconPath);
            if (!string.IsNullOrWhiteSpace(iconDirectory)) locations.Add(iconDirectory);
        }

        foreach (var location in locations)
        {
            var full = NormalizeDirectory(location);
            var platform = Directory.Exists(Path.Combine(full, "Bin")) ? Path.Combine(full, "Bin") : full;
            var variant = TryFindVariantSibling(platform);
            yield return new DetectionCandidate(platform, variant, "Windows registry");
        }
    }

    private static string TryFindVariantSibling(string platform)
    {
        try
        {
            var bin = new DirectoryInfo(platform);
            var version = bin.Name.Equals("Bin", StringComparison.OrdinalIgnoreCase) ? bin.Parent : null;
            var platformDirectory = version?.Parent;
            var root = platformDirectory?.Parent;
            if (root is null || version is null) return string.Empty;
            return Path.Combine(root.FullName, "Electric P8", version.Name, "Bin");
        }
        catch (Exception) when (platform.Length > 0)
        {
            return string.Empty;
        }
    }

    private static DetectionCandidate? CreateCandidate(string? platform, string? variant, string source)
    {
        if (string.IsNullOrWhiteSpace(platform) && string.IsNullOrWhiteSpace(variant)) return null;
        return new DetectionCandidate(NormalizeDirectory(platform), NormalizeDirectory(variant), source);
    }

    private static string NormalizeDirectory(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return string.Empty;
        try { return Path.TrimEndingDirectorySeparator(Path.GetFullPath(path.Trim(' ', '"'))); }
        catch (Exception) when (path.Length > 0) { return path.Trim(); }
    }

    private static string ReadFileVersion(string path)
    {
        try { return NormalizeVersion(FileVersionInfo.GetVersionInfo(path).FileVersion); }
        catch (Exception) { return string.Empty; }
    }

    private static string NormalizeVersion(string? value) => (value ?? string.Empty).Trim().Replace(',', '.').Split(' ')[0];

    private sealed record DetectionCandidate(string Platform, string Variant, string Source);

    private sealed class CandidateComparer : IEqualityComparer<(string Platform, string Variant)>
    {
        public static CandidateComparer Instance { get; } = new();
        public bool Equals((string Platform, string Variant) x, (string Platform, string Variant) y) =>
            string.Equals(x.Platform, y.Platform, StringComparison.OrdinalIgnoreCase)
            && string.Equals(x.Variant, y.Variant, StringComparison.OrdinalIgnoreCase);
        public int GetHashCode((string Platform, string Variant) obj) =>
            HashCode.Combine(StringComparer.OrdinalIgnoreCase.GetHashCode(obj.Platform), StringComparer.OrdinalIgnoreCase.GetHashCode(obj.Variant));
    }
}
