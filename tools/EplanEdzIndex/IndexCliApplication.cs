using System.Text.Json;
using System.Text.Json.Serialization;
using EplanEdzManager.Infrastructure.Sqlite;

namespace EplanEdzIndex;

public sealed class IndexCliApplication
{
    private static readonly JsonSerializerOptions JsonOptions = new JsonSerializerOptions
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
        Converters = { new JsonStringEnumConverter() }
    };

    public async Task<int> RunAsync(string[] args, TextWriter output, TextWriter error, CancellationToken cancellationToken)
    {
        try
        {
            if (args.Length < 2)
            {
                WriteUsage(error);
                return 2;
            }

            var command = args[0].ToLowerInvariant();
            var repository = new SqliteIndexRepository(args[1]);
            switch (command)
            {
                case "init":
                    await repository.InitializeAsync(cancellationToken).ConfigureAwait(false);
                    await WriteAsync(output, IsJson(args), new { initialized = true, database = repository.DatabasePath }).ConfigureAwait(false);
                    return 0;

                case "add-dir":
                    return await AddDirectoryAsync(repository, args, output, error, cancellationToken).ConfigureAwait(false);

                case "remove-dir":
                    return await RemoveDirectoryAsync(repository, args, output, error, cancellationToken).ConfigureAwait(false);

                case "directories":
                    var directories = await repository.GetDirectoriesAsync(cancellationToken).ConfigureAwait(false);
                    await WriteAsync(output, IsJson(args), directories).ConfigureAwait(false);
                    return 0;

                case "scan":
                    return await ScanAsync(repository, args, output, error, cancellationToken).ConfigureAwait(false);

                case "prune":
                    return await PruneAsync(repository, args, output, error, cancellationToken).ConfigureAwait(false);

                case "search":
                    return await SearchAsync(repository, args, output, error, cancellationToken).ConfigureAwait(false);

                case "stats":
                    var statistics = await repository.GetStatisticsAsync(cancellationToken).ConfigureAwait(false);
                    await WriteAsync(output, IsJson(args), statistics).ConfigureAwait(false);
                    return 0;

                default:
                    WriteUsage(error);
                    return 2;
            }
        }
        catch (OperationCanceledException)
        {
            error.WriteLine("Operation cancelled.");
            return 130;
        }
        catch (Exception exception) when (exception is not OutOfMemoryException and not StackOverflowException)
        {
            error.WriteLine(exception.GetType().Name + ": " + exception.Message);
            return 1;
        }
    }

    private static async Task<int> AddDirectoryAsync(SqliteIndexRepository repository, string[] args, TextWriter output, TextWriter error, CancellationToken cancellationToken)
    {
        if (!TryReadPositional(args, 2, out var directory))
        {
            error.WriteLine("add-dir requires a directory path.");
            return 2;
        }

        if (!Directory.Exists(directory))
        {
            error.WriteLine("Directory not found: " + directory);
            return 2;
        }

        var recursive = HasOption(args, "--recursive");
        var id = await repository.AddDirectoryAsync(directory, recursive, cancellationToken).ConfigureAwait(false);
        await WriteAsync(output, IsJson(args), new { id, directory = Path.GetFullPath(directory), recursive }).ConfigureAwait(false);
        return 0;
    }

    private static async Task<int> RemoveDirectoryAsync(SqliteIndexRepository repository, string[] args, TextWriter output, TextWriter error, CancellationToken cancellationToken)
    {
        if (!TryReadPositional(args, 2, out var directory))
        {
            error.WriteLine("remove-dir requires a directory path.");
            return 2;
        }

        var removed = await repository.RemoveDirectoryAsync(directory, cancellationToken).ConfigureAwait(false);
        await WriteAsync(output, IsJson(args), new { removed, directory = Path.GetFullPath(directory) }).ConfigureAwait(false);
        return removed ? 0 : 1;
    }

    private static async Task<int> ScanAsync(SqliteIndexRepository repository, string[] args, TextWriter output, TextWriter error, CancellationToken cancellationToken)
    {
        var builder = new EdzIndexBuilder(repository);
        IReadOnlyList<IndexScanResult> results;
        if (TryReadPositional(args, 2, out var directory))
        {
            var result = await builder.ScanDirectoryAsync(directory, HasOption(args, "--recursive"), null, cancellationToken).ConfigureAwait(false);
            results = new[] { result };
        }
        else
        {
            results = await builder.ScanRegisteredDirectoriesAsync(null, cancellationToken).ConfigureAwait(false);
        }

        await WriteAsync(output, IsJson(args), results).ConfigureAwait(false);
        return results.Any(result => result.Failed > 0) ? 1 : 0;
    }

    private static async Task<int> PruneAsync(SqliteIndexRepository repository, string[] args, TextWriter output, TextWriter error, CancellationToken cancellationToken)
    {
        var builder = new EdzIndexBuilder(repository);
        if (TryReadPositional(args, 2, out var directory))
        {
            var removed = await builder.PruneDirectoryAsync(directory, cancellationToken).ConfigureAwait(false);
            await WriteAsync(output, IsJson(args), new { directory = Path.GetFullPath(directory), removed }).ConfigureAwait(false);
            return 0;
        }

        var total = 0;
        foreach (var registered in await repository.GetDirectoriesAsync(cancellationToken).ConfigureAwait(false))
        {
            if (Directory.Exists(registered.Path))
            {
                total += await builder.PruneDirectoryAsync(registered.Path, cancellationToken).ConfigureAwait(false);
            }
        }

        await WriteAsync(output, IsJson(args), new { removed = total }).ConfigureAwait(false);
        return 0;
    }

    private static async Task<int> SearchAsync(SqliteIndexRepository repository, string[] args, TextWriter output, TextWriter error, CancellationToken cancellationToken)
    {
        var text = TryReadPositional(args, 2, out var positional) ? positional : null;
        var resourceValue = GetOptionValue(args, "--resources") ?? "any";
        if (!TryParseResourceFilter(resourceValue, out var resourceFilter))
        {
            error.WriteLine("--resources must be one of: any, has-any, present, missing, none.");
            return 2;
        }

        var query = new PartSearchQuery(
            text,
            GetOptionValue(args, "--manufacturer"),
            GetOptionValue(args, "--part-number"),
            GetOptionValue(args, "--type-number"),
            resourceFilter,
            ReadIntegerOption(args, "--limit", 100, 1, 1000),
            ReadIntegerOption(args, "--offset", 0, 0, int.MaxValue));
        var results = await repository.SearchAsync(query, cancellationToken).ConfigureAwait(false);
        await WriteAsync(output, IsJson(args), results).ConfigureAwait(false);
        return 0;
    }

    private static bool TryParseResourceFilter(string value, out ResourceExistenceFilter filter)
    {
        filter = value.ToLowerInvariant() switch
        {
            "any" => ResourceExistenceFilter.Any,
            "has-any" => ResourceExistenceFilter.HasAny,
            "present" => ResourceExistenceFilter.AllPresent,
            "missing" => ResourceExistenceFilter.Missing,
            "none" => ResourceExistenceFilter.None,
            _ => (ResourceExistenceFilter)(-1)
        };
        return Enum.IsDefined(filter);
    }

    private static bool TryReadPositional(string[] args, int startIndex, out string value)
    {
        for (var index = startIndex; index < args.Length; index++)
        {
            if (args[index].StartsWith("--", StringComparison.Ordinal))
            {
                if (OptionsWithValues.Contains(args[index]) && index + 1 < args.Length)
                {
                    index++;
                }

                continue;
            }

            value = args[index];
            return true;
        }

        value = string.Empty;
        return false;
    }

    private static string? GetOptionValue(string[] args, string name)
    {
        for (var index = 0; index < args.Length - 1; index++)
        {
            if (args[index].Equals(name, StringComparison.OrdinalIgnoreCase))
            {
                return args[index + 1];
            }
        }

        return null;
    }

    private static int ReadIntegerOption(string[] args, string name, int defaultValue, int minimum, int maximum)
    {
        var value = GetOptionValue(args, name);
        return int.TryParse(value, out var parsed) ? Math.Clamp(parsed, minimum, maximum) : defaultValue;
    }

    private static bool HasOption(string[] args, string option) => args.Any(argument => argument.Equals(option, StringComparison.OrdinalIgnoreCase));

    private static bool IsJson(string[] args) => HasOption(args, "--json");

    private static async Task WriteAsync(TextWriter output, bool json, object value)
    {
        if (json)
        {
            await output.WriteLineAsync(JsonSerializer.Serialize(value, JsonOptions)).ConfigureAwait(false);
        }
        else
        {
            await output.WriteLineAsync(JsonSerializer.Serialize(value, JsonOptions)).ConfigureAwait(false);
        }
    }

    private static void WriteUsage(TextWriter writer)
    {
        writer.WriteLine("Usage:");
        writer.WriteLine("  EplanEdzIndex init <index.db> [--json]");
        writer.WriteLine("  EplanEdzIndex add-dir <index.db> <directory> [--recursive] [--json]");
        writer.WriteLine("  EplanEdzIndex remove-dir <index.db> <directory> [--json]");
        writer.WriteLine("  EplanEdzIndex directories <index.db> [--json]");
        writer.WriteLine("  EplanEdzIndex scan <index.db> [directory] [--recursive] [--json]");
        writer.WriteLine("  EplanEdzIndex prune <index.db> [directory] [--json]");
        writer.WriteLine("  EplanEdzIndex search <index.db> [text] [--manufacturer X] [--part-number X] [--type-number X]");
        writer.WriteLine("                      [--resources any|has-any|present|missing|none] [--limit N] [--offset N] [--json]");
        writer.WriteLine("  EplanEdzIndex stats <index.db> [--json]");
    }

    private static readonly HashSet<string> OptionsWithValues = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "--manufacturer", "--part-number", "--type-number", "--resources", "--limit", "--offset"
    };
}
