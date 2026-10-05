using System.Xml.Linq;

namespace EplanEdzManager.EplanBridge.IntegrationTests;

internal static class EplanIntegrationEnvironment
{
    private static readonly Lazy<IReadOnlyDictionary<string, string>> LocalProperties = new(ReadLocalProperties);

    public static string PlatformBinDirectory => ResolveDirectory("EPLAN29_PLATFORM_BIN_DIR", "Eplan29PlatformBinDir");
    public static string VariantBinDirectory => ResolveDirectory("EPLAN29_VARIANT_BIN_DIR", "Eplan29VariantBinDir");

    public static string Fixture(string name)
    {
        var configured = Environment.GetEnvironmentVariable("EPLAN_EDZ_TEST_FIXTURE_DIR");
        var directory = string.IsNullOrWhiteSpace(configured)
            ? Path.Combine(FindRepositoryRoot(), "samples")
            : Path.GetFullPath(configured);
        var path = Path.Combine(directory, name);
        if (!File.Exists(path)) throw new FileNotFoundException("Provide a private local fixture through EPLAN_EDZ_TEST_FIXTURE_DIR.", path);
        return path;
    }

    private static string ResolveDirectory(string environmentName, string propertyName)
    {
        var value = Environment.GetEnvironmentVariable(environmentName);
        if (string.IsNullOrWhiteSpace(value)) LocalProperties.Value.TryGetValue(propertyName, out value);
        if (string.IsNullOrWhiteSpace(value))
            throw new InvalidOperationException($"Set {environmentName} or {propertyName} in the git-ignored Directory.Build.props.local before running real EPLAN integration tests.");
        var fullPath = Path.GetFullPath(value);
        if (!Directory.Exists(fullPath)) throw new DirectoryNotFoundException(fullPath);
        return fullPath;
    }

    private static IReadOnlyDictionary<string, string> ReadLocalProperties()
    {
        var path = Path.Combine(FindRepositoryRoot(), "Directory.Build.props.local");
        if (!File.Exists(path)) return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var document = XDocument.Load(path, LoadOptions.None);
        return document.Descendants()
            .Where(element => element.Name.LocalName is "Eplan29PlatformBinDir" or "Eplan29VariantBinDir")
            .GroupBy(element => element.Name.LocalName, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.Last().Value.Trim(), StringComparer.OrdinalIgnoreCase);
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "EplanEdzManager.sln"))) return directory.FullName;
            directory = directory.Parent;
        }
        throw new DirectoryNotFoundException("Repository root was not found from " + AppContext.BaseDirectory);
    }
}
