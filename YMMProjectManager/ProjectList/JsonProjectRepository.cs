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
    //CSJ,field=PluginDirectoryName,プラグインディレクトリ名
    private const string PluginDirectoryName = "YMMProjectManager";
    //CSJ,field=JsonOptions,JSONオプション
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    //CSJ,field=StrictRecoveryOptions,厳格復旧オプション
    private static readonly JsonSerializerOptions StrictRecoveryOptions = new() { UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow };
    //CSJ,field=ReservedRootNames,予約済みルート名前一覧
    private static readonly HashSet<string> ReservedRootNames = ReservedNames<ProjectListFileDto>();
    //CSJ,field=ReservedProjectNames,予約済みプロジェクト名前一覧
    private static readonly HashSet<string> ReservedProjectNames = ReservedNames<ProjectRecordDto>();
    //CSJ,field=ReservedLinkNames,予約済みリンク名前一覧
    private static readonly HashSet<string> ReservedLinkNames = ReservedNames<LinkedYmmpFileDto>();
    //CSJ,field=ReservedFolderNames,予約済みフォルダー名前一覧
    private static readonly HashSet<string> ReservedFolderNames = ReservedNames<ProjectFolderDto>();
    //CSJ,field=ReservedMetadataNames,予約済みメタデータ名前一覧
    private static readonly HashSet<string> ReservedMetadataNames = ReservedNames<MetadataAssociationDto>();
    //CSJ,field=ReservedIdentityNames,予約済み識別情報名前一覧
    private static readonly HashSet<string> ReservedIdentityNames = ReservedNames<FileIdentityDto>();
    //CSJ,field=SaveGate,保存排他制御
    private static readonly SemaphoreSlim SaveGate = new(1, 1);
    //CSJ,field=jsonPath,JSONのパス
    private readonly string jsonPath;
    //CSJ,field=writeTemporaryFile,書き込み一時ファイル
    private readonly Func<Stream, byte[], CancellationToken, Task> writeTemporaryFile;
    //CSJ,field=beforeReplace,置換前の処理
    private readonly Action? beforeReplace;
    //CSJ,field=afterReplace,変更後置換
    private readonly Action? afterReplace;
    //CSJ,field=beforeCandidate,候補を用意する前の処理
    private readonly Action? beforeCandidate;
    //CSJ,field=beforeReceipt,照合記録の保存前の処理
    private readonly Action? beforeReceipt;
    //CSJ,field=afterReceipt,変更後照合記録
    private readonly Action? afterReceipt;
    private readonly Action? afterDraftClaim;
    private readonly Action? duringReceipt;
    //CSJ,field=afterRevisionCheck,変更後版確認
    private readonly Action? afterRevisionCheck;
    private readonly Action? afterParking;
    //CSJ,field=beforeJournalCleanup,作業記録を片付ける前の処理
    private readonly Action? beforeJournalCleanup;
    //CSJ,field=replacement,置換
    private readonly ProjectListReplacement replacement;
    //CSJ,field=logger,ロガー
    private readonly FileLogger logger;

    public JsonProjectRepository(FileLogger logger) : this(logger, ResolvePath()) { }

    // Internal hooks make failures at the file boundary deterministic in isolated tests.
    internal JsonProjectRepository(FileLogger logger, string path,
        Func<Stream, byte[], CancellationToken, Task>? writeTemporaryFile = null, Action? beforeReplace = null,
        //CSJ,parameter=afterReplace,変更後置換
        Action? afterReplace = null, Action? beforeCandidate = null, Action? beforeReceipt = null,
        //CSJ,parameter=afterReceipt,変更後照合記録
        Action? afterReceipt = null, Action? afterDraftClaim = null, Action? duringReceipt = null, Action? afterRevisionCheck = null,
        Action? afterParking = null, Action? beforeJournalCleanup = null)
    {
        this.logger = logger;
        jsonPath = Path.GetFullPath(path);
        replacement = new ProjectListReplacement(jsonPath);
        this.writeTemporaryFile = writeTemporaryFile ?? ((stream, bytes, token) => stream.WriteAsync(bytes, token).AsTask());
        this.beforeReplace = beforeReplace;
        this.afterReplace = afterReplace;
        this.beforeCandidate = beforeCandidate;
        this.beforeReceipt = beforeReceipt;
        this.afterReceipt = afterReceipt;
        this.afterDraftClaim = afterDraftClaim;
        this.duringReceipt = duringReceipt;
        this.afterRevisionCheck = afterRevisionCheck;
        this.afterParking = afterParking;
        this.beforeJournalCleanup = beforeJournalCleanup;
    }

    //CSJ,property=RecoveryDirectory,復旧ディレクトリ
    internal string RecoveryDirectory => Path.GetFullPath(jsonPath) + ".recovery";

    public async Task<ProjectStore> LoadAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            //CSJ,variable=content,内容
            var content = await TryReadListAsync(cancellationToken).ConfigureAwait(false);
            if (content is not null) return DeserializeStore(content);
            // A reader could have arrived while a cooperating save had parked the active name.
            //CSJ,variable=processLock,プロセスロック
            var processLock = TryAcquireLockInExistingDirectory();
            using (processLock)
            {
                replacement.RequireNoPending();
                content = await TryReadListAsync(cancellationToken).ConfigureAwait(false);
                return content is null
                    ? new ProjectStore { Revision = new ProjectListRevision(null), IsRecoverySafe = true }
                    : DeserializeStore(content);
            }
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) { logger.Error("Failed to load projects.json", ex); throw; }
    }

    //CSJ,method=TryReadListAsync,一覧の読み取りを試みる（非同期）
    private async Task<byte[]?> TryReadListAsync(CancellationToken token)
    {
        try { return await File.ReadAllBytesAsync(jsonPath, token).ConfigureAwait(false); }
        catch (FileNotFoundException) { return null; }
        catch (DirectoryNotFoundException) { return null; }
    }

    private FileStream? TryAcquireLockInExistingDirectory()
    {
        try { return AcquireProcessLock(); }
        catch (DirectoryNotFoundException) { return null; }
    }

    //CSJ,method=DeserializeStore,保管先を逆シリアル化する
    internal static ProjectStore DeserializeStore(byte[] content)
    {
        //CSJ,variable=jsonContent,JSON内容
        var jsonContent = StripUtf8Bom(content);
        var dto = JsonSerializer.Deserialize<ProjectListFileDto>(jsonContent.Span, JsonOptions);
        if (dto is null) throw new InvalidDataException("Project list is null.");
        //CSJ,variable=safe,安全
        var safe = IsSafeForRecovery(content);
        //CSJ,variable=nested,入れ子
        var nested = safe ? CaptureNestedFields(dto) : null;
        //CSJ,variable=revision,版
        var revision = new ProjectListRevision(Convert.ToHexString(SHA256.HashData(content)), dto.SaveOperationId)
        {
            PreservedRootFieldsJson = dto.ExtensionData is { Count: > 0 } fields ? JsonSerializer.Serialize(fields) : null,
            PreservedNestedFieldsJson = nested is null ? null : JsonSerializer.Serialize(nested),
            PreservedFieldAnchors = nested is null ? null : string.Join(';', EnumeratePreservedAnchors(nested).Order(StringComparer.Ordinal)),
        };
        //CSJ,variable=store,保管先
        var store = new ProjectStore { Revision = revision, IsRecoverySafe = safe };
        foreach (var f in dto.Folders ?? [])
        {
            if (f.Id is null || string.IsNullOrWhiteSpace(f.Name)) continue;
            //CSJ,variable=created,作成済み
            var created = f.CreatedAt ?? DateTimeOffset.UnixEpoch;
            store.Folders.Add(new ProjectFolder { Id = f.Id.Value, Name = f.Name!, DisplayOrder = f.DisplayOrder ?? 0, CreatedAt = created, UpdatedAt = f.UpdatedAt ?? created });
        }
        //CSJ,variable=projectIds,プロジェクトID一覧
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

    //CSJ,method=LegacyLinkId,旧形式リンクID
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
            //CSJ,variable=jsonContent,JSON内容
            var jsonContent = StripUtf8Bom(content);
            var dto = JsonSerializer.Deserialize<ProjectListFileDto>(jsonContent.Span, StrictRecoveryOptions);
            if (dto?.Projects is null || dto.Folders is null || dto.SaveOperationId == Guid.Empty) return false;
            //CSJ,variable=document,文書
            using var document = JsonDocument.Parse(jsonContent);
            if (dto.ExtensionData is { Count: > 0 } unknown &&
                (!document.RootElement.TryGetProperty("Projects", out _) || unknown.Keys.Any(ReservedRootNames.Contains))) return false;
            if (dto.Folders.Any(f => f is null || f.Id is null || f.Id == Guid.Empty || string.IsNullOrWhiteSpace(f.Name) ||
                !CanPreserve(f.ExtensionData, ReservedFolderNames, true))) return false;
            if (dto.Folders.Select(f => f.Id).Distinct().Count() != dto.Folders.Count) return false;
            //CSJ,variable=projectIds,プロジェクトID一覧
            var projectIds = new HashSet<Guid>();
            //CSJ,variable=physicalIds,物理ID一覧
            var physicalIds = new HashSet<(ulong Volume, Guid Id)>();
            //CSJ,variable=project,プロジェクト
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
                //CSJ,variable=ids,ID一覧
                var ids = project.LinkedYmmpFiles.Where(l => l.Id.HasValue).Select(l => l.Id!.Value).ToArray();
                if (ids.Distinct().Count() != ids.Length) return false;
            }
            return !HasDuplicateProperties(document.RootElement);
        }
        catch (JsonException) { return false; }
    }

    //CSJ,method=ReservedNames,予約済み名前一覧
    private static HashSet<string> ReservedNames<T>() => typeof(T).GetProperties()
        .Where(property => !property.IsDefined(typeof(JsonExtensionDataAttribute), inherit: false))
        .Select(property => property.Name)
        .Concat(["Version", "SchemaVersion", "FormatVersion", "$schema"])
        .ToHashSet(StringComparer.OrdinalIgnoreCase);

    private static bool CanPreserve(Dictionary<string, JsonElement>? fields, HashSet<string> reserved, bool hasAnchor) =>
        fields is not { Count: > 0 } || (hasAnchor && !fields.Keys.Any(reserved.Contains));

    //CSJ,record=ProjectFields,プロジェクトフィールド一覧
    private sealed record ProjectFields(Dictionary<string, JsonElement>? Fields, Dictionary<Guid, Dictionary<string, JsonElement>> Links,
        MetadataAssociationDto? Metadata, FileIdentityDto? Identity);
    //CSJ,record=NestedFields,入れ子フィールド一覧
    private sealed record NestedFields(Dictionary<Guid, ProjectFields> Projects, Dictionary<Guid, Dictionary<string, JsonElement>> Folders);

    //CSJ,method=CaptureNestedFields,入れ子フィールド一覧を取得する
    private static NestedFields? CaptureNestedFields(ProjectListFileDto dto)
    {
        //CSJ,variable=projects,プロジェクト一覧
        var projects = new Dictionary<Guid, ProjectFields>();
        //CSJ,variable=project,プロジェクト
        foreach (var project in dto.Projects.Where(item => item.ProjectId.HasValue))
        {
            //CSJ,variable=links,リンク一覧
            var links = project.LinkedYmmpFiles.Where(link => link.ExtensionData is { Count: > 0 })
                .ToDictionary(link => link.Id!.Value, link => link.ExtensionData!);
            //CSJ,variable=metadata,メタデータ
            var metadata = project.MetadataAssociation is { ExtensionData.Count: > 0 } ? project.MetadataAssociation : null;
            //CSJ,variable=identity,識別情報
            var identity = project.FileIdentity is { ExtensionData.Count: > 0 } ? project.FileIdentity : null;
            if (project.ExtensionData is { Count: > 0 } || links.Count != 0 || metadata is not null || identity is not null)
                projects.Add(project.ProjectId!.Value, new ProjectFields(project.ExtensionData, links, metadata, identity));
        }
        //CSJ,variable=folders,フォルダー一覧
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
            //CSJ,variable=link,リンク
            foreach (var link in fields.Links.Keys) yield return ProjectListFieldAnchors.Link(id, link);
        }
        foreach (var id in nested.Folders.Keys) yield return ProjectListFieldAnchors.Folder(id);
    }

    //CSJ,method=RestoreNestedFields,入れ子フィールド一覧を復元する
    private static void RestoreNestedFields(ProjectListFileDto dto, string preserved)
    {
        //CSJ,variable=nested,入れ子
        var nested = JsonSerializer.Deserialize<NestedFields>(preserved) ?? throw new InvalidDataException("Preserved project list fields are invalid.");
        foreach (var (id, fields) in nested.Projects)
        {
            //CSJ,variable=project,プロジェクト
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
                //CSJ,variable=link,リンク
                var link = project.LinkedYmmpFiles.SingleOrDefault(item => item.Id == linkId)
                    ?? throw new InvalidDataException("This change would remove a project link with unknown fields.");
                link.ExtensionData = linkFields;
            }
        }
        foreach (var (id, fields) in nested.Folders)
        {
            //CSJ,variable=folder,フォルダー
            var folder = dto.Folders.SingleOrDefault(item => item.Id == id)
                ?? throw new InvalidDataException("This change would remove a folder with unknown fields.");
            folder.ExtensionData = fields;
        }
    }

    //CSJ,method=ValidMetadata,有効メタデータ
    private static bool ValidMetadata(ProjectMetadataAssociation value) =>
        !string.IsNullOrWhiteSpace(value.Directory) && Path.IsPathFullyQualified(value.Directory) &&
        value.RootRevisionId != Guid.Empty && value.SelectedRevisionId != Guid.Empty &&
        value.ObservedHeadRevisionId != Guid.Empty && IsHash(value.RootSha256) &&
        IsHash(value.SelectedSha256) && IsHash(value.ObservedHeadSha256);

    //CSJ,method=IsHash,ハッシュかを判定する
    private static bool IsHash(string? value) => value is { Length: 64 } && value.All(Uri.IsHexDigit);

    private static ReadOnlyMemory<byte> StripUtf8Bom(byte[] content) =>
        content.Length >= 3 && content[0] == 0xEF && content[1] == 0xBB && content[2] == 0xBF
            ? content.AsMemory(3) : content.AsMemory();

    //CSJ,method=HasDuplicateProperties,重複プロパティ一覧の有無を判定する
    private static bool HasDuplicateProperties(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            //CSJ,variable=names,名前一覧
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            //CSJ,variable=property,プロパティ
            foreach (var property in element.EnumerateObject())
                if (!names.Add(property.Name) || HasDuplicateProperties(property.Value)) return true;
        }
        else if (element.ValueKind == JsonValueKind.Array)
            foreach (var item in element.EnumerateArray()) if (HasDuplicateProperties(item)) return true;
        return false;
    }

    public Task SaveAsync(ProjectStore store, CancellationToken cancellationToken = default) =>
        SaveCoreAsync(store, null, cancellationToken);

    //CSJ,method=WritePendingDraftAsync,保留下書きを書き込む（非同期）
    internal async Task<string> WritePendingDraftAsync(ProjectStore baseline, ProjectStore local,
        //CSJ,parameter=evidence,証跡
        Dictionary<string, ProjectListDrafts.Evidence> evidence, string? predecessor, Action? beforePublication = null,
        //CSJ,parameter=afterPublication,変更後確定反映
        Action? afterPublication = null, CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        Directory.CreateDirectory(Path.GetDirectoryName(jsonPath)!);
        //CSJ,variable=processLock,プロセスロック
        using var processLock = AcquireProcessLock();
        return await new ProjectListDrafts(RecoveryDirectory)
            .WriteAsync(baseline, local, evidence, predecessor, beforePublication, afterPublication, token).ConfigureAwait(false);
    }

    internal async Task RetireReadyDraftAsync(string readyDraft, CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        //CSJ,variable=drafts,下書き一覧
        var drafts = new ProjectListDrafts(RecoveryDirectory);
        //CSJ,variable=processLock,プロセスロック
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
        //CSJ,variable=drafts,下書き一覧
        var drafts = new ProjectListDrafts(RecoveryDirectory);
        //CSJ,variable=processLock,プロセスロック
        using var processLock = AcquireProcessLock();
        var (id, operation) = drafts.GetClaimedIdentity(claimedDraft);
        await drafts.ReadAsync(claimedDraft, claimed: true).ConfigureAwait(false);
        replacement.RequireNoPending();
        // Keeping or restoring an explicitly chosen external version must not release the superseded local claim.
        if (operation is Guid known && replacement.HasExternalResolution(known))
            return ProjectListPublications.Outcome.Unknown;
        //CSJ,variable=content,内容
        var content = await TryReadListAsync(token).ConfigureAwait(false);
        //CSJ,variable=store,保管先
        var store = content is null ? new ProjectStore { Revision = new ProjectListRevision(null), IsRecoverySafe = true } : DeserializeStore(content);
        //CSJ,variable=revision,版
        var revision = store.Revision!;
        //CSJ,variable=outcome,処理結果
        var outcome = await new ProjectListPublications(RecoveryDirectory)
            .ReconcileAsync(id, operation, revision.Sha256, revision.OperationId, VerifyUnconsumedCandidateAsync, token).ConfigureAwait(false);
        if (outcome == ProjectListPublications.Outcome.Unpublished && store.IsRecoverySafe)
            drafts.ReleaseUnpublishedClaim(claimedDraft);
        else if (outcome == ProjectListPublications.Outcome.Committed)
            drafts.Retire(claimedDraft, claimed: true);
        else
            outcome = ProjectListPublications.Outcome.Unknown;
        logger.Info($"Project list publication reconciled. draft={id:N} outcome={outcome}");
        return outcome;
    }

    //CSJ,method=AcquireProcessLock,プロセスロックを取得する
    private FileStream AcquireProcessLock()
    {
        try { return new FileStream(jsonPath + ".lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
        catch (IOException ex) when ((ex.HResult & 0xffff) == 32) { throw new ProjectListBusyException(ex); }
    }

    //CSJ,method=CandidatePath,候補パス
    private string CandidatePath(Guid operationId) => $"{jsonPath}.tmp-{operationId:N}";

    private async Task<bool> VerifyUnconsumedCandidateAsync(Guid operationId, ProjectListReplacement.FileVersion expected,
        CancellationToken token)
    {
        try
        {
            using var candidate = ProjectListReplacement.TryOpen(CandidatePath(operationId));
            return candidate is not null &&
                await ProjectListReplacement.MatchesAsync(candidate, expected, token).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logger.Error(ex, "Unconsumed project list candidate could not be verified.");
            return false;
        }
    }

    //CSJ,property=HasInterruptedPublication,中断済み確定反映の有無
    internal bool HasInterruptedPublication => replacement.IsPending();
    //CSJ,property=InterruptedPublicationPath,中断済み確定反映パス
    internal string InterruptedPublicationPath => replacement.JournalPath;

    //CSJ,method=InspectInterruptedPublicationAsync,中断済み確定反映を点検する（非同期）
    internal async Task<ProjectListReplacement.Recovery?> InspectInterruptedPublicationAsync(CancellationToken token = default)
    {
        //CSJ,variable=processLock,プロセスロック
        using var processLock = TryAcquireLockInExistingDirectory();
        return await replacement.InspectAsync(token).ConfigureAwait(false);
    }

    //CSJ,method=RecoverInterruptedPublicationAsync,中断済み確定反映を復旧する（非同期）
    internal async Task<ProjectListReplacement.Recovery> RecoverInterruptedPublicationAsync(
        ProjectListReplacement.Recovery confirmed, CancellationToken token = default)
    {
        //CSJ,variable=processLock,プロセスロック
        using var processLock = AcquireProcessLock();
        return await replacement.ResolveAsync(confirmed, token).ConfigureAwait(false);
    }

    private async Task ParkAndPublishAsync(FileStream candidate, Guid operationId, ProjectListReplacement.FileVersion original,
        ProjectListReplacement.FileVersion result, CancellationToken token)
    {
        //CSJ,variable=marker,マーカー
        await using var marker = await replacement.WriteAsync(new ProjectListReplacement.Journal(1, operationId, original, result), token).ConfigureAwait(false);
        var parkedPath = replacement.ParkedPath(operationId);
        FileStream? parked = null;
        var didPark = false;
        var didPublish = false;
        //CSJ,variable=verifiedOriginal,検証済み元の
        var verifiedOriginal = false;
        //CSJ,variable=journalResolved,作業記録解決済み
        var journalResolved = false;
        try
        {
            afterRevisionCheck?.Invoke();
            token.ThrowIfCancellationRequested();
            File.Move(jsonPath, parkedPath, overwrite: false);
            didPark = true;
            afterParking?.Invoke();
            parked = ProjectListReplacement.Open(parkedPath, canRename: true);
            // The active name is already vacant. Establish whether we can safely restore the original
            // even if the caller cancels during this short verification.
            //CSJ,variable=verification,検証
            using (var verification = new CancellationTokenSource(TimeSpan.FromSeconds(10)))
            {
                try { verifiedOriginal = await ProjectListReplacement.MatchesAsync(parked, original, verification.Token).ConfigureAwait(false); }
                catch (OperationCanceledException ex) { throw new IOException("Could not verify the parked project list before the recovery deadline.", ex); }
            }
            if (!verifiedOriginal)
                throw new ProjectListConflictException();
            token.ThrowIfCancellationRequested();
            ProjectListReplacement.VerifyCompatibleFileMetadata(parked, candidate, parkedPath);
            File.SetCreationTimeUtc(candidate.SafeFileHandle, File.GetCreationTimeUtc(parked.SafeFileHandle));
            token.ThrowIfCancellationRequested();
            try { OwnedFilePublication.RenameOpenedPath(candidate.SafeFileHandle, jsonPath); }
            catch (IOException ex) when (ex.InnerException is System.ComponentModel.Win32Exception { NativeErrorCode: 80 or 183 })
            { throw new ProjectListConflictException(); }
            didPublish = true;
            beforeJournalCleanup?.Invoke();
            if (!OwnedFilePublication.TryDeleteOpenedPath(parked.SafeFileHandle))
                logger.Info($"Published project list; the original remains in {parkedPath}.");
            journalResolved = true;
        }
        finally
        {
            if (!didPark) journalResolved = true;
            if (didPark && !didPublish && verifiedOriginal && parked is not null)
            {
                try
                {
                    OwnedFilePublication.RenameOpenedPath(parked.SafeFileHandle, jsonPath);
                    journalResolved = true;
                }
                catch (IOException ex) { logger.Error(ex, $"Project list versions remain beside {jsonPath}."); }
            }
            parked?.Dispose();
            if (journalResolved && !OwnedFilePublication.TryDeleteOpenedPath(marker.SafeFileHandle))
                logger.Info($"Project list journal left at {replacement.JournalPath}.");
        }
    }

    //CSJ,method=SaveCoreAsync,中核を保存する（非同期）
    private async Task SaveCoreAsync(ProjectStore store, string? readyDraft, CancellationToken cancellationToken)
    {
        FileStream? candidate = null;
        //CSJ,variable=processLock,プロセスロック
        FileStream? processLock = null;
        //CSJ,variable=drafts,下書き一覧
        var drafts = new ProjectListDrafts(RecoveryDirectory);
        string? claim = null;
        var publicationAttempted = false;
        var committed = false;
        //CSJ,variable=stage,作業段階
        var stage = "waiting";
        await SaveGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            stage = "serialize";
            //CSJ,variable=baseline,基準
            var baseline = store.Revision ?? throw new InvalidDataException("Project list must be loaded before saving.");
            //CSJ,variable=operationId,操作ID
            var operationId = Guid.NewGuid();
            //CSJ,variable=bytes,バイト列
            var bytes = SerializeStore(store, operationId);
            //CSJ,variable=expectedResult,期待値結果
            var expectedResult = Convert.ToHexString(SHA256.HashData(bytes));
            cancellationToken.ThrowIfCancellationRequested();
            replacement.RequireLocalNtfs();
            //CSJ,variable=directory,ディレクトリ
            var directory = Path.GetDirectoryName(jsonPath)!;
            Directory.CreateDirectory(directory);
            stage = "inter-process lock";
            processLock = AcquireProcessLock();
            //CSJ,variable=fence,整合性境界
            using var fence = OwnedFilePublication.DirectoryFence.Open(directory, enforceShare: true);
            replacement.RequireNoPending();
            stage = "write publication receipt";
            //CSJ,variable=draftId,下書きID
            Guid? draftId = null;
            if (readyDraft is not null)
            {
                draftId = drafts.GetReadyId(readyDraft);
                await drafts.ReadAsync(readyDraft).ConfigureAwait(false);
            }
            var publications = new ProjectListPublications(RecoveryDirectory);
            //CSJ,variable=receipt,照合記録
            var receipt = new ProjectListPublications.Receipt(3, operationId, draftId, baseline.Sha256, expectedResult,
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
            //CSJ,variable=candidatePath,候補パス
            var candidatePath = CandidatePath(operationId);
            stage = "write temporary file";
            candidate = OwnedFilePublication.CreateOwnedOutputFile(candidatePath, readBack: true);
            using (var aclBaseline = ProjectListReplacement.TryOpen(jsonPath))
            {
                if ((aclBaseline is null) != (baseline.Sha256 is null))
                    throw new ProjectListConflictException();
                if (aclBaseline is not null) ProjectListReplacement.VerifyCompatibleFileMetadata(aclBaseline, candidate, jsonPath);
                await writeTemporaryFile(candidate, bytes, cancellationToken).ConfigureAwait(false);
                await candidate.FlushAsync(cancellationToken).ConfigureAwait(false);
                candidate.Flush(flushToDisk: true);
            }
            //CSJ,variable=candidateVersion,候補バージョン
            var candidateVersion = await ProjectListReplacement.ReadFileVersionAsync(candidate, cancellationToken).ConfigureAwait(false);
            if (candidateVersion.Sha256 != expectedResult)
                throw new IOException("The staged project list bytes do not match the publication receipt.");

            stage = "replace";
            beforeReplace?.Invoke();
            cancellationToken.ThrowIfCancellationRequested();
            await publications.WriteAsync(receipt with { PublicationStarted = true, Candidate = candidateVersion }, cancellationToken).ConfigureAwait(false);
            using var current = ProjectListReplacement.TryOpen(jsonPath, FileShare.Read | FileShare.Delete);
            //CSJ,variable=currentVersion,現在のバージョン
            var currentVersion = current is null ? null :
                await ProjectListReplacement.ReadFileVersionAsync(current, cancellationToken).ConfigureAwait(false);
            if (!string.Equals(currentVersion?.Sha256, baseline.Sha256, StringComparison.Ordinal))
                throw new ProjectListConflictException();
            if (currentVersion is not null)
            {
                await ParkAndPublishAsync(candidate, operationId, currentVersion, candidateVersion, cancellationToken).ConfigureAwait(false);
            }
            else
            {
                afterRevisionCheck?.Invoke();
                cancellationToken.ThrowIfCancellationRequested();
                publicationAttempted = true;
                OwnedFilePublication.RenameOpenedPath(candidate.SafeFileHandle, jsonPath);
            }
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
                    else if (!publicationAttempted && !replacement.IsPending()) drafts.ReleaseUnpublishedClaim(claim);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    logger.Error(ex, "A project list draft needs reconciliation.");
                }
            }
            if (candidate is not null)
            {
                try
                {
                    try
                    {
                        if (!committed && !replacement.IsPending())
                            OwnedFilePublication.TryDeleteOpenedPath(candidate.SafeFileHandle);
                    }
                    finally { await candidate.DisposeAsync().ConfigureAwait(false); }
                }
                catch (Exception ex) { logger.Error(ex, "Failed to close project list temporary file."); }
            }
            processLock?.Dispose();
            SaveGate.Release();
        }
    }

    //CSJ,method=SerializeStore,保管先をシリアル化する
    internal static byte[] SerializeStore(ProjectStore store, Guid? saveOperationId = null)
    {
        if (!store.IsRecoverySafe) throw new InvalidDataException("Project list contains data that cannot be safely preserved.");
        //CSJ,variable=projectIds,プロジェクトID一覧
        var projectIds = new HashSet<Guid>();
        //CSJ,variable=project,プロジェクト
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
        //CSJ,variable=bytes,バイト列
        var bytes = JsonSerializer.SerializeToUtf8Bytes(dto, JsonOptions);
        if (!IsSafeForRecovery(bytes)) throw new InvalidDataException("Project list contains entries that cannot be safely reloaded.");
        return bytes;
    }

    // Recovery export never replaces an existing file, including a file created after the dialog.
    //CSJ,method=SaveNewCopyAsync,新規コピーを保存する（非同期）
    internal Task SaveNewCopyAsync(ProjectStore store, CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        return WriteNewCopyAsync(jsonPath, SerializeStore(store), token);
    }

    //CSJ,method=ExportOriginalAsync,元のを書き出す（非同期）
    internal async Task ExportOriginalAsync(string destination, CancellationToken token = default)
    {
        //CSJ,variable=bytes,バイト列
        var bytes = await ReadOriginalAsync(token).ConfigureAwait(false);
        await WriteNewCopyAsync(Path.GetFullPath(destination), bytes, token).ConfigureAwait(false);
    }

    //CSJ,method=ReadOriginalAsync,元のを読み取る（非同期）
    internal Task<byte[]> ReadOriginalAsync(CancellationToken token = default) =>
        File.ReadAllBytesAsync(jsonPath, token);

    //CSJ,method=WriteNewCopyAsync,新規コピーを書き込む（非同期）
    internal static async Task WriteNewCopyAsync(string destination, byte[] bytes, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        destination = Path.GetFullPath(destination);
        //CSJ,variable=directory,ディレクトリ
        var directory = Path.GetDirectoryName(destination)!;
        var candidate = Path.Combine(directory, $"{Path.GetFileName(destination)}.tmp-{Guid.NewGuid():N}");
        //CSJ,variable=owned,管理対象の
        var owned = false;
        try
        {
            //CSJ,variable=stream,ストリーム
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

    //CSJ,method=ResolvePath,パスを解決する
    private static string ResolvePath(){ var runtimeYmmDir = Environment.GetEnvironmentVariable("YMM4DirPath"); if (!string.IsNullOrWhiteSpace(runtimeYmmDir)) return Path.Combine(runtimeYmmDir, "user", "plugin", PluginDirectoryName, "data", "projects.json"); var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData); return Path.Combine(appData, "YMMProjectManager", "plugin", PluginDirectoryName, "data", "projects.json"); }

    //CSJ,class=ProjectListFileDto,プロジェクト一覧ファイルDTO
    private sealed class ProjectListFileDto
    {
        //CSJ,property=Projects,プロジェクト一覧
        public List<ProjectRecordDto> Projects { get; set; } = [];
        //CSJ,property=Folders,フォルダー一覧
        public List<ProjectFolderDto> Folders { get; set; } = [];
        //CSJ,property=SaveOperationId,保存操作ID
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public Guid? SaveOperationId { get; set; }
        //CSJ,property=ExtensionData,拡張子データ
        [JsonExtensionData]
        public Dictionary<string, JsonElement>? ExtensionData { get; set; }
    }
    //CSJ,class=ProjectRecordDto,プロジェクト記録DTO
    private sealed class ProjectRecordDto { public string? FullPath { get; set; } [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public Guid? ProjectId { get; set; } [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public MetadataAssociationDto? MetadataAssociation { get; set; } [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public FileIdentityDto? FileIdentity { get; set; } public string? DisplayName { get; set; } public bool? Pinned { get; set; } public DateTimeOffset? LastAccess { get; set; } public Guid? FolderId { get; set; } public List<LinkedYmmpFileDto> LinkedYmmpFiles { get; set; } = []; [JsonExtensionData] public Dictionary<string, JsonElement>? ExtensionData { get; set; } }
    //CSJ,record=MetadataAssociationDto,メタデータ関連付けDTO
    private sealed record MetadataAssociationDto(string Directory, Guid RootRevisionId, string RootSha256,
        Guid SelectedRevisionId, string SelectedSha256, Guid ObservedHeadRevisionId, string ObservedHeadSha256)
    {
        //CSJ,property=ExtensionData,拡張子データ
        [JsonExtensionData]
        public Dictionary<string, JsonElement>? ExtensionData { get; set; }
        internal ProjectMetadataAssociation ToDomain() => new(Directory, RootRevisionId, RootSha256, SelectedRevisionId,
            SelectedSha256, ObservedHeadRevisionId, ObservedHeadSha256);
        internal static MetadataAssociationDto? FromDomain(ProjectMetadataAssociation? value) =>
            value is null ? null : new(value.Directory, value.RootRevisionId, value.RootSha256, value.SelectedRevisionId,
                value.SelectedSha256, value.ObservedHeadRevisionId, value.ObservedHeadSha256);
    }

    //CSJ,record=FileIdentityDto,ファイル識別情報DTO
    private sealed record FileIdentityDto(ulong Volume, Guid FileId, string Sha256)
    {
        //CSJ,property=ExtensionData,拡張子データ
        [JsonExtensionData]
        public Dictionary<string, JsonElement>? ExtensionData { get; set; }
        internal ProjectFileIdentity ToDomain() => new(Volume, FileId, Sha256);
        internal static FileIdentityDto? FromDomain(ProjectFileIdentity? value) =>
            value is null ? null : new(value.Volume, value.FileId, value.Sha256);
    }

    //CSJ,class=LinkedYmmpFileDto,関連YMMPファイルのDTO
    private sealed class LinkedYmmpFileDto { public Guid? Id { get; set; } public string? FilePath { get; set; } public string? DisplayName { get; set; } public string? Role { get; set; } public string? Memo { get; set; } public DateTimeOffset? RegisteredAt { get; set; } public DateTimeOffset? UpdatedAt { get; set; } public DateTimeOffset? LastCheckedAt { get; set; } public bool? Exists { get; set; } [JsonExtensionData] public Dictionary<string, JsonElement>? ExtensionData { get; set; } }
    //CSJ,class=ProjectFolderDto,プロジェクトフォルダーDTO
    private sealed class ProjectFolderDto { public Guid? Id { get; set; } public string? Name { get; set; } public int? DisplayOrder { get; set; } public DateTimeOffset? CreatedAt { get; set; } public DateTimeOffset? UpdatedAt { get; set; } [JsonExtensionData] public Dictionary<string, JsonElement>? ExtensionData { get; set; } }
}
