using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using YMMProjectManager.Application;
using YMMProjectManager.Domain;
using YukkuriMovieMaker.Commons;

namespace YMMProjectManager.Infrastructure;

public sealed class JsonProjectRepository : IProjectRepository
{
    private const string PluginDirectoryName = "YMMProjectManager";
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private static readonly JsonSerializerOptions StrictRecoveryOptions = new() { UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow };
    private static readonly SemaphoreSlim SaveGate = new(1, 1);
    private readonly string jsonPath;
    private readonly Func<Stream, byte[], CancellationToken, Task> writeTemporaryFile;
    private readonly Action? beforeReplace;
    private readonly FileLogger logger;

    public JsonProjectRepository(FileLogger logger) : this(logger, ResolvePath()) { }

    // Internal hooks make failures at the file boundary deterministic in isolated tests.
    internal JsonProjectRepository(FileLogger logger, string path,
        Func<Stream, byte[], CancellationToken, Task>? writeTemporaryFile = null, Action? beforeReplace = null)
    {
        this.logger = logger;
        jsonPath = path;
        this.writeTemporaryFile = writeTemporaryFile ?? ((stream, bytes, token) => stream.WriteAsync(bytes, token).AsTask());
        this.beforeReplace = beforeReplace;
    }

    internal string RecoveryDirectory => Path.GetFullPath(jsonPath) + ".recovery";

    public async Task<ProjectStore> LoadAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            var content = await File.ReadAllBytesAsync(jsonPath, cancellationToken).ConfigureAwait(false);
            return DeserializeStore(content);
        }
        catch (FileNotFoundException) { return new ProjectStore { Revision = new ProjectListRevision(null), IsRecoverySafe = true }; }
        catch (DirectoryNotFoundException) { return new ProjectStore { Revision = new ProjectListRevision(null), IsRecoverySafe = true }; }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) { logger.Error("Failed to load projects.json", ex); throw; }
    }

    internal static ProjectStore DeserializeStore(byte[] content)
    {
        var jsonContent = StripUtf8Bom(content);
        var dto = JsonSerializer.Deserialize<ProjectListFileDto>(jsonContent.Span, JsonOptions);
        if (dto is null) throw new InvalidDataException("Project list is null.");
        var store = new ProjectStore { Revision = new ProjectListRevision(Convert.ToHexString(SHA256.HashData(content))), IsRecoverySafe = IsSafeForRecovery(content) };
        foreach (var f in dto.Folders ?? [])
        {
            if (f.Id is null || string.IsNullOrWhiteSpace(f.Name)) continue;
            store.Folders.Add(new ProjectFolder { Id = f.Id.Value, Name = f.Name!, DisplayOrder = f.DisplayOrder ?? 0, CreatedAt = f.CreatedAt ?? DateTimeOffset.Now, UpdatedAt = f.UpdatedAt ?? DateTimeOffset.Now });
        }
        var projectIds = new HashSet<Guid>();
        foreach (var x in dto.Projects ?? [])
        {
            if (string.IsNullOrWhiteSpace(x.FullPath))
            {
                if (x.ProjectId is not null) throw new InvalidDataException("Identified project has no path.");
                continue;
            }
            if (x.ProjectId is Guid id && (id == Guid.Empty || !projectIds.Add(id))) throw new InvalidDataException("Project list contains an empty or duplicate project identity.");
            var p = new ProjectEntry { FullPath = x.FullPath!, ProjectId = x.ProjectId, MetadataAssociation = x.MetadataAssociation, DisplayName = string.IsNullOrWhiteSpace(x.DisplayName) ? null : x.DisplayName, Pinned = x.Pinned ?? false, LastAccess = x.LastAccess, FolderId = x.FolderId };
            foreach (var l in x.LinkedYmmpFiles ?? [])
            {
                if (string.IsNullOrWhiteSpace(l.FilePath)) continue;
                p.LinkedYmmpFiles.Add(new LinkedYmmpFile { Id = l.Id ?? Guid.NewGuid(), FilePath = l.FilePath!, DisplayName = l.DisplayName, Role = Enum.TryParse<YmmpRole>(l.Role, true, out var role) ? role : YmmpRole.Unknown, Memo = l.Memo, RegisteredAt = l.RegisteredAt ?? DateTimeOffset.Now, UpdatedAt = l.UpdatedAt ?? DateTimeOffset.Now, LastCheckedAt = l.LastCheckedAt, Exists = l.Exists ?? File.Exists(l.FilePath!) });
            }
            if (p.LinkedYmmpFiles.Count == 0 && p.FullPath.EndsWith(".ymmp", StringComparison.OrdinalIgnoreCase))
            {
                p.LinkedYmmpFiles.Add(new LinkedYmmpFile { FilePath = p.FullPath, DisplayName = p.DisplayName, Role = YmmpRole.Main, Exists = File.Exists(p.FullPath), LastCheckedAt = DateTimeOffset.Now });
            }
            store.Projects.Add(p);
        }
        return store;
    }

    private static bool IsSafeForRecovery(byte[] content)
    {
        try
        {
            var jsonContent = StripUtf8Bom(content);
            var dto = JsonSerializer.Deserialize<ProjectListFileDto>(jsonContent.Span, StrictRecoveryOptions);
            if (dto?.Projects is null || dto.Folders is null) return false;
            if (dto.Folders.Any(f => f is null || f.Id is null || string.IsNullOrWhiteSpace(f.Name))) return false;
            foreach (var project in dto.Projects)
            {
                if (project is null || string.IsNullOrWhiteSpace(project.FullPath) || project.LinkedYmmpFiles is null ||
                    (project.DisplayName is not null && string.IsNullOrWhiteSpace(project.DisplayName))) return false;
                if (project.MetadataAssociation is { } metadata && (!ValidMetadata(metadata) || project.ProjectId is null)) return false;
                if (project.LinkedYmmpFiles.Any(l => l is null || string.IsNullOrWhiteSpace(l.FilePath) ||
                    (l.Role is not null && !Enum.GetNames<YmmpRole>().Any(name => string.Equals(name, l.Role, StringComparison.OrdinalIgnoreCase))))) return false;
            }
            using var document = JsonDocument.Parse(jsonContent);
            return !HasDuplicateProperties(document.RootElement);
        }
        catch (JsonException) { return false; }
    }

    private static bool ValidMetadata(ProjectMetadataAssociation value) =>
        !string.IsNullOrWhiteSpace(value.Directory) && Path.IsPathFullyQualified(value.Directory) &&
        value.RootRevisionId != Guid.Empty && value.SelectedRevisionId != Guid.Empty &&
        value.ObservedHeadRevisionId != Guid.Empty && IsHash(value.RootSha256) &&
        IsHash(value.SelectedSha256) && IsHash(value.ObservedHeadSha256);

    private static bool IsHash(string? value) => value is { Length: 64 } && value.All(Uri.IsHexDigit);

    private static ReadOnlyMemory<byte> StripUtf8Bom(byte[] content) =>
        content.Length >= 3 && content[0] == 0xEF && content[1] == 0xBB && content[2] == 0xBF
            ? content.AsMemory(3) : content.AsMemory();

    private static bool HasDuplicateProperties(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var property in element.EnumerateObject())
                if (!names.Add(property.Name) || HasDuplicateProperties(property.Value)) return true;
        }
        else if (element.ValueKind == JsonValueKind.Array)
            foreach (var item in element.EnumerateArray()) if (HasDuplicateProperties(item)) return true;
        return false;
    }

    public async Task SaveAsync(ProjectStore store, CancellationToken cancellationToken = default)
    {
        string? tempPath = null;
        FileStream? processLock = null;
        var stage = "waiting";
        await SaveGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            stage = "serialize";
            var bytes = SerializeStore(store);
            cancellationToken.ThrowIfCancellationRequested();
            var directory = Path.GetDirectoryName(jsonPath)!;
            Directory.CreateDirectory(directory);
            stage = "inter-process lock";
            try { processLock = new FileStream(jsonPath + ".lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
            catch (IOException ex) when ((ex.HResult & 0xffff) == 32) { throw new ProjectListBusyException(ex); }
            var candidatePath = Path.Combine(directory, $"{Path.GetFileName(jsonPath)}.tmp-{Guid.NewGuid():N}");
            stage = "write temporary file";
            await using (var stream = new FileStream(candidatePath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.Asynchronous))
            {
                tempPath = candidatePath;
                await writeTemporaryFile(stream, bytes, cancellationToken).ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
                stream.Flush(flushToDisk: true);
            }
            stage = "replace";
            beforeReplace?.Invoke();
            cancellationToken.ThrowIfCancellationRequested();
            if (store.Revision is null) throw new InvalidDataException("Project list must be loaded before saving.");
            string? currentHash;
            try { currentHash = Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(jsonPath, cancellationToken).ConfigureAwait(false))); }
            catch (FileNotFoundException) { currentHash = null; }
            catch (DirectoryNotFoundException) { currentHash = null; }
            if (!string.Equals(currentHash, store.Revision.Sha256, StringComparison.Ordinal))
                throw new ProjectListConflictException();
            cancellationToken.ThrowIfCancellationRequested();
            if (currentHash is not null)
                File.Replace(tempPath, jsonPath, null);
            else
                File.Move(tempPath, jsonPath);
            tempPath = null;
            store.Revision = new ProjectListRevision(Convert.ToHexString(SHA256.HashData(bytes)));
            store.IsRecoverySafe = IsSafeForRecovery(bytes);
        }
        catch (OperationCanceledException)
        {
            logger.Info($"Project list save canceled. stage={stage}");
            throw;
        }
        catch (Exception ex)
        {
            logger.Error(ex, $"Project list save failed. stage={stage}");
            throw;
        }
        finally
        {
            if (tempPath is not null)
            {
                try { File.Delete(tempPath); }
                catch (Exception ex) { logger.Error(ex, "Failed to remove project list temporary file."); }
            }
            processLock?.Dispose();
            SaveGate.Release();
        }
    }

    internal static byte[] SerializeStore(ProjectStore store)
    {
        if (!store.IsRecoverySafe) throw new InvalidDataException("Project list contains data that cannot be safely preserved.");
        var projectIds = new HashSet<Guid>();
        foreach (var project in store.Projects)
        {
            if (project.ProjectId is not null && string.IsNullOrWhiteSpace(project.FullPath)) throw new InvalidDataException("Identified project has no path.");
            if (project.ProjectId is Guid id && (id == Guid.Empty || !projectIds.Add(id))) throw new InvalidDataException("Project list contains an empty or duplicate project identity.");
        }
        var dto = new ProjectListFileDto
        {
            Folders = store.Folders.Select(x => new ProjectFolderDto { Id = x.Id, Name = x.Name, DisplayOrder = x.DisplayOrder, CreatedAt = x.CreatedAt, UpdatedAt = x.UpdatedAt }).ToList(),
            Projects = store.Projects.Select(x => new ProjectRecordDto
            {
                FullPath = x.FullPath, ProjectId = x.ProjectId, MetadataAssociation = x.MetadataAssociation, DisplayName = string.IsNullOrWhiteSpace(x.DisplayName) ? null : x.DisplayName, Pinned = x.Pinned ? true : null, LastAccess = x.LastAccess, FolderId = x.FolderId,
                LinkedYmmpFiles = x.LinkedYmmpFiles.Select(l => new LinkedYmmpFileDto { Id = l.Id, FilePath = l.FilePath, DisplayName = l.DisplayName, Role = l.Role.ToString(), Memo = l.Memo, RegisteredAt = l.RegisteredAt, UpdatedAt = l.UpdatedAt, LastCheckedAt = l.LastCheckedAt, Exists = l.Exists }).ToList(),
            }).ToList(),
        };
        var bytes = JsonSerializer.SerializeToUtf8Bytes(dto, JsonOptions);
        if (!IsSafeForRecovery(bytes)) throw new InvalidDataException("Project list contains entries that cannot be safely reloaded.");
        return bytes;
    }

    // Recovery export never replaces an existing file, including a file created after the dialog.
    internal async Task SaveNewCopyAsync(ProjectStore store, CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        var bytes = SerializeStore(store);
        var directory = Path.GetDirectoryName(Path.GetFullPath(jsonPath))!;
        var candidate = Path.Combine(directory, $"{Path.GetFileName(jsonPath)}.tmp-{Guid.NewGuid():N}");
        var owned = false;
        try
        {
            await using (var stream = new FileStream(candidate, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.Asynchronous))
            {
                owned = true;
                await stream.WriteAsync(bytes, token).ConfigureAwait(false);
                await stream.FlushAsync(token).ConfigureAwait(false);
                stream.Flush(flushToDisk: true);
            }
            token.ThrowIfCancellationRequested();
            File.Move(candidate, jsonPath, overwrite: false);
            owned = false;
        }
        finally { if (owned) { try { File.Delete(candidate); } catch (IOException) { } catch (UnauthorizedAccessException) { } } }
    }

    private static string ResolvePath(){ var runtimeYmmDir = Environment.GetEnvironmentVariable("YMM4DirPath"); if (!string.IsNullOrWhiteSpace(runtimeYmmDir)) return Path.Combine(runtimeYmmDir, "user", "plugin", PluginDirectoryName, "data", "projects.json"); var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData); return Path.Combine(appData, "YMMProjectManager", "plugin", PluginDirectoryName, "data", "projects.json"); }

    private sealed class ProjectListFileDto { public List<ProjectRecordDto> Projects { get; set; } = []; public List<ProjectFolderDto> Folders { get; set; } = []; }
    private sealed class ProjectRecordDto { public string? FullPath { get; set; } [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)] public Guid? ProjectId { get; set; } [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public ProjectMetadataAssociation? MetadataAssociation { get; set; } public string? DisplayName { get; set; } public bool? Pinned { get; set; } public DateTimeOffset? LastAccess { get; set; } public Guid? FolderId { get; set; } public List<LinkedYmmpFileDto> LinkedYmmpFiles { get; set; } = []; }
    private sealed class LinkedYmmpFileDto { public Guid? Id { get; set; } public string? FilePath { get; set; } public string? DisplayName { get; set; } public string? Role { get; set; } public string? Memo { get; set; } public DateTimeOffset? RegisteredAt { get; set; } public DateTimeOffset? UpdatedAt { get; set; } public DateTimeOffset? LastCheckedAt { get; set; } public bool? Exists { get; set; } }
    private sealed class ProjectFolderDto { public Guid? Id { get; set; } public string? Name { get; set; } public int? DisplayOrder { get; set; } public DateTimeOffset? CreatedAt { get; set; } public DateTimeOffset? UpdatedAt { get; set; } }
}
