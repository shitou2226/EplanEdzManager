using System.Collections.ObjectModel;
using EplanEdzManager.Core.Diagnostics;

namespace EplanEdzManager.Core.Model;

public sealed class PartRecord
{
    public PartRecord(
        string? manufacturer,
        string? partNumber,
        string? typeNumber,
        string? orderNumber,
        string? description,
        string? productGroup,
        IEnumerable<string> variants,
        string? packageKey,
        string sourceEdz,
        string rawMetadataReference,
        IDictionary<string, string> unknownAttributes,
        IEnumerable<UnknownXmlElement> unknownElements)
    {
        Manufacturer = manufacturer;
        PartNumber = partNumber;
        TypeNumber = typeNumber;
        OrderNumber = orderNumber;
        Description = description;
        ProductGroup = productGroup;
        Variants = new ReadOnlyCollection<string>(variants.ToList());
        PackageKey = packageKey;
        SourceEdz = sourceEdz ?? throw new ArgumentNullException(nameof(sourceEdz));
        RawMetadataReference = rawMetadataReference ?? throw new ArgumentNullException(nameof(rawMetadataReference));
        UnknownAttributes = new ReadOnlyDictionary<string, string>(
            new Dictionary<string, string>(unknownAttributes, StringComparer.Ordinal));
        UnknownElements = new ReadOnlyCollection<UnknownXmlElement>(unknownElements.ToList());
    }

    public string? Manufacturer { get; }

    public string? PartNumber { get; }

    public string? TypeNumber { get; }

    public string? OrderNumber { get; }

    public string? Description { get; }

    public string? ProductGroup { get; }

    public string? Variant => Variants.FirstOrDefault();

    public IReadOnlyList<string> Variants { get; }

    public string? PackageKey { get; }

    public string SourceEdz { get; }

    public string RawMetadataReference { get; }

    public IReadOnlyDictionary<string, string> UnknownAttributes { get; }

    public IReadOnlyList<UnknownXmlElement> UnknownElements { get; }
}

public sealed class PartMetadataReadResult
{
    public PartMetadataReadResult(PartRecord? part, IEnumerable<DiagnosticRecord> diagnostics)
    {
        Part = part;
        Diagnostics = new ReadOnlyCollection<DiagnosticRecord>(diagnostics.ToList());
    }

    public PartRecord? Part { get; }

    public IReadOnlyList<DiagnosticRecord> Diagnostics { get; }
}

public interface IPartMetadataReader
{
    PartMetadataReadResult Read(
        Stream xmlStream,
        string? packageKey,
        string sourceEdz,
        string rawMetadataReference,
        CancellationToken cancellationToken);
}

