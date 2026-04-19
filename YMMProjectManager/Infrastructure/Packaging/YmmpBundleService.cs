using System.IO.Compression;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.IO;

namespace YMMProjectManager.Infrastructure.Packaging;

public sealed class YmmpBundleService
{
    private const string ManifestEntryName = "manifest.json";
    private const string ProjectEntryName = "project.ymmp";
    private readonly FileLogger logger;

    public YmmpBundleService(FileLogger logger)
    {
        this.logger = logger;
    }

    public async Task<(bool Success, string? ErrorMessage, string? OutputPath)> CreateBundleAsync(
        string ymmpPath,
        string outputYmmpxPath,
        CancellationToken token,
        IProgress<double>? progress = null)
    {
        try
        {
            token.ThrowIfCancellationRequested();
            var projectText = await File.ReadAllTextAsync(ymmpPath, token).ConfigureAwait(false);
            var filePaths = CollectFilePaths(projectText);
            var existingPaths = filePaths
                .Where(File.Exists)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            var outputDirectory = Path.GetDirectoryName(outputYmmpxPath);
            if (!string.IsNullOrWhiteSpace(outputDirectory))
            {
                Directory.CreateDirectory(outputDirectory);
            }

            if (File.Exists(outputYmmpxPath))
            {
                File.Delete(outputYmmpxPath);
            }

            var manifest = new BundleManifest
            {
                ProjectFileName = Path.GetFileName(ymmpPath),
            };

            await using var zipStream = new FileStream(outputYmmpxPath, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            using var archive = new ZipArchive(zipStream, ZipArchiveMode.Create);

            var projectEntry = archive.CreateEntry(ProjectEntryName, CompressionLevel.Optimal);
            await using (var projectEntryStream = projectEntry.Open())
            await using (var writer = new StreamWriter(projectEntryStream))
            {
                await writer.WriteAsync(projectText).ConfigureAwait(false);
            }

            for (var i = 0; i < existingPaths.Count; i++)
            {
                token.ThrowIfCancellationRequested();
                var sourcePath = existingPaths[i];
                var fileName = Path.GetFileName(sourcePath);
                var bundlePath = $"files/{i:D6}_{fileName}";
                var entry = archive.CreateEntry(bundlePath, CompressionLevel.Optimal);
                await using (var entryStream = entry.Open())
                await using (var sourceStream = new FileStream(sourcePath, FileMode.Open, FileAccess.Read, FileShare.Read))
                {
                    await sourceStream.CopyToAsync(entryStream, token).ConfigureAwait(false);
                }

                manifest.Files.Add(new BundleFileEntry
                {
                    OriginalPath = sourcePath,
                    BundlePath = bundlePath,
                });

                progress?.Report((i + 1d) / Math.Max(existingPaths.Count, 1));
            }

            var manifestEntry = archive.CreateEntry(ManifestEntryName, CompressionLevel.Optimal);
            await using (var manifestStream = manifestEntry.Open())
            await using (var writer = new StreamWriter(manifestStream))
            {
                var manifestText = JsonSerializer.Serialize(manifest, new JsonSerializerOptions { WriteIndented = true });
                await writer.WriteAsync(manifestText).ConfigureAwait(false);
            }

            logger.Info($"Bundle.Create completed. ymmp={ymmpPath}, output={outputYmmpxPath}, files={existingPaths.Count}");
            logger.Flush();
            return (true, null, outputYmmpxPath);
        }
        catch (OperationCanceledException)
        {
            logger.Info($"Bundle.Create canceled. ymmp={ymmpPath}");
            logger.Flush();
            throw;
        }
        catch (Exception ex)
        {
            logger.Error(ex, $"Bundle.Create failed. ymmp={ymmpPath}, output={outputYmmpxPath}");
            logger.Flush();
            return (false, "同梱ファイルの作成に失敗しました。ログを確認してください。", null);
        }
    }

    public async Task<(bool Success, string? ErrorMessage, string? RestoredYmmpPath)> ExtractBundleAsync(
        string ymmpxPath,
        string outputDirectory,
        CancellationToken token,
        IProgress<double>? progress = null)
    {
        try
        {
            token.ThrowIfCancellationRequested();
            var extractRoot = Path.Combine(outputDirectory, Path.GetFileNameWithoutExtension(ymmpxPath));
            Directory.CreateDirectory(extractRoot);

            await using var zipStream = new FileStream(ymmpxPath, FileMode.Open, FileAccess.Read, FileShare.Read);
            using var archive = new ZipArchive(zipStream, ZipArchiveMode.Read);

            var manifestEntry = archive.GetEntry(ManifestEntryName);
            var projectEntry = archive.GetEntry(ProjectEntryName);
            if (manifestEntry is null || projectEntry is null)
            {
                return (false, "同梱ファイルの形式が不正です。", null);
            }

            BundleManifest manifest;
            await using (var manifestStream = manifestEntry.Open())
            {
                manifest = await JsonSerializer.DeserializeAsync<BundleManifest>(manifestStream, cancellationToken: token).ConfigureAwait(false)
                           ?? new BundleManifest();
            }

            var projectText = await ReadEntryTextAsync(projectEntry, token).ConfigureAwait(false);
            var replacements = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            for (var i = 0; i < manifest.Files.Count; i++)
            {
                token.ThrowIfCancellationRequested();
                var item = manifest.Files[i];
                var fileEntry = archive.GetEntry(item.BundlePath);
                if (fileEntry is null)
                {
                    continue;
                }

                var safeRelative = item.BundlePath.Replace('/', Path.DirectorySeparatorChar);
                var destinationPath = Path.GetFullPath(Path.Combine(extractRoot, safeRelative));
                if (!destinationPath.StartsWith(extractRoot, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var destinationDir = Path.GetDirectoryName(destinationPath);
                if (!string.IsNullOrWhiteSpace(destinationDir))
                {
                    Directory.CreateDirectory(destinationDir);
                }

                await using (var source = fileEntry.Open())
                await using (var destination = new FileStream(destinationPath, FileMode.Create, FileAccess.Write, FileShare.None))
                {
                    await source.CopyToAsync(destination, token).ConfigureAwait(false);
                }

                replacements[item.OriginalPath] = destinationPath;
                progress?.Report((i + 1d) / Math.Max(manifest.Files.Count, 1));
            }

            foreach (var (oldPath, newPath) in replacements)
            {
                var oldToken = JsonSerializer.Serialize(oldPath);
                var newToken = JsonSerializer.Serialize(newPath);
                var pattern = "(\"FilePath\"\\s*:\\s*)" + Regex.Escape(oldToken);
                projectText = Regex.Replace(projectText, pattern, "$1" + newToken, RegexOptions.CultureInvariant);
            }

            var projectName = string.IsNullOrWhiteSpace(manifest.ProjectFileName)
                ? $"{Path.GetFileNameWithoutExtension(ymmpxPath)}.ymmp"
                : manifest.ProjectFileName;
            var restoredYmmpPath = Path.Combine(extractRoot, projectName);
            await File.WriteAllTextAsync(restoredYmmpPath, projectText, token).ConfigureAwait(false);

            logger.Info($"Bundle.Extract completed. ymmpx={ymmpxPath}, output={restoredYmmpPath}, files={replacements.Count}");
            logger.Flush();
            return (true, null, restoredYmmpPath);
        }
        catch (OperationCanceledException)
        {
            logger.Info($"Bundle.Extract canceled. ymmpx={ymmpxPath}");
            logger.Flush();
            throw;
        }
        catch (Exception ex)
        {
            logger.Error(ex, $"Bundle.Extract failed. ymmpx={ymmpxPath}, output={outputDirectory}");
            logger.Flush();
            return (false, "同梱ファイルの展開に失敗しました。ログを確認してください。", null);
        }
    }

    private static List<string> CollectFilePaths(string projectText)
    {
        var result = new List<string>();
        JsonNode? root;
        try
        {
            root = JsonNode.Parse(projectText);
        }
        catch
        {
            return result;
        }

        if (root is null)
        {
            return result;
        }

        CollectFilePathNodes(root, result);
        return result;
    }

    private static void CollectFilePathNodes(JsonNode node, IList<string> result)
    {
        switch (node)
        {
            case JsonObject obj:
            {
                if (obj.TryGetPropertyValue("FilePath", out var filePathNode) &&
                    filePathNode is JsonValue filePathValue &&
                    filePathValue.TryGetValue<string>(out var pathValue) &&
                    !string.IsNullOrWhiteSpace(pathValue))
                {
                    result.Add(pathValue);
                }

                foreach (var child in obj)
                {
                    if (child.Value is not null)
                    {
                        CollectFilePathNodes(child.Value, result);
                    }
                }

                break;
            }
            case JsonArray arr:
            {
                foreach (var child in arr)
                {
                    if (child is not null)
                    {
                        CollectFilePathNodes(child, result);
                    }
                }

                break;
            }
        }
    }

    private static async Task<string> ReadEntryTextAsync(ZipArchiveEntry entry, CancellationToken token)
    {
        await using var stream = entry.Open();
        using var reader = new StreamReader(stream);
        return await reader.ReadToEndAsync(token).ConfigureAwait(false);
    }

    private sealed class BundleManifest
    {
        public string ProjectFileName { get; set; } = "project.ymmp";
        public List<BundleFileEntry> Files { get; set; } = [];
    }

    private sealed class BundleFileEntry
    {
        public string OriginalPath { get; set; } = string.Empty;
        public string BundlePath { get; set; } = string.Empty;
    }
}
