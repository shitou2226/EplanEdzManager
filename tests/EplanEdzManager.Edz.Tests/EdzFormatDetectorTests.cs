using EplanEdzManager.Edz;
using EplanEdzManager.Core.Archives;
using Xunit;

namespace EplanEdzManager.Edz.Tests;

public sealed class EdzFormatDetectorTests
{
    [Fact]
    public void Detect_returns_invalid_file_for_missing_path()
    {
        var result = EdzFormatDetector.Detect(Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".edz"));

        Assert.Equal(EdzFormatKind.InvalidFile, result.Kind);
        Assert.Equal("EDZ002", result.Diagnostic?.Code);
    }

    [Fact]
    public void Detect_returns_not_edz_for_non_edz_extension()
    {
        var path = CreateFile("sample.bin", new byte[] { 0x37, 0x7A, 0xBC, 0xAF, 0x27, 0x1C });
        try
        {
            var result = EdzFormatDetector.Detect(path);

            Assert.Equal(EdzFormatKind.NotEdz, result.Kind);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Detect_recognizes_known_7z_edz_signature()
    {
        var path = CreateFile("sample.edz", new byte[] { 0x37, 0x7A, 0xBC, 0xAF, 0x27, 0x1C, 0, 4 });
        try
        {
            var result = EdzFormatDetector.Detect(path);

            Assert.Equal(EdzFormatKind.CurrentKnown7zEdz, result.Kind);
        }
        finally
        {
            File.Delete(path);
        }
    }

    private static string CreateFile(string name, byte[] bytes)
    {
        var directory = Path.Combine(Path.GetTempPath(), "EplanEdzManager.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, name);
        File.WriteAllBytes(path, bytes);
        return path;
    }
}

