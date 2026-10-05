using System.Collections.ObjectModel;
using EplanEdzManager.Core.Diagnostics;
using EplanEdzManager.Core.Model;

namespace EplanEdzManager.Core;

public sealed class LazyPartIndexEntry
{
    private readonly Func<CancellationToken, PartMetadataReadResult> _loader;
    private readonly object _sync = new object();
    private PartMetadataReadResult? _cached;

    public LazyPartIndexEntry(string packageKey, string? packageName, Func<CancellationToken, PartMetadataReadResult> loader)
    {
        PackageKey = packageKey ?? throw new ArgumentNullException(nameof(packageKey));
        PackageName = packageName;
        _loader = loader ?? throw new ArgumentNullException(nameof(loader));
    }

    public string PackageKey { get; }

    public string? PackageName { get; }

    internal bool IsLoaded
    {
        get
        {
            lock (_sync)
            {
                return _cached is not null;
            }
        }
    }

    internal PartMetadataReadResult? CachedResult
    {
        get
        {
            lock (_sync)
            {
                return _cached;
            }
        }
    }

    public PartMetadataReadResult Load(CancellationToken cancellationToken)
    {
        lock (_sync)
        {
            cancellationToken.ThrowIfCancellationRequested();
            _cached ??= _loader(cancellationToken);
            return _cached;
        }
    }
}

public sealed class EdzPartIndex
{
    private readonly IReadOnlyList<LazyPartIndexEntry> _entries;
    private readonly Dictionary<string, LazyPartIndexEntry> _byPackageKey;

    public EdzPartIndex(IEnumerable<LazyPartIndexEntry> entries)
    {
        var materialized = entries?.ToList() ?? throw new ArgumentNullException(nameof(entries));
        _entries = new ReadOnlyCollection<LazyPartIndexEntry>(materialized);
        _byPackageKey = new Dictionary<string, LazyPartIndexEntry>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in materialized)
        {
            if (!_byPackageKey.ContainsKey(entry.PackageKey))
            {
                _byPackageKey.Add(entry.PackageKey, entry);
            }
        }
    }

    public int Count => _entries.Count;

    public IReadOnlyList<DiagnosticRecord> Diagnostics => new ReadOnlyCollection<DiagnosticRecord>(
        _entries
            .Where(entry => entry.IsLoaded)
            .SelectMany(entry => entry.CachedResult?.Diagnostics ?? Array.Empty<DiagnosticRecord>())
            .ToList());

    public PartRecord? FindExact(string value, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        if (_byPackageKey.TryGetValue(value, out var byKey))
        {
            return byKey.Load(cancellationToken).Part;
        }

        foreach (var entry in _entries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var part = entry.Load(cancellationToken).Part;
            if (string.Equals(part?.PartNumber, value, StringComparison.OrdinalIgnoreCase))
            {
                return part;
            }
        }

        return null;
    }

    public IReadOnlyList<PartRecord> Take(int limit, CancellationToken cancellationToken)
    {
        if (limit <= 0)
        {
            return Array.Empty<PartRecord>();
        }

        var results = new List<PartRecord>(Math.Min(limit, _entries.Count));
        foreach (var entry in _entries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var part = entry.Load(cancellationToken).Part;
            if (part is not null)
            {
                results.Add(part);
                if (results.Count == limit)
                {
                    break;
                }
            }
        }

        return results.AsReadOnly();
    }

    public IReadOnlyList<PartRecord> Search(string query, int limit, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(query) || limit <= 0)
        {
            return Array.Empty<PartRecord>();
        }

        var results = new List<PartRecord>();
        var packageMatches = new HashSet<LazyPartIndexEntry>();
        foreach (var entry in _entries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!Contains(entry.PackageKey, query) && !Contains(entry.PackageName, query))
            {
                continue;
            }

            packageMatches.Add(entry);
            var part = entry.Load(cancellationToken).Part;
            if (part is not null)
            {
                results.Add(part);
                if (results.Count == limit)
                {
                    break;
                }
            }
        }

        if (results.Count >= limit)
        {
            return results.AsReadOnly();
        }

        foreach (var entry in _entries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (packageMatches.Contains(entry))
            {
                continue;
            }

            var part = entry.Load(cancellationToken).Part;
            if (part is not null
                && (Contains(part.PartNumber, query)
                    || Contains(part.Manufacturer, query)
                    || Contains(part.TypeNumber, query)))
            {
                results.Add(part);
                if (results.Count == limit)
                {
                    break;
                }
            }
        }

        return results.AsReadOnly();
    }

    private static bool Contains(string? value, string query)
    {
        return value?.IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0;
    }
}

