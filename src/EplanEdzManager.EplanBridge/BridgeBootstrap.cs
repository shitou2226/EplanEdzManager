using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Xml;
using System.Xml.Linq;
using Newtonsoft.Json;

namespace EplanEdzManager.EplanBridge;

internal sealed class BridgeBootstrapOptions
{
    public string PipeName { get; private set; } = string.Empty;
    public string SessionId { get; private set; } = string.Empty;
    public string VariantBinDirectory { get; private set; } = string.Empty;
    public string PlatformBinDirectory { get; private set; } = string.Empty;
    public string TargetMasterDataRoot { get; private set; } = string.Empty;

    public static BridgeBootstrapOptions Parse(string[] args)
    {
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        for (var index = 0; index < args.Length; index++)
        {
            if (!args[index].StartsWith("--", StringComparison.Ordinal) || index + 1 >= args.Length)
                throw new ArgumentException("Unknown or incomplete Bridge argument: " + args[index]);
            values[args[index]] = args[++index];
        }
        var pipe = Required(values, "--pipe-name");
        var session = Required(values, "--session-id");
        if (!Guid.TryParseExact(session, "N", out _)) throw new ArgumentException("session-id must be a GUID in N format.");
        if (!pipe.StartsWith("EplanEdzManager.S", StringComparison.Ordinal) || pipe.IndexOfAny(new[] { '\\', '/', ':' }) >= 0)
            throw new ArgumentException("The generated pipe name is invalid.");
        var variant = Path.GetFullPath(Required(values, "--variant-bin"));
        var platform = Path.GetFullPath(Required(values, "--platform-bin"));
        var targetMasterDataRoot = values.TryGetValue("--target-master-data-root", out var suppliedTarget) && !string.IsNullOrWhiteSpace(suppliedTarget)
            ? Path.GetFullPath(suppliedTarget)
            : string.Empty;
        if (!Directory.Exists(variant)) throw new DirectoryNotFoundException(variant);
        if (!Directory.Exists(platform)) throw new DirectoryNotFoundException(platform);
        if (!string.IsNullOrWhiteSpace(targetMasterDataRoot) && !Directory.Exists(targetMasterDataRoot))
            throw new DirectoryNotFoundException(targetMasterDataRoot);
        return new BridgeBootstrapOptions
        {
            PipeName = pipe,
            SessionId = session,
            VariantBinDirectory = variant,
            PlatformBinDirectory = platform,
            TargetMasterDataRoot = targetMasterDataRoot
        };
    }

    private static string Required(IDictionary<string, string> values, string name) =>
        values.TryGetValue(name, out var value) && !string.IsNullOrWhiteSpace(value)
            ? value
            : throw new ArgumentException("Required Bridge argument missing: " + name);
}

internal static class BridgeBootstrap
{
    public const string SchemeName = "EplanEdzManagerTemp";
    public const string TargetSchemeName = "EplanEdzManagerTarget";
    public const string SessionMarkerFileName = ".eplan-edz-manager-session.json";

    public static string CreateTemporaryRoot(string sessionId)
    {
        var approvedBase = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "EplanEdzManager", "Bridge"));
        Directory.CreateDirectory(approvedBase);
        var root = Path.GetFullPath(Path.Combine(approvedBase, sessionId));
        if (!root.StartsWith(approvedBase + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Temporary root escaped the approved Bridge base.");
        Directory.CreateDirectory(root);
        var marker = new
        {
            sessionId,
            pid = Process.GetCurrentProcess().Id,
            createdUtc = DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture),
            appVersion = Assembly.GetExecutingAssembly().GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
                ?? Assembly.GetExecutingAssembly().GetName().Version?.ToString()
                ?? "unknown",
            component = "EplanBridge"
        };
        File.WriteAllText(Path.Combine(root, SessionMarkerFileName), JsonConvert.SerializeObject(marker, Newtonsoft.Json.Formatting.Indented), new UTF8Encoding(false));
        return root;
    }

    public static string PrepareTemporaryVariant(string sourceVariantBin, string temporaryRoot, string targetMasterDataRoot)
    {
        var sourceVariantRoot = Directory.GetParent(sourceVariantBin)?.FullName ?? throw new DirectoryNotFoundException(sourceVariantBin);
        var targetVariantRoot = Path.Combine(temporaryRoot, "Variant", "Electric P8", "2.9.4");
        CopyDirectory(sourceVariantRoot, targetVariantRoot);
        var settingsPath = Path.Combine(temporaryRoot, "Settings", "Cfg");
        var dataPath = Path.Combine(temporaryRoot, "EplanData");
        var rightsPath = Path.Combine(temporaryRoot, "Rights");
        Directory.CreateDirectory(settingsPath);
        Directory.CreateDirectory(dataPath);
        Directory.CreateDirectory(rightsPath);
        RewriteSystemConfiguration(
            Path.Combine(targetVariantRoot, "Cfg", "SystemConfiguration.xml"),
            settingsPath,
            dataPath,
            rightsPath,
            targetMasterDataRoot);
        return Path.Combine(targetVariantRoot, "Bin");
    }

    public static bool TryDeleteTemporaryRoot(string temporaryRoot, out string warning)
    {
        warning = string.Empty;
        try
        {
            var approvedBase = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "EplanEdzManager", "Bridge")).TrimEnd(Path.DirectorySeparatorChar);
            var target = Path.GetFullPath(temporaryRoot).TrimEnd(Path.DirectorySeparatorChar);
            if (!target.StartsWith(approvedBase + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Cleanup safety gate rejected a path outside the Bridge temp base.");
            if (!Directory.Exists(target)) return true;
            var reparse = Directory.EnumerateFileSystemEntries(target, "*", SearchOption.AllDirectories)
                .Select(path => new FileInfo(path)).FirstOrDefault(info => (info.Attributes & FileAttributes.ReparsePoint) != 0);
            if (reparse != null) throw new IOException("Cleanup safety gate found a reparse point: " + reparse.FullName);
            Directory.Delete(target, true);
            return !Directory.Exists(target);
        }
        catch (Exception exception)
        {
            warning = exception.GetType().Name + ": " + exception.Message;
            return false;
        }
    }

    private static void RewriteSystemConfiguration(
        string configurationPath,
        string settingsPath,
        string dataPath,
        string rightsPath,
        string targetMasterDataRoot)
    {
        XDocument document;
        var settings = new XmlReaderSettings
        {
            DtdProcessing = DtdProcessing.Prohibit,
            XmlResolver = null,
            MaxCharactersInDocument = 64L * 1024 * 1024,
            IgnoreComments = false,
            IgnoreWhitespace = false
        };
        using (var reader = XmlReader.Create(configurationPath, settings))
            document = XDocument.Load(reader, LoadOptions.PreserveWhitespace);
        var container = document.Descendants("LEV2")
            .Single(element => string.Equals((string?)element.Attribute("name"), "SystemConfiguration", StringComparison.Ordinal));
        var sourceScheme = container.Elements("LEV3")
            .First(element => string.Equals((string?)element.Attribute("name"), "Standard", StringComparison.Ordinal));
        foreach (var existing in container.Elements("LEV3").Where(element =>
                     string.Equals((string?)element.Attribute("name"), SchemeName, StringComparison.Ordinal) ||
                     string.Equals((string?)element.Attribute("name"), TargetSchemeName, StringComparison.Ordinal)).ToList()) existing.Remove();
        var temporaryScheme = new XElement(sourceScheme);
        temporaryScheme.SetAttributeValue("name", SchemeName);
        SetSettingValue(temporaryScheme, "UserSettingsPath", settingsPath);
        SetSettingValue(temporaryScheme, "StationSettingsPath", settingsPath);
        SetSettingValue(temporaryScheme, "CompanySettingsPath", settingsPath);
        SetSettingValue(temporaryScheme, "EplanDataPath", dataPath);
        SetSettingValue(temporaryScheme, "RightsDbPath", rightsPath);
        container.Add(temporaryScheme);
        if (!string.IsNullOrWhiteSpace(targetMasterDataRoot))
        {
            var targetScheme = new XElement(sourceScheme);
            targetScheme.SetAttributeValue("name", TargetSchemeName);
            SetSettingValue(targetScheme, "UserSettingsPath", settingsPath);
            SetSettingValue(targetScheme, "StationSettingsPath", settingsPath);
            SetSettingValue(targetScheme, "CompanySettingsPath", settingsPath);
            SetSettingValue(targetScheme, "EplanDataPath", Path.GetFullPath(targetMasterDataRoot));
            SetSettingValue(targetScheme, "RightsDbPath", rightsPath);
            container.Add(targetScheme);
        }
        container.Elements("Setting").Single(element => string.Equals((string?)element.Attribute("name"), "LastUsed", StringComparison.Ordinal)).Element("Val")!.Value = SchemeName;
        document.Save(configurationPath);
    }

    private static void SetSettingValue(XElement scheme, string settingName, string value)
    {
        var setting = scheme.Descendants("Setting").Single(element => string.Equals((string?)element.Attribute("name"), settingName, StringComparison.Ordinal));
        (setting.Element("Val") ?? throw new InvalidDataException("Missing Val for " + settingName)).Value = value;
    }

    private static void CopyDirectory(string source, string destination)
    {
        Directory.CreateDirectory(destination);
        foreach (var directory in Directory.GetDirectories(source, "*", SearchOption.AllDirectories))
        {
            var relative = directory.Substring(source.Length).TrimStart(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            Directory.CreateDirectory(Path.Combine(destination, relative));
        }
        foreach (var file in Directory.GetFiles(source, "*", SearchOption.AllDirectories))
        {
            var relative = file.Substring(source.Length).TrimStart(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            var target = Path.Combine(destination, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(file, target, false);
        }
    }
}
