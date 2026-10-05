using System.Collections.ObjectModel;

namespace EplanEdzManager.Core.Model;

public sealed class UnknownXmlElement
{
    public UnknownXmlElement(string name, string location)
    {
        Name = name;
        Location = location;
    }

    public string Name { get; }

    public string Location { get; }
}

public sealed class EdzManifest
{
    public EdzManifest(
        string? version,
        IEnumerable<EdzPackage> packages,
        IDictionary<string, string> unknownAttributes,
        IEnumerable<UnknownXmlElement> unknownElements)
    {
        Version = version;
        Packages = new ReadOnlyCollection<EdzPackage>(packages.ToList());
        UnknownAttributes = new ReadOnlyDictionary<string, string>(
            new Dictionary<string, string>(unknownAttributes, StringComparer.Ordinal));
        UnknownElements = new ReadOnlyCollection<UnknownXmlElement>(unknownElements.ToList());
    }

    public string? Version { get; }

    public IReadOnlyList<EdzPackage> Packages { get; }

    public IReadOnlyDictionary<string, string> UnknownAttributes { get; }

    public IReadOnlyList<UnknownXmlElement> UnknownElements { get; }
}

public sealed class EdzPackage
{
    public EdzPackage(
        string? type,
        string? key,
        string? name,
        IEnumerable<EdzItemReference> items,
        IDictionary<string, string> unknownAttributes,
        IEnumerable<UnknownXmlElement> unknownElements)
    {
        Type = type;
        Key = key;
        Name = name;
        Items = new ReadOnlyCollection<EdzItemReference>(items.ToList());
        UnknownAttributes = new ReadOnlyDictionary<string, string>(
            new Dictionary<string, string>(unknownAttributes, StringComparer.Ordinal));
        UnknownElements = new ReadOnlyCollection<UnknownXmlElement>(unknownElements.ToList());
    }

    public string? Type { get; }

    public string? Key { get; }

    public string? Name { get; }

    public IReadOnlyList<EdzItemReference> Items { get; }

    public IReadOnlyDictionary<string, string> UnknownAttributes { get; }

    public IReadOnlyList<UnknownXmlElement> UnknownElements { get; }
}

public sealed class EdzItemReference
{
    public EdzItemReference(
        string? type,
        string? name,
        string? rawLocator,
        EdzLocator? resolvedLocator,
        IDictionary<string, string> unknownAttributes)
    {
        Type = type;
        Name = name;
        RawLocator = rawLocator;
        ResolvedLocator = resolvedLocator;
        UnknownAttributes = new ReadOnlyDictionary<string, string>(
            new Dictionary<string, string>(unknownAttributes, StringComparer.Ordinal));
    }

    public string? Type { get; }

    public string? Name { get; }

    public string? RawLocator { get; }

    public EdzLocator? ResolvedLocator { get; }

    public IReadOnlyDictionary<string, string> UnknownAttributes { get; }
}

