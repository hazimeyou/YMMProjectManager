using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using YMMProjectManager.Domain;

namespace YMMProjectManager.Application;

internal sealed record ProjectListMergeResult(ProjectStore? Store, IReadOnlyList<string> Changes, IReadOnlyList<string> Conflicts);

// A conservative three-way merge. Linked files are treated as one field, so competing
// link edits are rejected rather than guessed or silently combined.
internal static class ProjectListMerge
{
    internal static ProjectStore Snapshot(ProjectStore source) => new()
    {
        Revision = source.Revision, IsRecoverySafe = source.IsRecoverySafe,
        Projects = source.Projects.Select(Clone).ToList(), Folders = source.Folders.Select(Clone).ToList(),
    };

    internal static ProjectListMergeResult Merge(ProjectStore baseline, ProjectStore local, ProjectStore remote)
    {
        var conflicts = new List<string>(); var changes = new List<string>();
        if (!baseline.IsRecoverySafe || !local.IsRecoverySafe || !remote.IsRecoverySafe || remote.Revision is null)
            return new(null, changes, ["一覧に未知・不正な項目があるか、最新の版がありません。"]);
        if (!Valid(baseline) || !Valid(local) || !Valid(remote))
            return new(null, changes, ["パス、project ID、folder ID の重複または不正があります。"]);
        if (!OrderIsAppendOnly(baseline.Projects.Select(p => p.FullPath), local.Projects.Select(p => p.FullPath), StringComparer.OrdinalIgnoreCase) ||
            !OrderIsAppendOnly(baseline.Folders.Select(f => f.Id), local.Folders.Select(f => f.Id), EqualityComparer<Guid>.Default))
            return new(null, changes, ["未保存の並べ替えまたは途中への挿入は自動再適用できません。"]);
        foreach (var original in baseline.Projects.Where(p => p.ProjectId is not null))
        {
            var localIdentity = local.Projects.SingleOrDefault(p => p.ProjectId == original.ProjectId);
            var remoteIdentity = remote.Projects.SingleOrDefault(p => p.ProjectId == original.ProjectId);
            if ((localIdentity is not null && !string.Equals(localIdentity.FullPath, original.FullPath, StringComparison.OrdinalIgnoreCase)) ||
                (remoteIdentity is not null && !string.Equals(remoteIdentity.FullPath, original.FullPath, StringComparison.OrdinalIgnoreCase)))
                conflicts.Add($"project の移動または名前変更は自動再適用できません: {original.FullPath}");
        }
        var basePaths = baseline.Projects.Select(p => p.FullPath).ToHashSet(StringComparer.OrdinalIgnoreCase);
        bool AmbiguousLegacyMove(ProjectStore side) => baseline.Projects.Any(p => p.ProjectId is null && !side.Projects.Any(x => string.Equals(x.FullPath, p.FullPath, StringComparison.OrdinalIgnoreCase))) &&
            side.Projects.Any(p => !basePaths.Contains(p.FullPath));
        if (AmbiguousLegacyMove(local) || AmbiguousLegacyMove(remote)) conflicts.Add("ID のない項目の移動と追加・削除を区別できません。");
        if (conflicts.Count != 0) return new(null, changes, conflicts);
        var merged = Snapshot(remote);
        var baseProjects = baseline.Projects.ToDictionary(p => p.FullPath, StringComparer.OrdinalIgnoreCase);
        var localProjects = local.Projects.ToDictionary(p => p.FullPath, StringComparer.OrdinalIgnoreCase);
        var remoteProjects = remote.Projects.ToDictionary(p => p.FullPath, StringComparer.OrdinalIgnoreCase);
        foreach (var key in baseProjects.Keys.Union(localProjects.Keys, StringComparer.OrdinalIgnoreCase))
        {
            baseProjects.TryGetValue(key, out var b); localProjects.TryGetValue(key, out var l); remoteProjects.TryGetValue(key, out var r);
            if (b is null)
            {
                if (l is null) continue;
                if (l.ProjectId is null) { conflicts.Add($"ID のない新規項目は自動再適用できません: {key}"); continue; }
                if (r is null) { merged.Projects.Add(Clone(l)); changes.Add($"追加: {key} (ID: {l.ProjectId})"); }
                else if (!Same(l, r)) conflicts.Add($"同じパスへの異なる追加: {key}");
                continue;
            }
            if (l is null)
            {
                if (r is null) continue;
                if (b.ProjectId is null) { conflicts.Add($"ID のない項目の削除は自動再適用できません: {key}"); continue; }
                if (!Same(b, r)) conflicts.Add($"削除と外部変更の競合: {key}");
                else { merged.Projects.RemoveAll(p => string.Equals(p.FullPath, key, StringComparison.OrdinalIgnoreCase)); changes.Add($"削除: {key}"); }
                continue;
            }
            if (Same(b, l)) continue;
            if (r is null) { conflicts.Add($"編集と外部削除の競合: {key}"); continue; }
            if (b.ProjectId is null) { conflicts.Add($"ID のない項目の編集は自動再適用できません: {key}"); continue; }
            if (b.ProjectId != l.ProjectId) { conflicts.Add($"ID 設定には専用の競合回復を使用してください: {key}"); continue; }
            if (b.ProjectId != r.ProjectId) { conflicts.Add($"外部で project ID が変更されたため再適用できません: {key}"); continue; }
            var target = merged.Projects.Single(p => string.Equals(p.FullPath, key, StringComparison.OrdinalIgnoreCase));
            var before = conflicts.Count;
            target.FullPath = Field(b.FullPath, l.FullPath, r.FullPath, key, "FullPath", conflicts);
            target.ProjectId = Field(b.ProjectId, l.ProjectId, r.ProjectId, key, "ProjectId", conflicts);
            target.DisplayName = Field(b.DisplayName, l.DisplayName, r.DisplayName, key, "DisplayName", conflicts);
            target.Pinned = Field(b.Pinned, l.Pinned, r.Pinned, key, "Pinned", conflicts);
            target.LastAccess = Field(b.LastAccess, l.LastAccess, r.LastAccess, key, "LastAccess", conflicts);
            target.FolderId = Field(b.FolderId, l.FolderId, r.FolderId, key, "FolderId", conflicts);
            var bLinks = LinksFingerprint(b); var lLinks = LinksFingerprint(l); var rLinks = LinksFingerprint(r);
            var selected = Field(bLinks, lLinks, rLinks, key, "LinkedYmmpFiles", conflicts);
            if (selected == lLinks && lLinks != rLinks) { target.LinkedYmmpFiles.Clear(); foreach (var link in l.LinkedYmmpFiles) target.LinkedYmmpFiles.Add(Clone(link)); }
            if (before == conflicts.Count)
            {
                Describe(b.FullPath, l.FullPath, r.FullPath, key, "FullPath", changes);
                Describe(b.DisplayName, l.DisplayName, r.DisplayName, key, "DisplayName", changes);
                Describe(b.Pinned, l.Pinned, r.Pinned, key, "Pinned", changes);
                Describe(b.LastAccess, l.LastAccess, r.LastAccess, key, "LastAccess", changes);
                Describe(b.FolderId, l.FolderId, r.FolderId, key, "FolderId", changes);
                Describe(bLinks, lLinks, rLinks, key, "LinkedYmmpFiles", changes);
            }
        }
        MergeFolders(baseline, local, remote, merged, changes, conflicts);
        if (!Valid(merged)) conflicts.Add("再適用により project ID、パスまたは folder ID が重複します。");
        return conflicts.Count == 0 ? new(merged, changes, conflicts) : new(null, changes, conflicts);
    }

    private static T Field<T>(T b, T l, T r, string key, string name, List<string> conflicts)
    {
        if (Equal(l, b)) return r;
        if (Equal(r, b) || Equal(l, r)) return l;
        conflicts.Add($"{key}: {name} が両方で変更されました。"); return r;
    }

    private static void Describe<T>(T b, T l, T r, string key, string name, List<string> changes)
    {
        if (Equal(b, l)) return;
        changes.Add($"{key} / {name}\n  基準: {System.Text.Json.JsonSerializer.Serialize(b)}\n  未保存: {System.Text.Json.JsonSerializer.Serialize(l)}\n  最新: {System.Text.Json.JsonSerializer.Serialize(r)}");
    }

    private static bool Equal<T>(T left, T right) => left is DateTimeOffset a && right is DateTimeOffset b
        ? a.EqualsExact(b) : EqualityComparer<T>.Default.Equals(left, right);

    private static bool OrderIsAppendOnly<T>(IEnumerable<T> baseline, IEnumerable<T> local, IEqualityComparer<T> comparer)
    {
        var baseArray = baseline.ToArray(); var localArray = local.ToArray();
        var baseSet = baseArray.ToHashSet(comparer); var localSet = localArray.ToHashSet(comparer);
        if (!baseArray.Where(localSet.Contains).SequenceEqual(localArray.Where(baseSet.Contains), comparer)) return false;
        var seenNew = false;
        foreach (var item in localArray)
        {
            if (!baseSet.Contains(item)) seenNew = true;
            else if (seenNew) return false;
        }
        return true;
    }

    private static void MergeFolders(ProjectStore baseline, ProjectStore local, ProjectStore remote, ProjectStore merged, List<string> changes, List<string> conflicts)
    {
        var bs = baseline.Folders.ToDictionary(x => x.Id); var ls = local.Folders.ToDictionary(x => x.Id); var rs = remote.Folders.ToDictionary(x => x.Id);
        foreach (var id in bs.Keys.Union(ls.Keys))
        {
            bs.TryGetValue(id, out var b); ls.TryGetValue(id, out var l); rs.TryGetValue(id, out var r);
            if (b is null) { if (l is null) continue; if (r is null) { merged.Folders.Add(Clone(l)); changes.Add($"folder 追加: {l.Name}"); } else if (!Same(l, r)) conflicts.Add($"folder の追加競合: {id}"); continue; }
            if (l is null) { if (r is null) continue; if (!Same(b, r)) conflicts.Add($"folder の削除と変更が競合: {id}"); else { merged.Folders.RemoveAll(f => f.Id == id); changes.Add($"folder 削除: {b.Name}"); } continue; }
            if (Same(b, l)) continue;
            if (r is null) { conflicts.Add($"folder の編集と削除が競合: {id}"); continue; }
            var target = merged.Folders.Single(f => f.Id == id); var before = conflicts.Count;
            target.Name = Field(b.Name, l.Name, r.Name, id.ToString(), "Name", conflicts);
            target.DisplayOrder = Field(b.DisplayOrder, l.DisplayOrder, r.DisplayOrder, id.ToString(), "DisplayOrder", conflicts);
            target.CreatedAt = Field(b.CreatedAt, l.CreatedAt, r.CreatedAt, id.ToString(), "CreatedAt", conflicts);
            target.UpdatedAt = Field(b.UpdatedAt, l.UpdatedAt, r.UpdatedAt, id.ToString(), "UpdatedAt", conflicts);
            if (before == conflicts.Count)
            {
                Describe(b.Name, l.Name, r.Name, id.ToString(), "Name", changes);
                Describe(b.DisplayOrder, l.DisplayOrder, r.DisplayOrder, id.ToString(), "DisplayOrder", changes);
                Describe(b.CreatedAt, l.CreatedAt, r.CreatedAt, id.ToString(), "CreatedAt", changes);
                Describe(b.UpdatedAt, l.UpdatedAt, r.UpdatedAt, id.ToString(), "UpdatedAt", changes);
            }
        }
    }

    private static bool Valid(ProjectStore value)
    {
        try
        {
            if (value.Projects.Any(x => string.IsNullOrWhiteSpace(x.FullPath) || !Path.IsPathFullyQualified(x.FullPath) || x.ProjectId == Guid.Empty)) return false;
            if (value.Projects.Select(x => Path.GetFullPath(x.FullPath)).Distinct(StringComparer.OrdinalIgnoreCase).Count() != value.Projects.Count) return false;
            if (value.Projects.Where(x => x.ProjectId.HasValue).Select(x => x.ProjectId).Distinct().Count() != value.Projects.Count(x => x.ProjectId.HasValue)) return false;
            if (value.Folders.Any(x => x.Id == Guid.Empty || string.IsNullOrWhiteSpace(x.Name)) || value.Folders.Select(x => x.Id).Distinct().Count() != value.Folders.Count) return false;
            var folderIds = value.Folders.Select(x => x.Id).ToHashSet();
            return value.Projects.All(x => x.FolderId is null || folderIds.Contains(x.FolderId.Value));
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException) { return false; }
    }

    private static string LinksFingerprint(ProjectEntry p) => System.Text.Json.JsonSerializer.Serialize(p.LinkedYmmpFiles.Select(l => new { l.Id, l.FilePath, l.DisplayName, l.Role, l.Memo, l.RegisteredAt, l.UpdatedAt, l.LastCheckedAt, l.Exists }).ToArray());
    private static bool Same(ProjectEntry a, ProjectEntry b) => ProjectListRecovery.Fingerprint(new ProjectStore { Projects = [a] }) == ProjectListRecovery.Fingerprint(new ProjectStore { Projects = [b] });
    private static bool Same(ProjectFolder a, ProjectFolder b) => ProjectListRecovery.Fingerprint(new ProjectStore { Folders = [a] }) == ProjectListRecovery.Fingerprint(new ProjectStore { Folders = [b] });
    private static ProjectEntry Clone(ProjectEntry x)
    {
        var copy = new ProjectEntry { FullPath = x.FullPath, ProjectId = x.ProjectId, DisplayName = x.DisplayName, Pinned = x.Pinned, LastAccess = x.LastAccess, FolderId = x.FolderId, ThumbnailCacheDirectory = x.ThumbnailCacheDirectory, ThumbnailSource = x.ThumbnailSource };
        foreach (var link in x.LinkedYmmpFiles) copy.LinkedYmmpFiles.Add(Clone(link));
        return copy;
    }
    private static LinkedYmmpFile Clone(LinkedYmmpFile x) => new() { Id = x.Id, FilePath = x.FilePath, DisplayName = x.DisplayName, Role = x.Role, Memo = x.Memo, RegisteredAt = x.RegisteredAt, LastCheckedAt = x.LastCheckedAt, Exists = x.Exists, UpdatedAt = x.UpdatedAt };
    private static ProjectFolder Clone(ProjectFolder x) => new() { Id = x.Id, Name = x.Name, DisplayOrder = x.DisplayOrder, CreatedAt = x.CreatedAt, UpdatedAt = x.UpdatedAt };
}
