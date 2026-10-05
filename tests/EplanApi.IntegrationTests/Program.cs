using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text;
using System.Web.Script.Serialization;
using System.Xml.Linq;
using Eplan.EplApi.Starter;

namespace EplanApi.IntegrationTests;

internal static class Program
{
    private const string SchemeName = "EplanEdzManagerTemp";

    [STAThread]
    private static int Main(string[] args)
    {
        BootstrapOptions? options = null;
        string? temporaryRoot = null;
        try
        {
            options = BootstrapOptions.Parse(args);
            Directory.CreateDirectory(options.OutputDirectory);

            temporaryRoot = CreateUniqueTemporaryRoot();
            var temporaryVariantBin = PrepareTemporaryVariant(
                options.VariantBinDirectory,
                temporaryRoot,
                SchemeName);

            var resolver = new AssemblyResolver();
            resolver.SetBinPaths(options.PlatformBinDirectory, temporaryVariantBin);
            resolver.PinToEplan();

            Console.WriteLine("EPLAN 2.9 assemblies pinned from: " + resolver.GetPlatformBinPath());
            Console.WriteLine("Temporary variant bin: " + temporaryVariantBin);
            Console.WriteLine("Original EDZ remains read-only: " + options.SourceEdzPath);
            return RunAfterPin(options, temporaryRoot, temporaryVariantBin);
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine(exception);
            if (options != null)
            {
                WriteBootstrapFailure(options.OutputDirectory, temporaryRoot ?? string.Empty, exception);
            }

            if (!string.IsNullOrWhiteSpace(temporaryRoot))
            {
                TryDeleteTemporaryRoot(temporaryRoot!);
            }

            return 1;
        }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static int RunAfterPin(BootstrapOptions options, string temporaryRoot, string temporaryVariantBin)
    {
        if (options.Phase5)
        {
            return Phase5ImportSmoke.Run(
                options.SourceEdzPath,
                options.OutputDirectory,
                temporaryRoot,
                temporaryVariantBin,
                options.PlatformBinDirectory,
                SchemeName);
        }

        var phaseOptions = new EplanEdzManager.EplanApi.Phase15Options
        {
            EplanVariantBinDirectory = temporaryVariantBin,
            EplanPlatformBinDirectory = options.PlatformBinDirectory,
            TemporaryRoot = temporaryRoot,
            OutputDirectory = options.OutputDirectory,
            SourceEdzPath = options.SourceEdzPath,
            SystemConfigurationScheme = SchemeName,
            ExecuteMutatingSmokeTest = options.Execute
        };
        phaseOptions.ExportRequests.Add(new EplanEdzManager.EplanApi.PartExportRequest
        {
            Name = "Test 1 - DR-100-24",
            PartNumber = "DR-100-24",
            OutputFileName = "Generated.edz"
        });
        phaseOptions.ExportRequests.Add(new EplanEdzManager.EplanApi.PartExportRequest
        {
            Name = "Test 2 - single-part filter from multi-part EDZ",
            PartNumber = "DR-120-24",
            OutputFileName = "Selected.edz"
        });

        var exitCode = EplanEdzManager.EplanApi.Phase15SmokeRunner.Execute(phaseOptions);
        Console.WriteLine("Environment report: " + Path.Combine(options.OutputDirectory, "EnvironmentReport.json"));
        Console.WriteLine(exitCode == 0 ? "PHASE 1.5 PASS" : "PHASE 1.5 FAIL");
        return exitCode;
    }

    private static string CreateUniqueTemporaryRoot()
    {
        var baseDirectory = Path.Combine(Path.GetTempPath(), "EplanEdzManager", "TestDatabase");
        Directory.CreateDirectory(baseDirectory);
        var runId = DateTime.UtcNow.ToString("yyyyMMddTHHmmssfff", CultureInfo.InvariantCulture) + "-" +
            Guid.NewGuid().ToString("N");
        var root = Path.Combine(baseDirectory, runId);
        Directory.CreateDirectory(root);
        return root;
    }

    private static string PrepareTemporaryVariant(
        string sourceVariantBin,
        string temporaryRoot,
        string schemeName)
    {
        var sourceVariantRoot = Directory.GetParent(sourceVariantBin)?.FullName ??
            throw new DirectoryNotFoundException("Unable to resolve EPLAN variant root from: " + sourceVariantBin);
        var targetVariantRoot = Path.Combine(temporaryRoot, "Variant", "Electric P8", "2.9.4");
        CopyDirectory(sourceVariantRoot, targetVariantRoot);

        var settingsPath = Path.Combine(temporaryRoot, "Settings", "Cfg");
        var dataPath = Path.Combine(temporaryRoot, "EplanData");
        var rightsPath = Path.Combine(temporaryRoot, "Rights");
        Directory.CreateDirectory(settingsPath);
        Directory.CreateDirectory(dataPath);
        Directory.CreateDirectory(rightsPath);

        var configurationPath = Path.Combine(targetVariantRoot, "Cfg", "SystemConfiguration.xml");
        RewriteSystemConfiguration(configurationPath, schemeName, settingsPath, dataPath, rightsPath);
        return Path.Combine(targetVariantRoot, "Bin");
    }

    private static void RewriteSystemConfiguration(
        string configurationPath,
        string schemeName,
        string settingsPath,
        string dataPath,
        string rightsPath)
    {
        var document = XDocument.Load(configurationPath, LoadOptions.PreserveWhitespace);
        var container = document
            .Descendants("LEV2")
            .Single(element => string.Equals((string?)element.Attribute("name"), "SystemConfiguration", StringComparison.Ordinal));
        var sourceScheme = container
            .Elements("LEV3")
            .First(element => string.Equals((string?)element.Attribute("name"), "Standard", StringComparison.Ordinal));

        foreach (var existing in container.Elements("LEV3").Where(element =>
                     string.Equals((string?)element.Attribute("name"), schemeName, StringComparison.Ordinal)).ToList())
        {
            existing.Remove();
        }

        var temporaryScheme = new XElement(sourceScheme);
        temporaryScheme.SetAttributeValue("name", schemeName);
        SetSettingValue(temporaryScheme, "UserSettingsPath", settingsPath);
        SetSettingValue(temporaryScheme, "StationSettingsPath", settingsPath);
        SetSettingValue(temporaryScheme, "CompanySettingsPath", settingsPath);
        SetSettingValue(temporaryScheme, "EplanDataPath", dataPath);
        SetSettingValue(temporaryScheme, "RightsDbPath", rightsPath);
        container.Add(temporaryScheme);

        var lastUsed = container.Elements("Setting")
            .Single(element => string.Equals((string?)element.Attribute("name"), "LastUsed", StringComparison.Ordinal));
        lastUsed.Element("Val")!.Value = schemeName;
        document.Save(configurationPath);
    }

    private static void SetSettingValue(XElement scheme, string settingName, string value)
    {
        var setting = scheme.Descendants("Setting")
            .Single(element => string.Equals((string?)element.Attribute("name"), settingName, StringComparison.Ordinal));
        var valueElement = setting.Element("Val") ?? throw new InvalidDataException("Missing Val element for " + settingName);
        valueElement.Value = value;
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
            var destinationFile = Path.Combine(destination, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(destinationFile) ?? destination);
            File.Copy(file, destinationFile, false);
        }
    }

    private static void WriteBootstrapFailure(string outputDirectory, string temporaryRoot, Exception exception)
    {
        Directory.CreateDirectory(outputDirectory);
        var payload = new
        {
            SchemaVersion = "1.0",
            StartedUtc = DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture),
            FinishedUtc = DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture),
            OverallVerdict = "FAIL",
            Version = string.Empty,
            Assemblies = Array.Empty<object>(),
            AvailableApi = Array.Empty<string>(),
            MissingCapabilities = new[] { "EPLAN bootstrap/pinning failed: " + FlattenException(exception) },
            TemporaryRoot = temporaryRoot,
            TemporaryRootDeleted = false,
            Steps = new[]
            {
                new
                {
                    Name = "Pin EPLAN 2.9 assemblies",
                    Status = "FAIL",
                    ExceptionType = exception.GetType().FullName,
                    ExceptionMessage = FlattenException(exception),
                    ExceptionStackTrace = exception.ToString()
                }
            }
        };
        var serializer = new JavaScriptSerializer { MaxJsonLength = int.MaxValue };
        File.WriteAllText(
            Path.Combine(outputDirectory, "EnvironmentReport.json"),
            serializer.Serialize(payload),
            new UTF8Encoding(false));
    }

    private static string FlattenException(Exception exception)
    {
        var messages = new List<string>();
        for (var current = exception; current != null; current = current.InnerException)
        {
            messages.Add(current.GetType().Name + ": " + current.Message);
        }

        return string.Join(" --> ", messages);
    }

    private static void TryDeleteTemporaryRoot(string temporaryRoot)
    {
        try
        {
            var expectedBase = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "EplanEdzManager", "TestDatabase"))
                .TrimEnd(Path.DirectorySeparatorChar);
            var normalized = Path.GetFullPath(temporaryRoot).TrimEnd(Path.DirectorySeparatorChar);
            if (!normalized.StartsWith(expectedBase + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            if (Directory.Exists(normalized))
            {
                Directory.Delete(normalized, true);
            }
        }
        catch
        {
            // The durable JSON report contains the bootstrap failure; cleanup is best effort here.
        }
    }

    private sealed class BootstrapOptions
    {
        public string VariantBinDirectory { get; private set; } = string.Empty;

        public string PlatformBinDirectory { get; private set; } = string.Empty;

        public string SourceEdzPath { get; private set; } = string.Empty;

        public string OutputDirectory { get; private set; } = string.Empty;

        public bool Execute { get; private set; }

        public bool Phase5 { get; private set; }

        public static BootstrapOptions Parse(string[] args)
        {
            var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var execute = false;
            var phase5 = false;
            for (var index = 0; index < args.Length; index++)
            {
                var argument = args[index];
                if (string.Equals(argument, "--execute", StringComparison.OrdinalIgnoreCase))
                {
                    execute = true;
                    continue;
                }

                if (string.Equals(argument, "--phase5", StringComparison.OrdinalIgnoreCase))
                {
                    phase5 = true;
                    continue;
                }

                if (!argument.StartsWith("--", StringComparison.Ordinal) || index + 1 >= args.Length)
                {
                    throw new ArgumentException("Unknown or incomplete argument: " + argument);
                }

                values[argument] = args[++index];
            }

            var projectRoot = values.TryGetValue("--project-root", out var suppliedRoot)
                ? Path.GetFullPath(suppliedRoot)
                : FindProjectRoot(Directory.GetCurrentDirectory());
            var variantBin = ReadOption(values, "--variant-bin", "EPLAN29_VARIANT_BIN_DIR");
            var platformBin = ReadOption(values, "--platform-bin", "EPLAN29_PLATFORM_BIN_DIR");
            var sample = values.TryGetValue("--sample", out var suppliedSample)
                ? Path.GetFullPath(suppliedSample)
                : Path.Combine(Environment.GetEnvironmentVariable("EPLAN_EDZ_TEST_FIXTURE_DIR") ?? Path.Combine(projectRoot, "samples"), "明纬.edz");
            var output = values.TryGetValue("--output", out var suppliedOutput)
                ? Path.GetFullPath(suppliedOutput)
                : Path.Combine(projectRoot, "output", "phase1.5");

            RequireDirectory(variantBin, "EPLAN variant bin");
            RequireDirectory(platformBin, "EPLAN platform bin");
            if (!File.Exists(sample))
            {
                throw new FileNotFoundException("EDZ sample was not found.", sample);
            }

            return new BootstrapOptions
            {
                VariantBinDirectory = Path.GetFullPath(variantBin),
                PlatformBinDirectory = Path.GetFullPath(platformBin),
                SourceEdzPath = sample,
                OutputDirectory = output,
                Execute = execute,
                Phase5 = phase5
            };
        }

        private static string ReadOption(
            IDictionary<string, string> values,
            string argumentName,
            string environmentName)
        {
            if (values.TryGetValue(argumentName, out var supplied))
            {
                return supplied;
            }

            var environment = Environment.GetEnvironmentVariable(environmentName);
            if (!string.IsNullOrWhiteSpace(environment))
            {
                return environment;
            }

            throw new ArgumentException(
                "Supply " + argumentName + " or set the local environment variable " + environmentName + ".");
        }

        private static string FindProjectRoot(string start)
        {
            var directory = new DirectoryInfo(start);
            while (directory != null)
            {
                if (File.Exists(Path.Combine(directory.FullName, "EplanEdzManager.sln")))
                {
                    return directory.FullName;
                }

                directory = directory.Parent;
            }

            throw new DirectoryNotFoundException("EplanEdzManager.sln was not found above " + start);
        }

        private static void RequireDirectory(string path, string label)
        {
            if (string.IsNullOrWhiteSpace(path) || !Directory.Exists(path))
            {
                throw new DirectoryNotFoundException(label + " was not found: " + path);
            }
        }
    }
}
