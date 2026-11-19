using System;
using System.Globalization;
using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.IO;
using System.Text.Json;
using YMMProjectManager.Domain;

namespace YMMProjectManager.Infrastructure.Generations;

public sealed class ProjectGenerationStorage
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
    };

    private readonly string rootDirectory;
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> ManifestGates = new(StringComparer.OrdinalIgnoreCase);
    private readonly Func<Stream, byte[], CancellationToken, Task> writeManifest;
    private readonly Action<FileStream> flushManifest;
    private readonly Action? beforeManifestCommit;

    public ProjectGenerationStorage(string? rootDirectory = null) : this(rootDirectory, null, null, null) { }

    internal ProjectGenerationStorage(string? rootDirectory, Func<Stream, byte[], CancellationToken, Task>? writeManifest,
        Action<FileStream>? flushManifest, Action? beforeManifestCommit)
    {
        this.writeManifest = writeManifest ?? ((stream, bytes, token) => stream.WriteAsync(bytes, token).AsTask());
        this.flushManifest = flushManifest ?? (stream => stream.Flush(flushToDisk: true));
        this.beforeManifestCommit = beforeManifestCommit;
        this.rootDirectory = string.IsNullOrWhiteSpace(rootDirectory)
            ? Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "YMMProjectManager",
                "Generations")
            : rootDirectory;
    }

    public string RootDirectory => rootDirectory;

    public string GetProjectDirectory(string projectId) => Path.Combine(rootDirectory, "projects", projectId);

    public string GetManifestPath(string projectId) => Path.Combine(GetProjectDirectory(projectId), "manifest.json");

    public string GetGenerationsDirectory(string projectId) => Path.Combine(GetProjectDirectory(projectId), "generations");

    public string GetDeletedDirectory(string projectId) => Path.Combine(GetProjectDirectory(projectId), "deleted");

    public string GetRestoreBackupsDirectory(string projectId) => Path.Combine(GetProjectDirectory(projectId), "restore-backups");

    internal static bool IsValidGenerationId(string? id) =>
        !string.IsNullOrWhiteSpace(id) && id != "." && id != ".." &&
        id.IndexOfAny(Path.GetInvalidFileNameChars()) < 0 &&
        !id.Contains('/') && !id.Contains('\\') && !id.Contains(':') &&
        !id.EndsWith('.') && !id.EndsWith(' ');

    public string GetGenerationDirectory(string projectId, string generationId)
    {
        if (!IsValidGenerationId(generationId)) throw new InvalidDataException("Invalid generation ID.");
        return Path.Combine(GetGenerationsDirectory(projectId), generationId);
    }

    public string GetGenerationFilePath(string projectId, string generationId) => Path.Combine(GetGenerationDirectory(projectId, generationId), "project.ymmp");

    public string GetGenerationMetadataPath(string projectId, string generationId) => Path.Combine(GetGenerationDirectory(projectId, generationId), "metadata.json");

    public string GetDeletedGenerationDirectory(string projectId, string generationId)
    {
        if (!IsValidGenerationId(generationId)) throw new InvalidDataException("Invalid generation ID.");
        var timestamp = DateTimeOffset.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
        return Path.Combine(GetDeletedDirectory(projectId), $"{generationId}_{timestamp}");
    }

    public Task WriteManifestAsync(string projectId, ProjectGenerationManifest manifest, CancellationToken cancellationToken = default)
        => WriteManifestCoreAsync(projectId, manifest, false, null, cancellationToken);

    internal Task WriteManifestIfUnchangedAsync(string projectId, ProjectGenerationManifest manifest, string? expectedHash, CancellationToken cancellationToken)
        => WriteManifestCoreAsync(projectId, manifest, true, expectedHash, cancellationToken);

    private async Task WriteManifestCoreAsync(string projectId, ProjectGenerationManifest manifest, bool checkVersion, string? expectedHash, CancellationToken cancellationToken)
    {
        var path = Path.GetFullPath(GetManifestPath(projectId));
        var gate = ManifestGates.GetOrAdd(path, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        string? tempPath = null;
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            var bytes = JsonSerializer.SerializeToUtf8Bytes(manifest, JsonOptions);
            var directory = Path.GetDirectoryName(path)!;
            Directory.CreateDirectory(directory);
            var candidate = Path.Combine(directory, $"manifest.json.tmp-{Guid.NewGuid():N}");
            await using (var stream = new FileStream(candidate, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.Asynchronous))
            {
                tempPath = candidate;
                await writeManifest(stream, bytes, cancellationToken).ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
                flushManifest(stream);
            }
            beforeManifestCommit?.Invoke();
            cancellationToken.ThrowIfCancellationRequested();
            if (checkVersion)
            {
                var actualHash = await ReadManifestHashAsync(path, cancellationToken).ConfigureAwait(false);
                if (!string.Equals(actualHash, expectedHash, StringComparison.Ordinal))
                    throw new InvalidDataException("世代 manifest が変更されたため保存を中止しました。再読み込みしてください。");
            }
            cancellationToken.ThrowIfCancellationRequested();
            if (File.Exists(path)) File.Replace(tempPath, path, null, ignoreMetadataErrors: true);
            else File.Move(tempPath, path);
            tempPath = null;
        }
        finally
        {
            if (tempPath is not null) DeleteFileIfExists(tempPath);
            gate.Release();
        }
    }

    private static async Task<string?> ReadManifestHashAsync(string path, CancellationToken cancellationToken)
    {
        try
        {
            await using var stream = File.OpenRead(path);
            return Convert.ToHexString(await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false));
        }
        catch (FileNotFoundException) { return null; }
        catch (DirectoryNotFoundException) { return null; }
    }

    public async Task<ProjectGenerationManifest?> ReadManifestAsync(string projectId, CancellationToken cancellationToken = default)
        => (await ReadManifestSnapshotAsync(projectId, cancellationToken).ConfigureAwait(false)).Manifest;

    internal async Task<(ProjectGenerationManifest? Manifest, string? Hash)> ReadManifestSnapshotAsync(string projectId, CancellationToken cancellationToken = default)
    {
        try
        {
            await using var stream = File.OpenRead(GetManifestPath(projectId));
            using var buffer = new MemoryStream();
            await stream.CopyToAsync(buffer, cancellationToken).ConfigureAwait(false);
            var data = buffer.ToArray();
            var hash = Convert.ToHexString(SHA256.HashData(data));
            using var document = JsonDocument.Parse(data);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object ||
                !root.TryGetProperty("SchemaVersion", out var schema) || !schema.TryGetInt32(out var version))
                throw new InvalidDataException("世代 manifest のスキーマが不正です。");
            if (version != ProjectGenerationManifest.CurrentSchemaVersion)
                throw new NotSupportedException($"未対応の世代 manifest スキーマです: {version}");

            foreach (var name in new[] { "ProjectId", "ProjectPath", "ProjectFileName", "CreatedAt", "UpdatedAt", "Generations" })
                if (!root.TryGetProperty(name, out _)) throw new InvalidDataException($"世代 manifest に必須項目がありません: {name}");
            var entries = root.GetProperty("Generations");
            if (entries.ValueKind != JsonValueKind.Array) throw new InvalidDataException("世代一覧が不正です。");
            foreach (var item in entries.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object) throw new InvalidDataException("世代情報が不正です。");
                foreach (var name in new[] { "GenerationId", "CreatedAt", "DisplayName", "SourceProjectPath", "FileSize", "Sha256", "CreatedByVersion" })
                    if (!item.TryGetProperty(name, out _)) throw new InvalidDataException($"世代情報に必須項目がありません: {name}");
            }
            var manifest = root.Deserialize<ProjectGenerationManifest>(JsonOptions)
                ?? throw new InvalidDataException("世代 manifest が空です。");
            if (!string.Equals(manifest.ProjectId, projectId, StringComparison.OrdinalIgnoreCase) ||
                string.IsNullOrWhiteSpace(manifest.ProjectPath) || string.IsNullOrWhiteSpace(manifest.ProjectFileName) ||
                manifest.Generations is null || manifest.Generations.Any(x => x is null || !IsValidGenerationId(x.GenerationId) ||
                    x.DisplayName is null || x.SourceProjectPath is null || x.Sha256 is null || x.CreatedByVersion is null))
                throw new InvalidDataException("世代 manifest の必須値が不正です。");
            return (manifest, hash);
        }
        catch (FileNotFoundException) { return (null, null); }
        catch (DirectoryNotFoundException) { return (null, null); }
    }

    public bool HasExistingGenerations(string projectId)
    {
        foreach (var directory in new[] { GetGenerationsDirectory(projectId), GetDeletedDirectory(projectId) })
        {
            try { if (Directory.EnumerateFileSystemEntries(directory).Any()) return true; }
            catch (DirectoryNotFoundException) { }
        }
        return false;
    }

    public async Task WriteMetadataAsync(string projectId, string generationId, ProjectGenerationManifestItem metadata, CancellationToken cancellationToken = default)
    {
        var path = GetGenerationMetadataPath(projectId, generationId);
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var tempPath = path + ".tmp";
        try
        {
            await using (var stream = File.Create(tempPath))
            {
                await JsonSerializer.SerializeAsync(stream, metadata, JsonOptions, cancellationToken).ConfigureAwait(false);
            }

            if (File.Exists(path))
            {
                File.Replace(tempPath, path, null, ignoreMetadataErrors: true);
            }
            else
            {
                File.Move(tempPath, path, overwrite: true);
            }
        }
        finally
        {
            DeleteFileIfExists(tempPath);
        }
    }

    public async Task<ProjectGenerationManifestItem?> ReadMetadataAsync(string projectId, string generationId, CancellationToken cancellationToken = default)
    {
        var path = GetGenerationMetadataPath(projectId, generationId);
        if (!File.Exists(path))
        {
            return null;
        }

        await using var stream = File.OpenRead(path);
        return await JsonSerializer.DeserializeAsync<ProjectGenerationManifestItem>(stream, JsonOptions, cancellationToken).ConfigureAwait(false);
    }

    public void EnsureProjectLayout(string projectId)
    {
        Directory.CreateDirectory(GetProjectDirectory(projectId));
        Directory.CreateDirectory(GetGenerationsDirectory(projectId));
        Directory.CreateDirectory(GetDeletedDirectory(projectId));
        Directory.CreateDirectory(GetRestoreBackupsDirectory(projectId));
    }

    public async Task CopyProjectSnapshotAsync(string sourcePath, string destinationPath, CancellationToken cancellationToken = default)
    {
        var directory = Path.GetDirectoryName(destinationPath);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        await using var source = File.Open(sourcePath, FileMode.Open, FileAccess.Read, FileShare.Read);
        await using var destination = File.Create(destinationPath);
        await source.CopyToAsync(destination, cancellationToken).ConfigureAwait(false);
    }

    public async Task ReplaceFileAtomicallyAsync(string sourceTempPath, string targetPath, string? backupPath = null, CancellationToken cancellationToken = default)
    {
        var directory = Path.GetDirectoryName(targetPath);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        if (File.Exists(targetPath))
        {
            if (!string.IsNullOrWhiteSpace(backupPath))
            {
                var backupDirectory = Path.GetDirectoryName(backupPath);
                if (!string.IsNullOrWhiteSpace(backupDirectory))
                {
                    Directory.CreateDirectory(backupDirectory);
                }
                File.Replace(sourceTempPath, targetPath, backupPath, ignoreMetadataErrors: true);
                return;
            }

            File.Move(sourceTempPath, targetPath, overwrite: true);
            return;
        }

        File.Move(sourceTempPath, targetPath, overwrite: true);
    }

    public async Task<string> CreateTemporaryCopyAsync(string sourcePath, string targetDirectory, CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(targetDirectory);
        var tempPath = Path.Combine(targetDirectory, $"{Guid.NewGuid():N}.tmp");
        await CopyProjectSnapshotAsync(sourcePath, tempPath, cancellationToken).ConfigureAwait(false);
        return tempPath;
    }

    public string ResolveRestoreBackupPath(string projectId, string targetProjectPath)
    {
        var projectFileName = Path.GetFileName(targetProjectPath);
        return Path.Combine(GetRestoreBackupsDirectory(projectId), $"{DateTimeOffset.Now:yyyyMMdd-HHmmss}_before-restore{Path.GetExtension(projectFileName)}");
    }

    public async Task MoveDirectoryAsync(string sourceDirectory, string destinationDirectory, CancellationToken cancellationToken = default)
    {
        var destinationParent = Path.GetDirectoryName(destinationDirectory);
        if (!string.IsNullOrWhiteSpace(destinationParent))
        {
            Directory.CreateDirectory(destinationParent);
        }

        if (Directory.Exists(destinationDirectory))
        {
            Directory.Delete(destinationDirectory, recursive: true);
        }

        await Task.Run(() => Directory.Move(sourceDirectory, destinationDirectory), cancellationToken).ConfigureAwait(false);
    }

    public long GetDirectorySizeBytes(string directoryPath)
    {
        if (!Directory.Exists(directoryPath))
        {
            return 0;
        }

        long total = 0;
        foreach (var file in Directory.EnumerateFiles(directoryPath, "*", SearchOption.AllDirectories))
        {
            try
            {
                total += new FileInfo(file).Length;
            }
            catch
            {
            }
        }

        return total;
    }

    public int CountChildDirectories(string directoryPath)
    {
        if (!Directory.Exists(directoryPath))
        {
            return 0;
        }

        try
        {
            return Directory.EnumerateDirectories(directoryPath).Count();
        }
        catch
        {
            return 0;
        }
    }

    public void DeleteFileIfExists(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch
        {
        }
    }
}
