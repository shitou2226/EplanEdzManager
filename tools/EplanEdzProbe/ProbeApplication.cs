using System.Text.Json;
using System.Text.Json.Serialization;
using System.Diagnostics;
using EplanEdzManager.Core.Diagnostics;
using EplanEdzManager.Core.Model;
using EplanEdzManager.Edz.Documents;

namespace EplanEdzProbe;

public sealed class ProbeApplication
{
    private static readonly JsonSerializerOptions JsonOptions = new JsonSerializerOptions
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
        Converters = { new JsonStringEnumConverter() }
    };

    public Task<int> RunAsync(string[] args, TextWriter output, TextWriter error, CancellationToken cancellationToken)
    {
        try
        {
            return Task.FromResult(Run(args, output, error, cancellationToken));
        }
        catch (OperationCanceledException)
        {
            error.WriteLine("Operation cancelled.");
            return Task.FromResult(130);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException and not StackOverflowException)
        {
            error.WriteLine("EDZ999 UnexpectedError: " + exception.Message);
            return Task.FromResult(1);
        }
    }

    private static int Run(string[] args, TextWriter output, TextWriter error, CancellationToken cancellationToken)
    {
        if (args.Length < 2)
        {
            WriteUsage(error);
            return 2;
        }

        var command = args[0].ToLowerInvariant();
        var path = args[1];
        var json = args.Any(argument => argument.Equals("--json", StringComparison.OrdinalIgnoreCase));
        var limit = ReadLimit(args, 50);
        if (!KnownCommands.Contains(command))
        {
            WriteUsage(error);
            return 2;
        }

        using var runtimeSampler = new RuntimeMetricSampler();
        var openResult = new EdzDocumentReader().Open(path, cancellationToken);
        if (openResult.Document is null)
        {
            WriteResult(output, json, command, false, null, openResult.Diagnostics, runtimeSampler.Stop(0));
            return 1;
        }

        using var document = openResult.Document;
        object? data;
        var success = true;
        IReadOnlyList<PartRecord>? humanParts = null;
        PartRecord? humanPart = null;
        IReadOnlyList<EdzPackageResource>? humanResources = null;
        var commandTimer = Stopwatch.StartNew();
        switch (command)
        {
            case "inspect":
                data = Inspect(document, openResult.Format.ToString(), openResult.Metrics);
                break;
            case "parts":
                humanParts = document.Parts.Take(limit, cancellationToken);
                data = humanParts.Select(ToPartData).ToArray();
                break;
            case "search":
                if (args.Length < 3 || args[2].StartsWith("--", StringComparison.Ordinal))
                {
                    WriteUsage(error);
                    return 2;
                }

                humanParts = document.Parts.Search(args[2], limit, cancellationToken);
                data = humanParts.Select(ToPartData).ToArray();
                break;
            case "part":
                if (args.Length < 3 || args[2].StartsWith("--", StringComparison.Ordinal))
                {
                    WriteUsage(error);
                    return 2;
                }

                humanPart = document.Parts.FindExact(args[2], cancellationToken);
                humanResources = humanPart is null ? null : document.GetResources(humanPart.PackageKey ?? args[2]);
                data = humanPart is null ? null : new
                {
                    metadata = ToPartData(humanPart),
                    resources = ToResourceData(humanResources!)
                };
                success = humanPart is not null;
                break;
            case "resources":
                if (args.Length < 3 || args[2].StartsWith("--", StringComparison.Ordinal))
                {
                    WriteUsage(error);
                    return 2;
                }

                humanResources = document.GetResources(args[2]);
                data = ToResourceData(humanResources);
                success = humanResources.Count > 0;
                break;
            case "validate":
                var diagnostics = document.Diagnostics;
                success = diagnostics.All(record => record.Severity < DiagnosticSeverity.Error);
                data = new
                {
                    archiveValid = true,
                    manifestValid = true,
                    entryCount = document.ArchiveInfo.EntryCount,
                    packageCount = document.Manifest.Packages.Count,
                    missingReferenceCount = document.MissingReferenceCount,
                    unreferencedEntryCount = document.UnreferencedEntryCount,
                    duplicatePackageKeyCount = diagnostics.Count(record => record.Code == "EDZ303"),
                    duplicatePathCount = diagnostics.Count(record => record.Code == "EDZ302")
                };
                break;
            default:
                throw new InvalidOperationException("Unsupported command dispatch.");
        }
        commandTimer.Stop();
        var runtimeMetrics = runtimeSampler.Stop(commandTimer.Elapsed.TotalMilliseconds);

        if (json)
        {
            WriteResult(output, true, command, success, data, document.Diagnostics, runtimeMetrics);
        }
        else
        {
            WriteHumanResult(output, command, success, document, humanParts, humanPart, humanResources, document.Diagnostics);
        }

        return success ? 0 : 1;
    }

    private static readonly HashSet<string> KnownCommands = new HashSet<string>(
        new[] { "inspect", "parts", "search", "part", "resources", "validate" },
        StringComparer.OrdinalIgnoreCase);

    private static object Inspect(EdzDocument document, string format, EdzReadMetrics metrics)
    {
        var info = document.ArchiveInfo;
        return new
        {
            format,
            archiveSize = info.FileSize,
            entryCount = info.EntryCount,
            manifest = "manifest.xml",
            manifestVersion = document.Manifest.Version,
            packageCount = document.Manifest.Packages.Count,
            resourceCount = info.EntryCount - 1,
            compression = info.CompressionMethods,
            isSolid = info.IsSolid,
            isEncrypted = info.IsEncrypted,
            missingReferenceCount = document.MissingReferenceCount,
            unreferencedEntryCount = document.UnreferencedEntryCount,
            timingsMilliseconds = new
            {
                metrics.ArchiveOpenMilliseconds,
                metrics.EntryEnumerationMilliseconds,
                metrics.ManifestParseMilliseconds,
                metrics.ValidationMilliseconds,
                metrics.IndexBuildMilliseconds,
                metrics.TotalOpenMilliseconds
            }
        };
    }

    private static object ToPartData(PartRecord part)
    {
        return new
        {
            part.Manufacturer,
            part.PartNumber,
            part.TypeNumber,
            part.OrderNumber,
            part.Description,
            part.ProductGroup,
            part.Variant,
            part.Variants,
            part.PackageKey,
            part.SourceEdz,
            part.RawMetadataReference,
            part.UnknownAttributes,
            unknownElements = part.UnknownElements
        };
    }

    private static object[] ToResourceData(IReadOnlyList<EdzPackageResource> resources)
    {
        return resources.Select(resource => (object)new
        {
            resource.Reference.Type,
            resource.Reference.Name,
            locator = resource.Reference.RawLocator,
            path = resource.Reference.ResolvedLocator?.ArchivePath,
            exists = resource.Entry is not null,
            size = resource.Entry?.Size,
            compressedSize = resource.Entry?.CompressedSize,
            compression = resource.Entry?.CompressionMethod,
            resource.ReferenceCount,
            resource.Reference.UnknownAttributes
        }).ToArray();
    }

    private static void WriteHumanResult(
        TextWriter output,
        string command,
        bool success,
        EdzDocument document,
        IReadOnlyList<PartRecord>? parts,
        PartRecord? part,
        IReadOnlyList<EdzPackageResource>? resources,
        IReadOnlyList<DiagnosticRecord> diagnostics)
    {
        output.WriteLine("Command : " + command);
        output.WriteLine("Success : " + success);
        output.WriteLine();

        if (command == "inspect")
        {
            var info = document.ArchiveInfo;
            output.WriteLine("Format       : CurrentKnown7zEdz");
            output.WriteLine("Archive Size : " + info.FileSize);
            output.WriteLine("Entry Count  : " + info.EntryCount);
            output.WriteLine("Manifest     : manifest.xml (version " + (document.Manifest.Version ?? "unknown") + ")");
            output.WriteLine("Packages     : " + document.Manifest.Packages.Count);
            output.WriteLine("Resources    : " + (info.EntryCount - 1));
            output.WriteLine("Compression  : " + string.Join(", ", info.CompressionMethods));
        }
        else if (command is "parts" or "search")
        {
            output.WriteLine("Manufacturer\tPartNumber\tTypeNumber\tDescription");
            foreach (var row in parts ?? Array.Empty<PartRecord>())
            {
                output.WriteLine(string.Join("\t", Clean(row.Manufacturer), Clean(row.PartNumber), Clean(row.TypeNumber), Clean(row.Description)));
            }
        }
        else if (command == "part" && part is not null)
        {
            output.WriteLine("Manufacturer         : " + Clean(part.Manufacturer));
            output.WriteLine("Part Number          : " + Clean(part.PartNumber));
            output.WriteLine("Type Number          : " + Clean(part.TypeNumber));
            output.WriteLine("Order Number         : " + Clean(part.OrderNumber));
            output.WriteLine("Description          : " + Clean(part.Description));
            output.WriteLine("Product Group        : " + Clean(part.ProductGroup));
            output.WriteLine("Variant              : " + Clean(part.Variant));
            output.WriteLine("Raw Metadata         : " + part.RawMetadataReference);
            output.WriteLine();
            WriteResourceTable(output, resources ?? Array.Empty<EdzPackageResource>());
        }
        else if (command == "resources")
        {
            WriteResourceTable(output, resources ?? Array.Empty<EdzPackageResource>());
        }
        else if (command == "validate")
        {
            output.WriteLine("Archive                : OK");
            output.WriteLine("Manifest               : OK");
            output.WriteLine("Entry Count            : " + document.ArchiveInfo.EntryCount);
            output.WriteLine("Package Count          : " + document.Manifest.Packages.Count);
            output.WriteLine("Missing References     : " + document.MissingReferenceCount);
            output.WriteLine("Unreferenced Entries   : " + document.UnreferencedEntryCount);
            output.WriteLine("Duplicate Package Keys : " + diagnostics.Count(record => record.Code == "EDZ303"));
            output.WriteLine("Duplicate Paths        : " + diagnostics.Count(record => record.Code == "EDZ302"));
        }

        WriteHumanDiagnostics(output, diagnostics);
    }

    private static void WriteResourceTable(TextWriter output, IEnumerable<EdzPackageResource> resources)
    {
        output.WriteLine("Type\tName\tExists\tSize\tReferences\tPath");
        foreach (var resource in resources)
        {
            output.WriteLine(string.Join(
                "\t",
                Clean(resource.Reference.Type),
                Clean(resource.Reference.Name),
                resource.Entry is not null,
                resource.Entry?.Size.ToString() ?? string.Empty,
                resource.ReferenceCount,
                Clean(resource.Reference.ResolvedLocator?.ArchivePath)));
        }
    }

    private static void WriteHumanDiagnostics(TextWriter output, IReadOnlyList<DiagnosticRecord> diagnostics)
    {
        if (diagnostics.Count == 0) return;
        output.WriteLine();
        output.WriteLine("Severity\tCode\tEvidence\tPackage\tEntry\tMessage");
        foreach (var diagnostic in diagnostics)
        {
            output.WriteLine(string.Join(
                "\t",
                diagnostic.Severity,
                diagnostic.Code,
                diagnostic.EvidenceLevel,
                Clean(diagnostic.PackageKey),
                Clean(diagnostic.EntryPath),
                Clean(diagnostic.Message)));
        }
    }

    private static string Clean(string? value)
    {
        return (value ?? string.Empty).Replace('\t', ' ').Replace('\r', ' ').Replace('\n', ' ');
    }

    private static void WriteResult(
        TextWriter output,
        bool json,
        string command,
        bool success,
        object? data,
        IReadOnlyList<DiagnosticRecord> diagnostics,
        RuntimeMetrics runtimeMetrics)
    {
        if (json)
        {
            var envelope = new
            {
                schemaVersion = "1.0",
                command,
                success,
                data,
                diagnostics,
                runtimeMetrics
            };
            output.WriteLine(JsonSerializer.Serialize(envelope, JsonOptions));
            return;
        }

        output.WriteLine("Command : " + command);
        output.WriteLine("Success : " + success);
        if (data is not null)
        {
            output.WriteLine(JsonSerializer.Serialize(data, JsonOptions));
        }

        if (diagnostics.Count > 0)
        {
            output.WriteLine();
            output.WriteLine("Diagnostics:");
            foreach (var diagnostic in diagnostics)
            {
                output.WriteLine($"{diagnostic.Severity,-11} {diagnostic.Code,-7} {diagnostic.Message}");
            }
        }
    }

    private static int ReadLimit(string[] args, int defaultValue)
    {
        for (var index = 0; index < args.Length - 1; index++)
        {
            if (args[index].Equals("--limit", StringComparison.OrdinalIgnoreCase)
                && int.TryParse(args[index + 1], out var parsed)
                && parsed > 0)
            {
                return Math.Min(parsed, 100000);
            }
        }

        return defaultValue;
    }

    private static void WriteUsage(TextWriter writer)
    {
        writer.WriteLine("Usage:");
        writer.WriteLine("  EplanEdzProbe inspect <file.edz> [--json]");
        writer.WriteLine("  EplanEdzProbe parts <file.edz> [--limit N] [--json]");
        writer.WriteLine("  EplanEdzProbe search <file.edz> <query> [--limit N] [--json]");
        writer.WriteLine("  EplanEdzProbe part <file.edz> <part-number> [--json]");
        writer.WriteLine("  EplanEdzProbe resources <file.edz> <part-number> [--json]");
        writer.WriteLine("  EplanEdzProbe validate <file.edz> [--json]");
    }
}

public sealed class RuntimeMetrics
{
    public RuntimeMetrics(
        long peakManagedMemoryBytes,
        long peakWorkingSetBytes,
        double totalElapsedMilliseconds,
        double commandElapsedMilliseconds,
        int gen0Collections,
        int gen1Collections,
        int gen2Collections)
    {
        PeakManagedMemoryBytes = peakManagedMemoryBytes;
        PeakWorkingSetBytes = peakWorkingSetBytes;
        TotalElapsedMilliseconds = totalElapsedMilliseconds;
        CommandElapsedMilliseconds = commandElapsedMilliseconds;
        Gen0Collections = gen0Collections;
        Gen1Collections = gen1Collections;
        Gen2Collections = gen2Collections;
    }

    public long PeakManagedMemoryBytes { get; }
    public long PeakWorkingSetBytes { get; }
    public double TotalElapsedMilliseconds { get; }
    public double CommandElapsedMilliseconds { get; }
    public int Gen0Collections { get; }
    public int Gen1Collections { get; }
    public int Gen2Collections { get; }
}

internal sealed class RuntimeMetricSampler : IDisposable
{
    private readonly CancellationTokenSource _cancellation = new CancellationTokenSource();
    private readonly Stopwatch _timer = Stopwatch.StartNew();
    private readonly Process _process = Process.GetCurrentProcess();
    private readonly Task _samplerTask;
    private readonly int _initialGen0Collections = GC.CollectionCount(0);
    private readonly int _initialGen1Collections = GC.CollectionCount(1);
    private readonly int _initialGen2Collections = GC.CollectionCount(2);
    private long _peakManagedMemory;
    private long _peakWorkingSet;
    private RuntimeMetrics? _result;

    public RuntimeMetricSampler()
    {
        Sample();
        _samplerTask = Task.Run(async () =>
        {
            try
            {
                while (!_cancellation.IsCancellationRequested)
                {
                    Sample();
                    await Task.Delay(5, _cancellation.Token).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException)
            {
                // Normal sampler shutdown.
            }
        });
    }

    public RuntimeMetrics Stop(double commandElapsedMilliseconds)
    {
        if (_result is not null)
        {
            return _result;
        }

        _cancellation.Cancel();
        _samplerTask.GetAwaiter().GetResult();
        Sample();
        _timer.Stop();
        _result = new RuntimeMetrics(
            Interlocked.Read(ref _peakManagedMemory),
            Interlocked.Read(ref _peakWorkingSet),
            _timer.Elapsed.TotalMilliseconds,
            commandElapsedMilliseconds,
            GC.CollectionCount(0) - _initialGen0Collections,
            GC.CollectionCount(1) - _initialGen1Collections,
            GC.CollectionCount(2) - _initialGen2Collections);
        return _result;
    }

    public void Dispose()
    {
        Stop(0);
        _cancellation.Dispose();
        _process.Dispose();
    }

    private void Sample()
    {
        UpdateMaximum(ref _peakManagedMemory, GC.GetTotalMemory(false));
        _process.Refresh();
        UpdateMaximum(ref _peakWorkingSet, _process.WorkingSet64);
    }

    private static void UpdateMaximum(ref long target, long value)
    {
        var current = Interlocked.Read(ref target);
        while (value > current)
        {
            var observed = Interlocked.CompareExchange(ref target, value, current);
            if (observed == current)
            {
                return;
            }

            current = observed;
        }
    }
}

