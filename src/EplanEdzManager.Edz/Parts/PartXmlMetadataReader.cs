using System.Xml;
using EplanEdzManager.Core.Diagnostics;
using EplanEdzManager.Core.Model;
using EplanEdzManager.Core.Text;

namespace EplanEdzManager.Edz.Parts;

public sealed class PartXmlMetadataReader : IPartMetadataReader
{
    private const long MaximumCharacters = 16L * 1024 * 1024;
    private const int MaximumDepth = 128;

    private static readonly HashSet<string> KnownPartAttributes = new HashSet<string>(
        new[]
        {
            "P_ARTICLE_MANUFACTURER",
            "P_ARTICLE_PARTNR",
            "P_ARTICLE_TYPENR",
            "P_ARTICLE_ORDERNR",
            "P_ARTICLE_DESCR1",
            "P_ARTICLE_PRODUCTTOPGROUP",
            "P_ARTICLE_PRODUCTGROUP",
            "P_ARTICLE_PRODUCTSUBGROUP"
        },
        StringComparer.Ordinal);

    private static readonly HashSet<string> KnownElements = new HashSet<string>(
        new[] { "partsmanagement", "part", "variant", "functiontemplate", "freeproperty" },
        StringComparer.Ordinal);

    public PartMetadataReadResult Read(
        Stream xmlStream,
        string? packageKey,
        string sourceEdz,
        string rawMetadataReference,
        CancellationToken cancellationToken)
    {
        if (xmlStream is null)
        {
            throw new ArgumentNullException(nameof(xmlStream));
        }

        var diagnostics = new List<DiagnosticRecord>();
        try
        {
            var settings = new XmlReaderSettings
            {
                DtdProcessing = DtdProcessing.Prohibit,
                XmlResolver = null,
                IgnoreWhitespace = true,
                MaxCharactersInDocument = MaximumCharacters,
                MaxCharactersFromEntities = 1024,
                CloseInput = false
            };

            using var reader = XmlReader.Create(xmlStream, settings);
            string? manufacturer = null;
            string? partNumber = null;
            string? typeNumber = null;
            string? orderNumber = null;
            string? description = null;
            string? topGroup = null;
            string? group = null;
            string? subGroup = null;
            var variants = new List<string>();
            var unknownAttributes = new Dictionary<string, string>(StringComparer.Ordinal);
            var unknownElements = new List<UnknownXmlElement>();
            var partFound = false;
            var insidePart = false;
            var partDepth = -1;

            while (reader.Read())
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (reader.Depth > MaximumDepth)
                {
                    throw new InvalidDataException("The part XML nesting depth exceeds the configured safety limit.");
                }

                if (reader.NodeType == XmlNodeType.EndElement
                    && insidePart
                    && reader.Depth == partDepth
                    && reader.LocalName.Equals("part", StringComparison.Ordinal))
                {
                    insidePart = false;
                    continue;
                }

                if (reader.NodeType != XmlNodeType.Element)
                {
                    continue;
                }

                if (reader.LocalName.Equals("part", StringComparison.Ordinal) && !partFound)
                {
                    partFound = true;
                    insidePart = true;
                    partDepth = reader.Depth;
                    manufacturer = reader.GetAttribute("P_ARTICLE_MANUFACTURER");
                    partNumber = reader.GetAttribute("P_ARTICLE_PARTNR");
                    typeNumber = reader.GetAttribute("P_ARTICLE_TYPENR");
                    orderNumber = reader.GetAttribute("P_ARTICLE_ORDERNR");
                    description = EplanMultilingualText.ToDisplayText(reader.GetAttribute("P_ARTICLE_DESCR1"));
                    topGroup = reader.GetAttribute("P_ARTICLE_PRODUCTTOPGROUP");
                    group = reader.GetAttribute("P_ARTICLE_PRODUCTGROUP");
                    subGroup = reader.GetAttribute("P_ARTICLE_PRODUCTSUBGROUP");
                    CaptureUnknownAttributes(reader, KnownPartAttributes, unknownAttributes, string.Empty);
                    continue;
                }

                if (insidePart && reader.LocalName.Equals("variant", StringComparison.Ordinal))
                {
                    var variant = reader.GetAttribute("P_ARTICLE_VARIANT");
                    if (!string.IsNullOrWhiteSpace(variant))
                    {
                        variants.Add(variant);
                    }

                    CaptureUnknownAttributes(
                        reader,
                        new HashSet<string>(new[] { "P_ARTICLE_VARIANT" }, StringComparer.Ordinal),
                        unknownAttributes,
                        "variant[" + variants.Count + "].");
                    continue;
                }

                if (insidePart && !KnownElements.Contains(reader.LocalName))
                {
                    unknownElements.Add(new UnknownXmlElement(
                        reader.Name,
                        "/partsmanagement/part/" + reader.Name));
                }
            }

            if (!partFound)
            {
                diagnostics.Add(Error("The part XML does not contain a part element.", rawMetadataReference));
                return new PartMetadataReadResult(null, diagnostics);
            }

            var productGroup = JoinGroup(topGroup, group, subGroup);
            return new PartMetadataReadResult(
                new PartRecord(
                    manufacturer,
                    partNumber,
                    typeNumber,
                    orderNumber,
                    description,
                    productGroup,
                    variants,
                    packageKey,
                    sourceEdz,
                    rawMetadataReference,
                    unknownAttributes,
                    unknownElements),
                diagnostics);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception) when (exception is XmlException or InvalidDataException)
        {
            diagnostics.Add(Error(
                "The part XML is malformed, exceeds safety limits, or contains prohibited XML constructs.",
                rawMetadataReference,
                exception));
            return new PartMetadataReadResult(null, diagnostics);
        }
    }

    private static void CaptureUnknownAttributes(
        XmlReader reader,
        ISet<string> known,
        IDictionary<string, string> destination,
        string prefix)
    {
        if (!reader.HasAttributes)
        {
            return;
        }

        while (reader.MoveToNextAttribute())
        {
            if (!known.Contains(reader.LocalName) && !reader.Name.StartsWith("xmlns", StringComparison.Ordinal))
            {
                destination[prefix + reader.Name] = reader.Value;
            }
        }

        reader.MoveToElement();
    }

    private static string? JoinGroup(params string?[] values)
    {
        var present = values.Where(value => !string.IsNullOrWhiteSpace(value)).ToArray();
        return present.Length == 0 ? null : string.Join("/", present!);
    }

    private static DiagnosticRecord Error(
        string message,
        string entryPath,
        Exception? exception = null)
    {
        return new DiagnosticRecord(
            DiagnosticSeverity.Error,
            "EDZ401",
            message,
            EvidenceLevel.Confirmed,
            entryPath: entryPath,
            exceptionType: exception?.GetType().FullName);
    }
}

