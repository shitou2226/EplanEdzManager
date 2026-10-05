using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using EplanEdzManager.Core.Diagnostics;

namespace EplanEdzManager.Core;

public sealed class EdzLocator
{
    private static readonly Regex EncodedByte = new Regex("%[0-9a-fA-F]{2}", RegexOptions.CultureInvariant);

    private EdzLocator(string resourceType, string locator)
    {
        ResourceType = resourceType;
        Locator = locator;
        ArchivePath = "items/" + resourceType + "/" + locator;
    }

    public string ResourceType { get; }

    public string Locator { get; }

    public string ArchivePath { get; }

    public static bool TryCreate(
        string resourceType,
        string locator,
        out EdzLocator? result,
        out DiagnosticRecord? diagnostic)
    {
        result = null;
        diagnostic = null;

        var normalizedType = Normalize(resourceType);
        var normalizedLocator = Normalize(locator);
        if (!IsSafeResourceType(normalizedType) || !IsSafeRelativePath(normalizedLocator))
        {
            diagnostic = new DiagnosticRecord(
                DiagnosticSeverity.Error,
                "EDZ201",
                "The manifest locator is unsafe or invalid.",
                EvidenceLevel.Confirmed,
                entryPath: locator);
            return false;
        }

        result = new EdzLocator(normalizedType, normalizedLocator);
        return true;
    }

    private static string Normalize(string? value)
    {
        return (value ?? string.Empty)
            .Normalize(NormalizationForm.FormC)
            .Replace('\\', '/');
    }

    private static bool IsSafeResourceType(string value)
    {
        return value.Length is > 0 and <= 128
            && value.IndexOf('/') < 0
            && value.IndexOf(':') < 0
            && !value.Equals(".", StringComparison.Ordinal)
            && !value.Equals("..", StringComparison.Ordinal)
            && !ContainsControlCharacter(value)
            && !EncodedByte.IsMatch(value);
    }

    private static bool IsSafeRelativePath(string value)
    {
        if (!IsSafePathShape(value))
        {
            return false;
        }

        // EPLAN samples legitimately use literal "%2F" in archive filenames. Keep the
        // encoded spelling for lookup, but repeatedly decode a copy so encoded or
        // double-encoded traversal cannot bypass validation.
        var decoded = value;
        for (var depth = 0; depth < 5; depth++)
        {
            string next;
            try
            {
                next = Uri.UnescapeDataString(decoded).Replace('\\', '/');
            }
            catch (UriFormatException)
            {
                return false;
            }

            if (!IsSafePathShape(next))
            {
                return false;
            }

            if (string.Equals(next, decoded, StringComparison.Ordinal))
            {
                return true;
            }

            decoded = next;
        }

        return !EncodedByte.IsMatch(decoded);
    }

    private static bool IsSafePathShape(string value)
    {
        if (value.Length is 0 or > 2048
            || value.StartsWith("/", StringComparison.Ordinal)
            || value.StartsWith("//", StringComparison.Ordinal)
            || value.IndexOf(':') >= 0
            || value.Contains("//")
            || ContainsControlCharacter(value))
        {
            return false;
        }

        return value.Split('/').All(segment =>
            segment.Length > 0
            && !segment.Equals(".", StringComparison.Ordinal)
            && !segment.Equals("..", StringComparison.Ordinal));
    }

    private static bool ContainsControlCharacter(string value)
    {
        return value.Any(character => CharUnicodeInfo.GetUnicodeCategory(character) == UnicodeCategory.Control);
    }
}

