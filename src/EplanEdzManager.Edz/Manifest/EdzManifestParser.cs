using System.Xml;
using EplanEdzManager.Core;
using EplanEdzManager.Core.Diagnostics;
using EplanEdzManager.Core.Model;

namespace EplanEdzManager.Edz.Manifest;

public sealed class ManifestParseResult
{
    public ManifestParseResult(EdzManifest? manifest, IEnumerable<DiagnosticRecord> diagnostics)
    {
        Manifest = manifest;
        Diagnostics = diagnostics.ToList().AsReadOnly();
    }

    public EdzManifest? Manifest { get; }

    public IReadOnlyList<DiagnosticRecord> Diagnostics { get; }
}

public sealed class EdzManifestParser
{
    private const long MaximumCharacters = 64L * 1024 * 1024;
    private const int MaximumDepth = 64;

    private static readonly HashSet<string> KnownResourceTypes = new HashSet<string>(
        new[] { "partxml", "picture", "macro", "gmacro" },
        StringComparer.OrdinalIgnoreCase);

    public ManifestParseResult Parse(Stream stream, CancellationToken cancellationToken)
    {
        if (stream is null)
        {
            throw new ArgumentNullException(nameof(stream));
        }

        var diagnostics = new List<DiagnosticRecord>();
        try
        {
            var settings = new XmlReaderSettings
            {
                DtdProcessing = DtdProcessing.Prohibit,
                XmlResolver = null,
                IgnoreComments = false,
                IgnoreWhitespace = true,
                MaxCharactersInDocument = MaximumCharacters,
                MaxCharactersFromEntities = 1024,
                CloseInput = false
            };

            using var reader = XmlReader.Create(stream, settings);
            if (!MoveToFirstElement(reader, cancellationToken)
                || !reader.LocalName.Equals("manifest", StringComparison.Ordinal))
            {
                diagnostics.Add(Error("EDZ102", "The manifest root element is missing or invalid."));
                return new ManifestParseResult(null, diagnostics);
            }

            var version = reader.GetAttribute("version");
            var unknownAttributes = ReadUnknownAttributes(reader, "version");
            var packages = new List<EdzPackage>();
            var unknownElements = new List<UnknownXmlElement>();

            while (reader.Read())
            {
                cancellationToken.ThrowIfCancellationRequested();
                EnsureDepth(reader);
                if (reader.NodeType != XmlNodeType.Element)
                {
                    continue;
                }

                if (reader.LocalName.Equals("packages", StringComparison.Ordinal))
                {
                    continue;
                }

                if (reader.LocalName.Equals("package", StringComparison.Ordinal))
                {
                    packages.Add(ParsePackage(reader, diagnostics, cancellationToken));
                    continue;
                }

                unknownElements.Add(new UnknownXmlElement(reader.Name, "/manifest/" + reader.Name));
                reader.Skip();
            }

            return new ManifestParseResult(
                new EdzManifest(version, packages, unknownAttributes, unknownElements),
                diagnostics);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (XmlException exception)
        {
            diagnostics.Add(Error("EDZ102", "The manifest XML is malformed or contains prohibited XML constructs.", exception));
            return new ManifestParseResult(null, diagnostics);
        }
        catch (InvalidDataException exception)
        {
            diagnostics.Add(Error("EDZ102", exception.Message, exception));
            return new ManifestParseResult(null, diagnostics);
        }
    }

    private static EdzPackage ParsePackage(
        XmlReader packageReader,
        ICollection<DiagnosticRecord> diagnostics,
        CancellationToken cancellationToken)
    {
        var type = packageReader.GetAttribute("type");
        var key = packageReader.GetAttribute("key");
        var name = packageReader.GetAttribute("name");
        var unknownAttributes = ReadUnknownAttributes(packageReader, "type", "key", "name");
        var items = new List<EdzItemReference>();
        var unknownElements = new List<UnknownXmlElement>();

        using var subtree = packageReader.ReadSubtree();
        subtree.Read();
        while (subtree.Read())
        {
            cancellationToken.ThrowIfCancellationRequested();
            EnsureDepth(subtree);
            if (subtree.NodeType != XmlNodeType.Element)
            {
                continue;
            }

            if (subtree.LocalName.Equals("items", StringComparison.Ordinal))
            {
                continue;
            }

            if (subtree.LocalName.Equals("item", StringComparison.Ordinal))
            {
                items.Add(ParseItem(subtree, key, diagnostics));
                if (!subtree.IsEmptyElement)
                {
                    subtree.Skip();
                }

                continue;
            }

            unknownElements.Add(new UnknownXmlElement(
                subtree.Name,
                "/manifest/packages/package[" + (key ?? "?") + "]/" + subtree.Name));
            subtree.Skip();
        }

        return new EdzPackage(type, key, name, items, unknownAttributes, unknownElements);
    }

    private static EdzItemReference ParseItem(
        XmlReader reader,
        string? packageKey,
        ICollection<DiagnosticRecord> diagnostics)
    {
        var type = reader.GetAttribute("type");
        var name = reader.GetAttribute("name");
        var rawLocator = reader.GetAttribute("locator");
        var unknownAttributes = ReadUnknownAttributes(reader, "type", "name", "locator");

        if (!string.IsNullOrWhiteSpace(type) && !KnownResourceTypes.Contains(type))
        {
            diagnostics.Add(new DiagnosticRecord(
                DiagnosticSeverity.Warning,
                "EDZ202",
                "The manifest contains an unknown resource type: " + type,
                EvidenceLevel.Unknown,
                packageKey,
                rawLocator));
        }

        EdzLocator? resolvedLocator = null;
        if (!EdzLocator.TryCreate(type ?? string.Empty, rawLocator ?? string.Empty, out resolvedLocator, out var locatorDiagnostic)
            && locatorDiagnostic is not null)
        {
            diagnostics.Add(new DiagnosticRecord(
                locatorDiagnostic.Severity,
                locatorDiagnostic.Code,
                locatorDiagnostic.Message,
                locatorDiagnostic.EvidenceLevel,
                packageKey,
                rawLocator,
                locatorDiagnostic.ExceptionType));
        }

        return new EdzItemReference(type, name, rawLocator, resolvedLocator, unknownAttributes);
    }

    private static Dictionary<string, string> ReadUnknownAttributes(XmlReader reader, params string[] knownNames)
    {
        var known = new HashSet<string>(knownNames, StringComparer.Ordinal);
        var attributes = new Dictionary<string, string>(StringComparer.Ordinal);
        if (!reader.HasAttributes)
        {
            return attributes;
        }

        while (reader.MoveToNextAttribute())
        {
            if (!known.Contains(reader.LocalName) && !reader.Name.StartsWith("xmlns", StringComparison.Ordinal))
            {
                attributes[reader.Name] = reader.Value;
            }
        }

        reader.MoveToElement();
        return attributes;
    }

    private static bool MoveToFirstElement(XmlReader reader, CancellationToken cancellationToken)
    {
        while (reader.Read())
        {
            cancellationToken.ThrowIfCancellationRequested();
            EnsureDepth(reader);
            if (reader.NodeType == XmlNodeType.Element)
            {
                return true;
            }
        }

        return false;
    }

    private static void EnsureDepth(XmlReader reader)
    {
        if (reader.Depth > MaximumDepth)
        {
            throw new InvalidDataException("The manifest XML nesting depth exceeds the configured safety limit.");
        }
    }

    private static DiagnosticRecord Error(string code, string message, Exception? exception = null)
    {
        return new DiagnosticRecord(
            DiagnosticSeverity.Error,
            code,
            message,
            EvidenceLevel.Confirmed,
            exceptionType: exception?.GetType().FullName);
    }
}

