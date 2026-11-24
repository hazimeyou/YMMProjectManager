using System;
using System.Globalization;
using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.IO;
using System.Text.Json;
using YMMProjectManager.Domain;

namespace YMMProjectManager.Infrastructure.Generations;

//CSJ,class=ProjectGenerationStorage,世代ファイルの保管
public sealed class ProjectGenerationStorage
{
    //CSJ,field=JsonOptions,JSONオプション
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
    };

    //CSJ,field=rootDirectory,ルートディレクトリ
    private readonly string rootDirectory;
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> ManifestGates = new(StringComparer.OrdinalIgnoreCase);
    //CSJ,field=writeManifest,書き込み目録
    private readonly Func<Stream, byte[], CancellationToken, Task> writeManifest;
    private readonly Action<FileStream> flushManifest;
    //CSJ,field=beforeManifestCommit,世代目録の保存確定前の処理
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

    //CSJ,property=RootDirectory,ルートディレクトリ
    public string RootDirectory => rootDirectory;

    //CSJ,method=GetProjectDirectory,プロジェクトディレクトリを取得する
    public string GetProjectDirectory(string projectId) => Path.Combine(rootDirectory, "projects", projectId);

    //CSJ,method=GetManifestPath,目録パスを取得する
    public string GetManifestPath(string projectId) => Path.Combine(GetProjectDirectory(projectId), "manifest.json");

    //CSJ,method=GetGenerationsDirectory,世代ディレクトリを取得する
    public string GetGenerationsDirectory(string projectId) => Path.Combine(GetProjectDirectory(projectId), "generations");

    //CSJ,method=GetDeletedDirectory,削除済みのディレクトリを取得する
    public string GetDeletedDirectory(string projectId) => Path.Combine(GetProjectDirectory(projectId), "deleted");

    //CSJ,method=GetRestoreBackupsDirectory,復元用バックアップのディレクトリを取得する
    public string GetRestoreBackupsDirectory(string projectId) => Path.Combine(GetProjectDirectory(projectId), "restore-backups");

    //CSJ,method=IsValidGenerationId,有効世代IDかを判定する
    internal static bool IsValidGenerationId(string? id) =>
        !string.IsNullOrWhiteSpace(id) && id != "." && id != ".." &&
        id.IndexOfAny(Path.GetInvalidFileNameChars()) < 0 &&
        !id.Contains('/') && !id.Contains('\\') && !id.Contains(':') &&
        !id.EndsWith('.') && !id.EndsWith(' ');

    //CSJ,method=GetGenerationDirectory,世代ディレクトリを取得する
    public string GetGenerationDirectory(string projectId, string generationId)
    {
        if (!IsValidGenerationId(generationId)) throw new InvalidDataException("Invalid generation ID.");
        return Path.Combine(GetGenerationsDirectory(projectId), generationId);
    }

    //CSJ,method=GetGenerationFilePath,世代ファイルパスを取得する
    public string GetGenerationFilePath(string projectId, string generationId) => Path.Combine(GetGenerationDirectory(projectId, generationId), "project.ymmp");

    //CSJ,method=GetGenerationMetadataPath,世代メタデータパスを取得する
    public string GetGenerationMetadataPath(string projectId, string generationId) => Path.Combine(GetGenerationDirectory(projectId, generationId), "metadata.json");

    //CSJ,method=GetDeletedGenerationDirectory,削除済み世代ディレクトリを取得する
    public string GetDeletedGenerationDirectory(string projectId, string generationId)
    {
        if (!IsValidGenerationId(generationId)) throw new InvalidDataException("Invalid generation ID.");
        //CSJ,variable=timestamp,タイムスタンプ
        var timestamp = DateTimeOffset.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
        return Path.Combine(GetDeletedDirectory(projectId), $"{generationId}_{timestamp}");
    }

    //CSJ,method=WriteManifestAsync,目録を書き込む（非同期）
    public Task WriteManifestAsync(string projectId, ProjectGenerationManifest manifest, CancellationToken cancellationToken = default)
        => WriteManifestCoreAsync(projectId, manifest, false, null, cancellationToken);

    internal Task WriteManifestIfUnchangedAsync(string projectId, ProjectGenerationManifest manifest, string? expectedHash, CancellationToken cancellationToken)
        => WriteManifestCoreAsync(projectId, manifest, true, expectedHash, cancellationToken);

    //CSJ,method=WriteManifestCoreAsync,目録中核を書き込む（非同期）
    private async Task WriteManifestCoreAsync(string projectId, ProjectGenerationManifest manifest, bool checkVersion, string? expectedHash, CancellationToken cancellationToken)
    {
        //CSJ,variable=path,パス
        var path = Path.GetFullPath(GetManifestPath(projectId));
        //CSJ,variable=gate,排他制御
        var gate = ManifestGates.GetOrAdd(path, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        //CSJ,variable=tempPath,一時パス
        string? tempPath = null;
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            //CSJ,variable=bytes,バイト列
            var bytes = JsonSerializer.SerializeToUtf8Bytes(manifest, JsonOptions);
            //CSJ,variable=directory,ディレクトリ
            var directory = Path.GetDirectoryName(path)!;
            Directory.CreateDirectory(directory);
            var candidate = Path.Combine(directory, $"manifest.json.tmp-{Guid.NewGuid():N}");
            //CSJ,variable=stream,ストリーム
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
                //CSJ,variable=actualHash,実測値ハッシュ
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

    //CSJ,method=ReadManifestHashAsync,目録ハッシュを読み取る（非同期）
    private static async Task<string?> ReadManifestHashAsync(string path, CancellationToken cancellationToken)
    {
        try
        {
            //CSJ,variable=stream,ストリーム
            await using var stream = File.OpenRead(path);
            return Convert.ToHexString(await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false));
        }
        catch (FileNotFoundException) { return null; }
        catch (DirectoryNotFoundException) { return null; }
    }

    //CSJ,method=ReadManifestAsync,目録を読み取る（非同期）
    public async Task<ProjectGenerationManifest?> ReadManifestAsync(string projectId, CancellationToken cancellationToken = default)
        => (await ReadManifestSnapshotAsync(projectId, cancellationToken).ConfigureAwait(false)).Manifest;

    //CSJ,method=ReadManifestSnapshotAsync,目録スナップショットを読み取る（非同期）
    internal async Task<(ProjectGenerationManifest? Manifest, string? Hash)> ReadManifestSnapshotAsync(string projectId, CancellationToken cancellationToken = default)
    {
        try
        {
            // Atomic replacement keeps this handle on the original version while new readers see the new version.
            //CSJ,variable=stream,ストリーム
            await using var stream = new FileStream(GetManifestPath(projectId), FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
            //CSJ,variable=buffer,バッファー
            using var buffer = new MemoryStream();
            await stream.CopyToAsync(buffer, cancellationToken).ConfigureAwait(false);
            //CSJ,variable=data,データ
            var data = buffer.ToArray();
            //CSJ,variable=hash,ハッシュ
            var hash = Convert.ToHexString(SHA256.HashData(data));
            //CSJ,variable=document,文書
            using var document = JsonDocument.Parse(data);
            //CSJ,variable=root,ルート
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object ||
                !root.TryGetProperty("SchemaVersion", out var schema) || !schema.TryGetInt32(out var version))
                throw new InvalidDataException("世代 manifest のスキーマが不正です。");
            if (version != ProjectGenerationManifest.CurrentSchemaVersion)
                throw new NotSupportedException($"未対応の世代 manifest スキーマです: {version}");

            //CSJ,variable=name,名前
            foreach (var name in new[] { "ProjectId", "ProjectPath", "ProjectFileName", "CreatedAt", "UpdatedAt", "Generations" })
                if (!root.TryGetProperty(name, out _)) throw new InvalidDataException($"世代 manifest に必須項目がありません: {name}");
            //CSJ,variable=entries,項目一覧
            var entries = root.GetProperty("Generations");
            if (entries.ValueKind != JsonValueKind.Array) throw new InvalidDataException("世代一覧が不正です。");
            foreach (var item in entries.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object) throw new InvalidDataException("世代情報が不正です。");
                //CSJ,variable=name,名前
                foreach (var name in new[] { "GenerationId", "CreatedAt", "DisplayName", "SourceProjectPath", "FileSize", "Sha256", "CreatedByVersion" })
                    if (!item.TryGetProperty(name, out _)) throw new InvalidDataException($"世代情報に必須項目がありません: {name}");
            }
            //CSJ,variable=manifest,目録
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

    //CSJ,method=HasExistingGenerations,既存世代の有無を判定する
    public bool HasExistingGenerations(string projectId)
    {
        //CSJ,variable=directory,ディレクトリ
        foreach (var directory in new[] { GetGenerationsDirectory(projectId), GetDeletedDirectory(projectId) })
        {
            try { if (Directory.EnumerateFileSystemEntries(directory).Any()) return true; }
            catch (DirectoryNotFoundException) { }
        }
        return false;
    }

    //CSJ,method=WriteMetadataAsync,メタデータを書き込む（非同期）
    public async Task WriteMetadataAsync(string projectId, string generationId, ProjectGenerationManifestItem metadata, CancellationToken cancellationToken = default)
    {
        //CSJ,variable=path,パス
        var path = GetGenerationMetadataPath(projectId, generationId);
        //CSJ,variable=directory,ディレクトリ
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        //CSJ,variable=tempPath,一時パス
        var tempPath = path + ".tmp";
        try
        {
            //CSJ,variable=stream,ストリーム
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

    //CSJ,method=ReadMetadataAsync,メタデータを読み取る（非同期）
    public async Task<ProjectGenerationManifestItem?> ReadMetadataAsync(string projectId, string generationId, CancellationToken cancellationToken = default)
    {
        //CSJ,variable=path,パス
        var path = GetGenerationMetadataPath(projectId, generationId);
        if (!File.Exists(path))
        {
            return null;
        }

        //CSJ,variable=stream,ストリーム
        await using var stream = File.OpenRead(path);
        return await JsonSerializer.DeserializeAsync<ProjectGenerationManifestItem>(stream, JsonOptions, cancellationToken).ConfigureAwait(false);
    }

    //CSJ,method=EnsureProjectLayout,プロジェクト配置を確認する
    public void EnsureProjectLayout(string projectId)
    {
        Directory.CreateDirectory(GetProjectDirectory(projectId));
        Directory.CreateDirectory(GetGenerationsDirectory(projectId));
        Directory.CreateDirectory(GetDeletedDirectory(projectId));
        Directory.CreateDirectory(GetRestoreBackupsDirectory(projectId));
    }

    //CSJ,method=CopyProjectSnapshotAsync,プロジェクトスナップショットをコピーする（非同期）
    public async Task CopyProjectSnapshotAsync(string sourcePath, string destinationPath, CancellationToken cancellationToken = default)
    {
        //CSJ,variable=directory,ディレクトリ
        var directory = Path.GetDirectoryName(destinationPath);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        await using var source = File.Open(sourcePath, FileMode.Open, FileAccess.Read, FileShare.Read);
        //CSJ,variable=destination,格納先
        await using var destination = File.Create(destinationPath);
        await source.CopyToAsync(destination, cancellationToken).ConfigureAwait(false);
    }

    //CSJ,method=ReplaceFileAtomicallyAsync,ファイルをアトミックに置換する（非同期）
    public async Task ReplaceFileAtomicallyAsync(string sourceTempPath, string targetPath, string? backupPath = null, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var source = Path.GetFullPath(sourceTempPath);
        var target = Path.GetFullPath(targetPath);
        var directory = Path.GetDirectoryName(target)!;
        using var targetFence = OpenOrCreateOutputDirectory(directory);
        using var sourceFence = OwnedFilePublication.DirectoryFence.Open(Path.GetDirectoryName(source)!, enforceShare: true);
        await using var original = ProjectGenerationRestorePublication.OpenTarget(target);
        using var candidateHandle = OwnedFilePublication.OpenOrdinaryPath(source, expectDirectory: false,
            allowRename: true, FileShare.None);
        using var candidate = new FileStream(candidateHandle, FileAccess.Read);
        await ProjectGenerationRestorePublication.PublishAsync(candidate, original, target, backupPath, cancellationToken).ConfigureAwait(false);
    }

    internal static OwnedFilePublication.DirectoryFence OpenOrCreateOutputDirectory(string fullPath)
    {
        var missing = new Stack<string>();
        var existing = fullPath;
        while (!Directory.Exists(existing))
        {
            missing.Push(existing);
            existing = Path.GetDirectoryName(existing) ?? throw new DirectoryNotFoundException(fullPath);
        }

        var fence = OwnedFilePublication.DirectoryFence.Open(existing, enforceShare: true);
        try
        {
            while (missing.TryPop(out var directory))
            {
                // Only create one level below a directory already checked and pinned.
                Directory.CreateDirectory(directory);
                fence.Add(directory);
            }
            return fence;
        }
        catch
        {
            fence.Dispose();
            throw;
        }
    }

    //CSJ,method=CreateTemporaryCopyAsync,一時コピーを作成する（非同期）
    public async Task<string> CreateTemporaryCopyAsync(string sourcePath, string targetDirectory, CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(targetDirectory);
        //CSJ,variable=tempPath,一時パス
        var tempPath = Path.Combine(targetDirectory, $"{Guid.NewGuid():N}.tmp");
        await CopyProjectSnapshotAsync(sourcePath, tempPath, cancellationToken).ConfigureAwait(false);
        return tempPath;
    }

    //CSJ,method=ResolveRestoreBackupPath,復元バックアップ先のパスを解決する
    public string ResolveRestoreBackupPath(string projectId, string targetProjectPath)
    {
        //CSJ,variable=projectFileName,プロジェクトファイル名
        var projectFileName = Path.GetFileName(targetProjectPath);
        return Path.Combine(GetRestoreBackupsDirectory(projectId), $"{DateTimeOffset.Now:yyyyMMdd-HHmmss}_{Guid.NewGuid():N}_before-restore{Path.GetExtension(projectFileName)}");
    }

    //CSJ,method=MoveDirectoryAsync,ディレクトリを移動する（非同期）
    public async Task MoveDirectoryAsync(string sourceDirectory, string destinationDirectory, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        //CSJ,variable=destinationParent,格納先親
        var destinationParent = Path.GetDirectoryName(destinationDirectory);
        if (!string.IsNullOrWhiteSpace(destinationParent))
        {
            Directory.CreateDirectory(destinationParent);
        }

        // Directory.Move refuses a destination that appears after the caller's preflight.
        await Task.Run(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            Directory.Move(sourceDirectory, destinationDirectory);
        }, cancellationToken).ConfigureAwait(false);
    }

    //CSJ,method=GetDirectorySizeBytes,ディレクトリサイズバイト列を取得する
    public long GetDirectorySizeBytes(string directoryPath)
    {
        if (!Directory.Exists(directoryPath))
        {
            return 0;
        }

        //CSJ,variable=total,合計
        long total = 0;
        //CSJ,variable=file,ファイル
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

    //CSJ,method=CountChildDirectories,子ディレクトリ一覧を数える
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

    //CSJ,method=DeleteFileIfExists,存在する場合にファイルを削除する
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
