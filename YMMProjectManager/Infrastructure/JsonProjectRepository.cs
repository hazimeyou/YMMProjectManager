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
    private static readonly HashSet<string> ReservedRootNames = ReservedNames<ProjectListFileDto>();
    private static readonly HashSet<string> ReservedProjectNames = ReservedNames<ProjectRecordDto>();
    private static readonly HashSet<string> ReservedLinkNames = ReservedNames<LinkedYmmpFileDto>();
    private static readonly HashSet<string> ReservedFolderNames = ReservedNames<ProjectFolderDto>();
    private static readonly HashSet<string> ReservedMetadataNames = ReservedNames<MetadataAssociationDto>();
    private static readonly HashSet<string> ReservedIdentityNames = ReservedNames<FileIdentityDto>();
    private static readonly SemaphoreSlim SaveGate = new(1, 1);
    private readonly string jsonPath;
    private readonly Func<Stream, byte[], CancellationToken, Task> writeTemporaryFile;
    private readonly Action? beforeReplace;
    private readonly Action? afterReplace;
    private readonly Action? beforeCandidate;
    private readonly Action? beforeReceipt;
    private readonly Action? afterReceipt;
    private readonly Action? afterDraftClaim;
    private readonly Action? duringReceipt;
    private readonly Action? afterRevisionCheck;
    private readonly FileLogger logger;

    public JsonProjectRepository(FileLogger logger) : this(logger, ResolvePath()) { }

    // Internal hooks make failures at the file boundary deterministic in isolated tests.
    internal JsonProjectRepository(FileLogger logger, string path,
        Func<Stream, byte[], CancellationToken, Task>? writeTemporaryFile = null, Action? beforeReplace = null,
        Action? afterReplace = null, Action? beforeCandidate = null, Action? beforeReceipt = null,
        Action? afterReceipt = null, Action? afterDraftClaim = null, Action? duringReceipt = null, Action? afterRevisionCheck = null)
    {
        this.logger = logger;
        jsonPath = Path.GetFullPath(path);
        this.writeTemporaryFile = writeTemporaryFile ?? ((stream, bytes, token) => stream.WriteAsync(bytes, token).AsTask());
        this.beforeReplace = beforeReplace;
        this.afterReplace = afterReplace;
        this.beforeCandidate = beforeCandidate;
        this.beforeReceipt = beforeReceipt;
        this.afterReceipt = afterReceipt;
        this.afterDraftClaim = afterDraftClaim;
        this.duringReceipt = duringReceipt;
        this.afterRevisionCheck = afterRevisionCheck;
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
        var safe = IsSafeForRecovery(content);
        var nested = safe ? CaptureNestedFields(dto) : null;
        var revision = new ProjectListRevision(Convert.ToHexString(SHA256.HashData(content)), dto.SaveOperationId)
        {
            PreservedRootFieldsJson = dto.ExtensionData is { Count: > 0 } fields ? JsonSerializer.Serialize(fields) : null,
            PreservedNestedFieldsJson = nested is null ? null : JsonSerializer.Serialize(nested),
            PreservedFieldAnchors = nested is null ? null : string.Join(';', EnumeratePreservedAnchors(nested).Order(StringComparer.Ordinal)),
        };
        var store = new ProjectStore { Revision = revision, IsRecoverySafe = safe };
        foreach (var f in dto.Folders ?? [])
        {
            if (f.Id is null || string.IsNullOrWhiteSpace(f.Name)) continue;
            var created = f.CreatedAt ?? DateTimeOffset.UnixEpoch;
            store.Folders.Add(new ProjectFolder { Id = f.Id.Value, Name = f.Name!, DisplayOrder = f.DisplayOrder ?? 0, CreatedAt = created, UpdatedAt = f.UpdatedAt ?? created });
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
            var p = new ProjectEntry { FullPath = x.FullPath!, ProjectId = x.ProjectId, MetadataAssociation = x.MetadataAssociation?.ToDomain(), FileIdentity = x.FileIdentity?.ToDomain(), DisplayName = string.IsNullOrWhiteSpace(x.DisplayName) ? null : x.DisplayName, Pinned = x.Pinned ?? false, LastAccess = x.LastAccess, FolderId = x.FolderId };
            var legacyOccurrences = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            foreach (var l in x.LinkedYmmpFiles ?? [])
            {
                if (string.IsNullOrWhiteSpace(l.FilePath)) continue;
                var registered = l.RegisteredAt ?? DateTimeOffset.UnixEpoch;
                var occurrence = legacyOccurrences.GetValueOrDefault(l.FilePath);
                legacyOccurrences[l.FilePath] = occurrence + 1;
                p.LinkedYmmpFiles.Add(new LinkedYmmpFile
                {
                    Id = l.Id ?? LegacyLinkId(p.FullPath, l.FilePath, occurrence),
                    FilePath = l.FilePath, DisplayName = l.DisplayName,
                    Role = Enum.TryParse<YmmpRole>(l.Role, true, out var role) ? role : YmmpRole.Unknown,
                    Memo = l.Memo, RegisteredAt = registered, UpdatedAt = l.UpdatedAt ?? registered,
                    LastCheckedAt = l.LastCheckedAt, Exists = l.Exists ?? false,
                });
            }
            if (p.LinkedYmmpFiles.Count == 0 && p.FullPath.EndsWith(".ymmp", StringComparison.OrdinalIgnoreCase))
            {
                p.LinkedYmmpFiles.Add(new LinkedYmmpFile
                {
                    Id = LegacyLinkId(p.FullPath, p.FullPath, 0), FilePath = p.FullPath, DisplayName = p.DisplayName, Role = YmmpRole.Main,
                    RegisteredAt = DateTimeOffset.UnixEpoch, UpdatedAt = DateTimeOffset.UnixEpoch,
                });
            }
            // Existing metadata can be inspected even if duplicate child identity makes editing unsafe.
            if (p.LinkedYmmpFiles.Any(link => link.Id == Guid.Empty) ||
                p.LinkedYmmpFiles.Select(link => link.Id).Distinct().Count() != p.LinkedYmmpFiles.Count)
                store.IsRecoverySafe = false;
            store.Projects.Add(p);
        }
        return store;
    }

    private static Guid LegacyLinkId(string projectPath, string linkPath, int occurrence)
    {
        var anchor = JsonSerializer.SerializeToUtf8Bytes(new { Type = "project-list-legacy-link", Project = projectPath.ToUpperInvariant(),
            Link = linkPath.ToUpperInvariant(), Occurrence = occurrence });
        return new Guid(SHA256.HashData(anchor).AsSpan(0, 16));
    }

    private static bool IsSafeForRecovery(byte[] content)
    {
        try
        {
            var jsonContent = StripUtf8Bom(content);
            var dto = JsonSerializer.Deserialize<ProjectListFileDto>(jsonContent.Span, StrictRecoveryOptions);
            if (dto?.Projects is null || dto.Folders is null || dto.SaveOperationId == Guid.Empty) return false;
            using var document = JsonDocument.Parse(jsonContent);
            if (dto.ExtensionData is { Count: > 0 } unknown &&
                (!document.RootElement.TryGetProperty("Projects", out _) || unknown.Keys.Any(ReservedRootNames.Contains))) return false;
            if (dto.Folders.Any(f => f is null || f.Id is null || f.Id == Guid.Empty || string.IsNullOrWhiteSpace(f.Name) ||
                !CanPreserve(f.ExtensionData, ReservedFolderNames, true))) return false;
            if (dto.Folders.Select(f => f.Id).Distinct().Count() != dto.Folders.Count) return false;
            var projectIds = new HashSet<Guid>();
            var physicalIds = new HashSet<(ulong Volume, Guid Id)>();
            foreach (var project in dto.Projects)
            {
                if (project is null || string.IsNullOrWhiteSpace(project.FullPath) || project.LinkedYmmpFiles is null ||
                    (project.DisplayName is not null && string.IsNullOrWhiteSpace(project.DisplayName))) return false;
                if (project.ProjectId is { } id && (id == Guid.Empty || !projectIds.Add(id))) return false;
                if (!CanPreserve(project.ExtensionData, ReservedProjectNames, project.ProjectId.HasValue)) return false;
                if (project.MetadataAssociation is { } metadata && (!ValidMetadata(metadata.ToDomain()) || project.ProjectId is null ||
                    !CanPreserve(metadata.ExtensionData, ReservedMetadataNames, true))) return false;
                if (project.FileIdentity is { } identity && (identity.FileId == Guid.Empty || !IsHash(identity.Sha256) ||
                    project.ProjectId is null || !physicalIds.Add((identity.Volume, identity.FileId)) ||
                    !CanPreserve(identity.ExtensionData, ReservedIdentityNames, true))) return false;
                if (project.LinkedYmmpFiles.Any(l => l is null || l.Id == Guid.Empty || string.IsNullOrWhiteSpace(l.FilePath) ||
                    !CanPreserve(l.ExtensionData, ReservedLinkNames, l.Id.HasValue && project.ProjectId.HasValue) ||
                    (l.Role is not null && !Enum.GetNames<YmmpRole>().Any(name => string.Equals(name, l.Role, StringComparison.OrdinalIgnoreCase))))) return false;
                var ids = project.LinkedYmmpFiles.Where(l => l.Id.HasValue).Select(l => l.Id!.Value).ToArray();
                if (ids.Distinct().Count() != ids.Length) return false;
            }
            return !HasDuplicateProperties(document.RootElement);
        }
        catch (JsonException) { return false; }
    }

    private static HashSet<string> ReservedNames<T>() => typeof(T).GetProperties()
        .Where(property => !property.IsDefined(typeof(JsonExtensionDataAttribute), inherit: false))
        .Select(property => property.Name)
        .Concat(["Version", "SchemaVersion", "FormatVersion", "$schema"])
        .ToHashSet(StringComparer.OrdinalIgnoreCase);

    private static bool CanPreserve(Dictionary<string, JsonElement>? fields, HashSet<string> reserved, bool hasAnchor) =>
        fields is not { Count: > 0 } || (hasAnchor && !fields.Keys.Any(reserved.Contains));

    private sealed record ProjectFields(Dictionary<string, JsonElement>? Fields, Dictionary<Guid, Dictionary<string, JsonElement>> Links,
        MetadataAssociationDto? Metadata, FileIdentityDto? Identity);
    private sealed record NestedFields(Dictionary<Guid, ProjectFields> Projects, Dictionary<Guid, Dictionary<string, JsonElement>> Folders);

    private static NestedFields? CaptureNestedFields(ProjectListFileDto dto)
    {
        var projects = new Dictionary<Guid, ProjectFields>();
        foreach (var project in dto.Projects.Where(item => item.ProjectId.HasValue))
        {
            var links = project.LinkedYmmpFiles.Where(link => link.ExtensionData is { Count: > 0 })
                .ToDictionary(link => link.Id!.Value, link => link.ExtensionData!);
            var metadata = project.MetadataAssociation is { ExtensionData.Count: > 0 } ? project.MetadataAssociation : null;
            var identity = project.FileIdentity is { ExtensionData.Count: > 0 } ? project.FileIdentity : null;
            if (project.ExtensionData is { Count: > 0 } || links.Count != 0 || metadata is not null || identity is not null)
                projects.Add(project.ProjectId!.Value, new ProjectFields(project.ExtensionData, links, metadata, identity));
        }
        var folders = dto.Folders.Where(folder => folder.ExtensionData is { Count: > 0 })
            .ToDictionary(folder => folder.Id!.Value, folder => folder.ExtensionData!);
        return projects.Count == 0 && folders.Count == 0 ? null : new NestedFields(projects, folders);
    }

    private static IEnumerable<string> EnumeratePreservedAnchors(NestedFields nested)
    {
        foreach (var (id, fields) in nested.Projects)
        {
            if (fields.Fields is { Count: > 0 }) yield return ProjectListFieldAnchors.Project(id);
            if (fields.Metadata is { } metadata) yield return ProjectListFieldAnchors.Metadata(id, metadata.ToDomain());
            if (fields.Identity is { } identity) yield return ProjectListFieldAnchors.FileIdentity(id, identity.ToDomain());
            foreach (var link in fields.Links.Keys) yield return ProjectListFieldAnchors.Link(id, link);
        }
        foreach (var id in nested.Folders.Keys) yield return ProjectListFieldAnchors.Folder(id);
    }

    private static void RestoreNestedFields(ProjectListFileDto dto, string preserved)
    {
        var nested = JsonSerializer.Deserialize<NestedFields>(preserved) ?? throw new InvalidDataException("Preserved project list fields are invalid.");
        foreach (var (id, fields) in nested.Projects)
        {
            var project = dto.Projects.SingleOrDefault(item => item.ProjectId == id)
                ?? throw new InvalidDataException("This change would remove a project with unknown fields.");
            project.ExtensionData = fields.Fields;
            if (fields.Metadata is { } metadata)
            {
                if (project.MetadataAssociation?.ToDomain() != metadata.ToDomain())
                    throw new InvalidDataException("This change cannot preserve unknown fields in the metadata association.");
                project.MetadataAssociation = metadata;
            }
            if (fields.Identity is { } identity)
            {
                if (project.FileIdentity?.ToDomain() != identity.ToDomain())
                    throw new InvalidDataException("This change cannot preserve unknown fields in the file identity.");
                project.FileIdentity = identity;
            }
            foreach (var (linkId, linkFields) in fields.Links)
            {
                var link = project.LinkedYmmpFiles.SingleOrDefault(item => item.Id == linkId)
                    ?? throw new InvalidDataException("This change would remove a project link with unknown fields.");
                link.ExtensionData = linkFields;
            }
        }
        foreach (var (id, fields) in nested.Folders)
        {
            var folder = dto.Folders.SingleOrDefault(item => item.Id == id)
                ?? throw new InvalidDataException("This change would remove a folder with unknown fields.");
            folder.ExtensionData = fields;
        }
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

    public Task SaveAsync(ProjectStore store, CancellationToken cancellationToken = default) =>
        SaveCoreAsync(store, null, cancellationToken);

    internal async Task<string> WritePendingDraftAsync(ProjectStore baseline, ProjectStore local,
        Dictionary<string, ProjectListDrafts.Evidence> evidence, string? predecessor, Action? beforePublication = null,
        Action? afterPublication = null)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(jsonPath)!);
        using var processLock = AcquireProcessLock();
        return await new ProjectListDrafts(RecoveryDirectory)
            .WriteAsync(baseline, local, evidence, predecessor, beforePublication, afterPublication).ConfigureAwait(false);
    }

    internal async Task RetireReadyDraftAsync(string readyDraft, CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        var drafts = new ProjectListDrafts(RecoveryDirectory);
        using var processLock = AcquireProcessLock();
        await drafts.ReadAsync(readyDraft).ConfigureAwait(false);
        token.ThrowIfCancellationRequested();
        drafts.Retire(readyDraft);
    }

    internal Task SaveReadyDraftAsync(ProjectStore store, string readyDraft, CancellationToken token = default) =>
        SaveCoreAsync(store, readyDraft, token);

    internal string GetReadyDraftPath(string claimedDraft) => new ProjectListDrafts(RecoveryDirectory).GetReadyPath(claimedDraft);

    internal async Task<ProjectListPublications.Outcome> ReconcileDraftAsync(string claimedDraft, CancellationToken token = default)
    {
        var drafts = new ProjectListDrafts(RecoveryDirectory);
        using var processLock = AcquireProcessLock();
        var (id, operation) = drafts.GetClaimedIdentity(claimedDraft);
        await drafts.ReadAsync(claimedDraft, claimed: true).ConfigureAwait(false);
        var store = await LoadAsync(token).ConfigureAwait(false);
        var revision = store.Revision!;
        var outcome = await new ProjectListPublications(RecoveryDirectory)
            .ReconcileAsync(id, operation, revision.Sha256, revision.OperationId, HasUnconsumedCandidate, token).ConfigureAwait(false);
        if (outcome == ProjectListPublications.Outcome.Unpublished && store.IsRecoverySafe)
            drafts.ReleaseUnpublishedClaim(claimedDraft);
        else if (outcome == ProjectListPublications.Outcome.Committed)
            drafts.Retire(claimedDraft, claimed: true);
        else
            outcome = ProjectListPublications.Outcome.Unknown;
        logger.Info($"Project list publication reconciled. draft={id:N} outcome={outcome}");
        return outcome;
    }

    private FileStream AcquireProcessLock()
    {
        try { return new FileStream(jsonPath + ".lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
        catch (IOException ex) when ((ex.HResult & 0xffff) == 32) { throw new ProjectListBusyException(ex); }
    }

    private string CandidatePath(Guid operationId) => $"{jsonPath}.tmp-{operationId:N}";

    private bool HasUnconsumedCandidate(Guid operationId)
    {
        try { return (File.GetAttributes(CandidatePath(operationId)) & (FileAttributes.ReparsePoint | FileAttributes.Directory)) == 0; }
        catch (FileNotFoundException) { return false; }
        catch (DirectoryNotFoundException) { return false; }
    }

    private FileStream? OpenCurrentListForPublication()
    {
        // Delete sharing permits our atomic replacement. The lease blocks in-place writes, but another process
        // with delete sharing can still replace the path. The process lock coordinates product writers.
        try { return new FileStream(jsonPath, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete, 81920, useAsync: true); }
        catch (FileNotFoundException) { return null; }
        catch (DirectoryNotFoundException) { return null; }
    }

    private async Task SaveCoreAsync(ProjectStore store, string? readyDraft, CancellationToken cancellationToken)
    {
        string? tempPath = null;
        FileStream? processLock = null;
        var drafts = new ProjectListDrafts(RecoveryDirectory);
        string? claim = null;
        var publicationAttempted = false;
        var committed = false;
        var stage = "waiting";
        await SaveGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            stage = "serialize";
            var baseline = store.Revision ?? throw new InvalidDataException("Project list must be loaded before saving.");
            var operationId = Guid.NewGuid();
            var bytes = SerializeStore(store, operationId);
            var expectedResult = Convert.ToHexString(SHA256.HashData(bytes));
            cancellationToken.ThrowIfCancellationRequested();
            var directory = Path.GetDirectoryName(jsonPath)!;
            Directory.CreateDirectory(directory);
            stage = "inter-process lock";
            processLock = AcquireProcessLock();
            stage = "write publication receipt";
            Guid? draftId = null;
            if (readyDraft is not null)
            {
                draftId = drafts.GetReadyId(readyDraft);
                await drafts.ReadAsync(readyDraft).ConfigureAwait(false);
            }
            var publications = new ProjectListPublications(RecoveryDirectory);
            var receipt = new ProjectListPublications.Receipt(2, operationId, draftId, baseline.Sha256, expectedResult,
                PublicationStarted: false, ExpectedOperation: baseline.OperationId);
            beforeReceipt?.Invoke();
            await publications.WriteAsync(receipt, cancellationToken, duringReceipt).ConfigureAwait(false);
            afterReceipt?.Invoke();
            if (readyDraft is not null)
            {
                stage = "claim prepared draft";
                cancellationToken.ThrowIfCancellationRequested();
                claim = drafts.Claim(readyDraft, operationId);
                afterDraftClaim?.Invoke();
            }
            beforeCandidate?.Invoke();
            var candidatePath = CandidatePath(operationId);
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
            await publications.WriteAsync(receipt with { PublicationStarted = true }, cancellationToken).ConfigureAwait(false);
            await using var current = OpenCurrentListForPublication();
            var currentHash = current is null ? null :
                Convert.ToHexString(await SHA256.HashDataAsync(current, cancellationToken).ConfigureAwait(false));
            if (!string.Equals(currentHash, baseline.Sha256, StringComparison.Ordinal))
                throw new ProjectListConflictException();
            afterRevisionCheck?.Invoke();
            cancellationToken.ThrowIfCancellationRequested();
            publicationAttempted = true;
            if (currentHash is not null)
                File.Replace(tempPath, jsonPath, null);
            else
                File.Move(tempPath, jsonPath);
            tempPath = null;
            committed = true;
            store.Revision = baseline with { Sha256 = expectedResult, OperationId = operationId };
            store.IsRecoverySafe = true;
            stage = "after publication";
            afterReplace?.Invoke();
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
            if (claim is not null)
            {
                try
                {
                    if (committed) drafts.Retire(claim, claimed: true);
                    else if (!publicationAttempted) drafts.ReleaseUnpublishedClaim(claim);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    logger.Error(ex, "A project list draft needs reconciliation.");
                }
            }
            if (tempPath is not null)
            {
                try { File.Delete(tempPath); }
                catch (Exception ex) { logger.Error(ex, "Failed to remove project list temporary file."); }
            }
            processLock?.Dispose();
            SaveGate.Release();
        }
    }

    internal static byte[] SerializeStore(ProjectStore store, Guid? saveOperationId = null)
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
            SaveOperationId = saveOperationId,
            ExtensionData = store.Revision?.PreservedRootFieldsJson is { } extension
                ? JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(extension)
                : null,
            Folders = store.Folders.Select(x => new ProjectFolderDto { Id = x.Id, Name = x.Name, DisplayOrder = x.DisplayOrder, CreatedAt = x.CreatedAt, UpdatedAt = x.UpdatedAt }).ToList(),
            Projects = store.Projects.Select(x => new ProjectRecordDto
            {
                FullPath = x.FullPath, ProjectId = x.ProjectId, MetadataAssociation = MetadataAssociationDto.FromDomain(x.MetadataAssociation), FileIdentity = FileIdentityDto.FromDomain(x.FileIdentity), DisplayName = string.IsNullOrWhiteSpace(x.DisplayName) ? null : x.DisplayName, Pinned = x.Pinned ? true : null, LastAccess = x.LastAccess, FolderId = x.FolderId,
                LinkedYmmpFiles = x.LinkedYmmpFiles.Select(l => new LinkedYmmpFileDto { Id = l.Id, FilePath = l.FilePath, DisplayName = l.DisplayName, Role = l.Role.ToString(), Memo = l.Memo, RegisteredAt = l.RegisteredAt, UpdatedAt = l.UpdatedAt, LastCheckedAt = l.LastCheckedAt, Exists = l.Exists }).ToList(),
            }).ToList(),
        };
        if (store.Revision?.PreservedNestedFieldsJson is { } nested) RestoreNestedFields(dto, nested);
        var bytes = JsonSerializer.SerializeToUtf8Bytes(dto, JsonOptions);
        if (!IsSafeForRecovery(bytes)) throw new InvalidDataException("Project list contains entries that cannot be safely reloaded.");
        return bytes;
    }

    // Recovery export never replaces an existing file, including a file created after the dialog.
    internal Task SaveNewCopyAsync(ProjectStore store, CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        return WriteNewCopyAsync(jsonPath, SerializeStore(store), token);
    }

    internal async Task ExportOriginalAsync(string destination, CancellationToken token = default)
    {
        var bytes = await ReadOriginalAsync(token).ConfigureAwait(false);
        await WriteNewCopyAsync(Path.GetFullPath(destination), bytes, token).ConfigureAwait(false);
    }

    internal Task<byte[]> ReadOriginalAsync(CancellationToken token = default) =>
        File.ReadAllBytesAsync(jsonPath, token);

    internal static async Task WriteNewCopyAsync(string destination, byte[] bytes, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        destination = Path.GetFullPath(destination);
        var directory = Path.GetDirectoryName(destination)!;
        var candidate = Path.Combine(directory, $"{Path.GetFileName(destination)}.tmp-{Guid.NewGuid():N}");
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
            File.Move(candidate, destination, overwrite: false);
            owned = false;
        }
        finally { if (owned) { try { File.Delete(candidate); } catch (IOException) { } catch (UnauthorizedAccessException) { } } }
    }

    private static string ResolvePath(){ var runtimeYmmDir = Environment.GetEnvironmentVariable("YMM4DirPath"); if (!string.IsNullOrWhiteSpace(runtimeYmmDir)) return Path.Combine(runtimeYmmDir, "user", "plugin", PluginDirectoryName, "data", "projects.json"); var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData); return Path.Combine(appData, "YMMProjectManager", "plugin", PluginDirectoryName, "data", "projects.json"); }

    private sealed class ProjectListFileDto
    {
        public List<ProjectRecordDto> Projects { get; set; } = [];
        public List<ProjectFolderDto> Folders { get; set; } = [];
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public Guid? SaveOperationId { get; set; }
        [JsonExtensionData]
        public Dictionary<string, JsonElement>? ExtensionData { get; set; }
    }
    private sealed class ProjectRecordDto { public string? FullPath { get; set; } [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public Guid? ProjectId { get; set; } [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public MetadataAssociationDto? MetadataAssociation { get; set; } [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public FileIdentityDto? FileIdentity { get; set; } public string? DisplayName { get; set; } public bool? Pinned { get; set; } public DateTimeOffset? LastAccess { get; set; } public Guid? FolderId { get; set; } public List<LinkedYmmpFileDto> LinkedYmmpFiles { get; set; } = []; [JsonExtensionData] public Dictionary<string, JsonElement>? ExtensionData { get; set; } }
    private sealed record MetadataAssociationDto(string Directory, Guid RootRevisionId, string RootSha256,
        Guid SelectedRevisionId, string SelectedSha256, Guid ObservedHeadRevisionId, string ObservedHeadSha256)
    {
        [JsonExtensionData]
        public Dictionary<string, JsonElement>? ExtensionData { get; set; }
        internal ProjectMetadataAssociation ToDomain() => new(Directory, RootRevisionId, RootSha256, SelectedRevisionId,
            SelectedSha256, ObservedHeadRevisionId, ObservedHeadSha256);
        internal static MetadataAssociationDto? FromDomain(ProjectMetadataAssociation? value) =>
            value is null ? null : new(value.Directory, value.RootRevisionId, value.RootSha256, value.SelectedRevisionId,
                value.SelectedSha256, value.ObservedHeadRevisionId, value.ObservedHeadSha256);
    }

    private sealed record FileIdentityDto(ulong Volume, Guid FileId, string Sha256)
    {
        [JsonExtensionData]
        public Dictionary<string, JsonElement>? ExtensionData { get; set; }
        internal ProjectFileIdentity ToDomain() => new(Volume, FileId, Sha256);
        internal static FileIdentityDto? FromDomain(ProjectFileIdentity? value) =>
            value is null ? null : new(value.Volume, value.FileId, value.Sha256);
    }

    private sealed class LinkedYmmpFileDto { public Guid? Id { get; set; } public string? FilePath { get; set; } public string? DisplayName { get; set; } public string? Role { get; set; } public string? Memo { get; set; } public DateTimeOffset? RegisteredAt { get; set; } public DateTimeOffset? UpdatedAt { get; set; } public DateTimeOffset? LastCheckedAt { get; set; } public bool? Exists { get; set; } [JsonExtensionData] public Dictionary<string, JsonElement>? ExtensionData { get; set; } }
    private sealed class ProjectFolderDto { public Guid? Id { get; set; } public string? Name { get; set; } public int? DisplayOrder { get; set; } public DateTimeOffset? CreatedAt { get; set; } public DateTimeOffset? UpdatedAt { get; set; } [JsonExtensionData] public Dictionary<string, JsonElement>? ExtensionData { get; set; } }
}
