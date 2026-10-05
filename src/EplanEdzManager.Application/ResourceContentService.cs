using System.Diagnostics;
using EplanEdzManager.Core;
using EplanEdzManager.Edz.Archives;

namespace EplanEdzManager.Application;

internal sealed class ResourceContentService
{
    public Task<byte[]> ReadResourceAsync(ResourceItem resource, int maximumBytes, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(resource);
        return Task.Run(() => ReadResourceCore(resource, maximumBytes, cancellationToken), cancellationToken);
    }

    public Task<string> ReadRawMetadataAsync(PartSummary part, int maximumBytes, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(part);
        return Task.Run(() =>
        {
            EnsureSource(part.EdzPath);
            using var archive = OpenArchive(part.EdzPath, cancellationToken);
            using var source = archive.OpenEntryStream(part.RawMetadataReference, cancellationToken);
            using var memory = ReadBounded(source, maximumBytes, cancellationToken);
            using var reader = new StreamReader(memory, detectEncodingFromByteOrderMarks: true);
            return reader.ReadToEnd();
        }, cancellationToken);
    }

    public Task ExportResourceAsync(ResourceItem resource, string destinationPath, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(resource);
        if (string.IsNullOrWhiteSpace(destinationPath)) throw new ArgumentException("A destination path is required.", nameof(destinationPath));
        return Task.Run(() =>
        {
            var archivePath = ValidateResource(resource);
            EnsureSource(resource.EdzPath);
            var outputPath = Path.GetFullPath(destinationPath);
            Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
            var temporaryPath = outputPath + ".tmp-" + Guid.NewGuid().ToString("N");
            try
            {
                using var archive = OpenArchive(resource.EdzPath, cancellationToken);
                using var source = archive.OpenEntryStream(archivePath, cancellationToken);
                using (var output = new FileStream(temporaryPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1024 * 1024, FileOptions.SequentialScan))
                {
                    var buffer = new byte[1024 * 1024];
                    while (true)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        var read = source.Read(buffer, 0, buffer.Length);
                        if (read == 0) break;
                        output.Write(buffer, 0, read);
                    }
                    cancellationToken.ThrowIfCancellationRequested();
                    output.Flush(true);
                }
                File.Move(temporaryPath, outputPath, overwrite: true);
            }
            finally
            {
                if (File.Exists(temporaryPath)) File.Delete(temporaryPath);
            }
        }, cancellationToken);
    }

    public void OpenContainingFolder(string sourcePath)
    {
        var fullPath = Path.GetFullPath(sourcePath);
        if (!File.Exists(fullPath)) throw new FileNotFoundException("The source EDZ was moved or deleted.", fullPath);
        Process.Start(new ProcessStartInfo("explorer.exe", "/select,\"" + fullPath + "\"") { UseShellExecute = true });
    }

    private static byte[] ReadResourceCore(ResourceItem resource, int maximumBytes, CancellationToken cancellationToken)
    {
        var archivePath = ValidateResource(resource);
        EnsureSource(resource.EdzPath);
        using var archive = OpenArchive(resource.EdzPath, cancellationToken);
        using var source = archive.OpenEntryStream(archivePath, cancellationToken);
        using var memory = ReadBounded(source, maximumBytes, cancellationToken);
        return memory.ToArray();
    }

    private static string ValidateResource(ResourceItem resource)
    {
        if (!resource.ExistsInArchive) throw new FileNotFoundException("The resource is missing from the EDZ archive.");
        if (string.IsNullOrWhiteSpace(resource.ResourceType) || string.IsNullOrWhiteSpace(resource.RawLocator)
            || !EdzLocator.TryCreate(resource.ResourceType, resource.RawLocator, out var locator, out _))
        {
            throw new InvalidDataException("The resource locator is unsafe or invalid.");
        }

        if (!string.Equals(locator!.ArchivePath, resource.ArchivePath, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("The indexed resource path no longer matches its safe locator.");
        }

        return locator.ArchivePath;
    }

    private static void EnsureSource(string path)
    {
        if (!File.Exists(path)) throw new FileNotFoundException("The source EDZ was moved or deleted.", path);
    }

    private static EplanEdzManager.Core.Archives.IEdzArchive OpenArchive(string path, CancellationToken cancellationToken)
    {
        var result = new SevenZipEdzArchiveReader().Open(path, cancellationToken);
        return result.Archive ?? throw new InvalidDataException(result.Diagnostics.FirstOrDefault()?.Message ?? "The EDZ archive could not be opened.");
    }

    private static MemoryStream ReadBounded(Stream source, int maximumBytes, CancellationToken cancellationToken)
    {
        var memory = new MemoryStream();
        var buffer = new byte[64 * 1024];
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var read = source.Read(buffer, 0, buffer.Length);
            if (read == 0) break;
            if (memory.Length + read > maximumBytes) throw new InvalidDataException($"The resource exceeds the {maximumBytes:N0}-byte preview limit.");
            memory.Write(buffer, 0, read);
        }
        memory.Position = 0;
        return memory;
    }
}
