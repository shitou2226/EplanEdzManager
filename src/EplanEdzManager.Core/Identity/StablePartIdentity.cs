using System.Security.Cryptography;
using System.Text;

namespace EplanEdzManager.Core.Identity;

public enum StableIdentityKind
{
    Primary,
    PackageFallback
}

public sealed class StablePartIdentity
{
    private StablePartIdentity(string value, StableIdentityKind kind)
    {
        Value = value;
        Kind = kind;
    }

    public string Value { get; }
    public StableIdentityKind Kind { get; }

    public static StablePartIdentity Create(string? manufacturer, string? partNumber, string? variant, string? packageKey)
    {
        var manufacturerKey = Normalize(manufacturer);
        var partNumberKey = Normalize(partNumber);
        var variantKey = Normalize(variant);
        var packageKeyValue = Normalize(packageKey);
        StableIdentityKind kind;
        string payload;
        if (manufacturerKey.Length > 0 && partNumberKey.Length > 0)
        {
            kind = StableIdentityKind.Primary;
            payload = Compose("PRIMARY", manufacturerKey, partNumberKey, variantKey);
        }
        else if (packageKeyValue.Length > 0)
        {
            kind = StableIdentityKind.PackageFallback;
            payload = Compose("PACKAGE", manufacturerKey, partNumberKey, variantKey, packageKeyValue);
        }
        else
        {
            throw new InvalidOperationException("A stable part identity requires Manufacturer + Part Number, or a Package Key fallback.");
        }

        return new StablePartIdentity("LP1:" + ComputeSha256(payload), kind);
    }

    public static bool TryCreate(string? manufacturer, string? partNumber, string? variant, string? packageKey, out StablePartIdentity? identity)
    {
        try
        {
            identity = Create(manufacturer, partNumber, variant, packageKey);
            return true;
        }
        catch (InvalidOperationException)
        {
            identity = null;
            return false;
        }
    }

    internal static string Normalize(string? value)
    {
        var source = (value ?? string.Empty).Normalize(NormalizationForm.FormKC).Trim();
        if (source.Length == 0) return string.Empty;
        var builder = new StringBuilder(source.Length);
        var pendingSpace = false;
        foreach (var character in source)
        {
            if (char.IsWhiteSpace(character))
            {
                pendingSpace = builder.Length > 0;
                continue;
            }

            if (pendingSpace)
            {
                builder.Append(' ');
                pendingSpace = false;
            }
            builder.Append(char.ToUpperInvariant(character));
        }
        return builder.ToString();
    }

    private static string Compose(params string[] values)
    {
        var builder = new StringBuilder();
        foreach (var value in values)
        {
            builder.Append(value.Length).Append(':').Append(value).Append('|');
        }
        return builder.ToString();
    }

    internal static string ComputeSha256(string value)
    {
        using (var sha256 = SHA256.Create())
        {
            var bytes = sha256.ComputeHash(Encoding.UTF8.GetBytes(value));
            var builder = new StringBuilder(bytes.Length * 2);
            foreach (var item in bytes) builder.Append(item.ToString("x2"));
            return builder.ToString();
        }
    }
}

public static class PartSourceIdentity
{
    public static string Create(string edzPath, string? packageKey, string rawMetadataReference)
    {
        if (string.IsNullOrWhiteSpace(edzPath)) throw new ArgumentException("An EDZ path is required.", nameof(edzPath));
        if (string.IsNullOrWhiteSpace(rawMetadataReference)) throw new ArgumentException("A raw metadata reference is required.", nameof(rawMetadataReference));
        var fullPath = Path.GetFullPath(edzPath).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var pathKey = StablePartIdentity.Normalize(fullPath);
        var packageKeyValue = StablePartIdentity.Normalize(packageKey);
        var metadataKey = StablePartIdentity.Normalize(rawMetadataReference.Replace('\\', '/'));
        var payload = pathKey.Length + ":" + pathKey + "|" + packageKeyValue.Length + ":" + packageKeyValue + "|" + metadataKey.Length + ":" + metadataKey;
        return "SI1:" + StablePartIdentity.ComputeSha256(payload);
    }
}
