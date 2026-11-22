using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using YMMProjectManager.Domain;

namespace YMMProjectManager.Application;

//CSJ,record=ProjectListConflict,プロジェクト一覧競合
internal sealed record ProjectListConflict(string Item, string Field, string Local, string Remote);
//CSJ,record=ProjectListMergeResult,プロジェクト一覧マージ結果
internal sealed record ProjectListMergeResult(ProjectStore? Store, IReadOnlyList<string> Changes, IReadOnlyList<string> Conflicts)
{
    internal IReadOnlyList<ProjectListConflict> Choices { get; init; } = [];
    internal bool CanResolveAll => Conflicts.Count > 0 && Conflicts.Count == Choices.Count;
}

// Index rows by known logical identity across moves, and reject ambiguous legacy moves.
//CSJ,class=ProjectListMerge,プロジェクト一覧の三方向マージ
internal static class ProjectListMerge
{
    //CSJ,method=Snapshot,スナップショット
    internal static ProjectStore Snapshot(ProjectStore source) => new()
    {
        Revision = source.Revision, IsRecoverySafe = source.IsRecoverySafe,
        Projects = source.Projects.Select(Clone).ToList(), Folders = source.Folders.Select(Clone).ToList(),
    };

    //CSJ,method=Merge,マージする
    internal static ProjectListMergeResult Merge(ProjectStore baseline, ProjectStore local, ProjectStore remote,
        IReadOnlyDictionary<ProjectListConflict, bool>? useLocal = null)
    {
        var conflicts = new List<string>(); var changes = new List<string>();
        var choices = new List<ProjectListConflict>();
        if (!baseline.IsRecoverySafe || !local.IsRecoverySafe || !remote.IsRecoverySafe || remote.Revision is null)
            return new(null, changes, ["一覧に未知・不正な項目があるか、最新の版がありません。"]);
        if (!Valid(baseline) || !Valid(local) || !Valid(remote))
            return new(null, changes, ["パス、project ID、ファイル識別情報、folder ID の重複または不正があります。"]);
        //CSJ,variable=originalPaths,元のパス一覧
        var originalPaths = baseline.Projects.Where(project => project.ProjectId.HasValue)
            .ToDictionary(project => project.ProjectId!.Value, project => project.FullPath);
        //CSJ,method=Key,キー
        string Key(ProjectEntry project) => project.ProjectId is { } id && originalPaths.TryGetValue(id, out var original)
            ? original : project.FullPath;
        //CSJ,variable=basePaths,基点パス一覧
        var basePaths = baseline.Projects.Select(p => p.FullPath).ToHashSet(StringComparer.OrdinalIgnoreCase);
        bool AmbiguousLegacyMove(ProjectStore side) => baseline.Projects.Any(p => p.ProjectId is null && !side.Projects.Any(x => string.Equals(x.FullPath, p.FullPath, StringComparison.OrdinalIgnoreCase))) &&
            side.Projects.Any(p => !basePaths.Contains(p.FullPath));
        if (AmbiguousLegacyMove(local) || AmbiguousLegacyMove(remote)) conflicts.Add("ID のない項目の移動と追加・削除を区別できません。");
        if (new[] { local, remote }.Any(store => store.Projects.Select(Key).Distinct(StringComparer.OrdinalIgnoreCase).Count() != store.Projects.Count))
            conflicts.Add("移動前のパスが別の登録に使われているため、対象を区別できません。");
        if (conflicts.Count != 0) return new(null, changes, conflicts);
        var merged = Snapshot(remote);
        //CSJ,variable=baseProjects,基点プロジェクト一覧
        var baseProjects = baseline.Projects.ToDictionary(p => p.FullPath, StringComparer.OrdinalIgnoreCase);
        //CSJ,variable=localProjects,ローカルプロジェクト一覧
        var localProjects = local.Projects.ToDictionary(Key, StringComparer.OrdinalIgnoreCase);
        //CSJ,variable=remoteProjects,リモートプロジェクト一覧
        var remoteProjects = remote.Projects.ToDictionary(Key, StringComparer.OrdinalIgnoreCase);
        //CSJ,variable=key,キー
        foreach (var key in baseProjects.Keys.Union(localProjects.Keys, StringComparer.OrdinalIgnoreCase))
        {
            baseProjects.TryGetValue(key, out var b); localProjects.TryGetValue(key, out var l); remoteProjects.TryGetValue(key, out var r);
            if (b is null)
            {
                if (l is null) continue;
                if (l.ProjectId is null) { conflicts.Add($"ID のない新規項目は自動再適用できません: {key}"); continue; }
                if (r is null) { merged.Projects.Add(Clone(l)); changes.Add($"追加: {key} (ID: {l.ProjectId})"); }
                else if (!Same(l, r) && Decide(key, "同じパスへの追加", ProjectDetails(l), ProjectDetails(r), conflicts, choices, useLocal) is true)
                {
                    merged.Projects[merged.Projects.FindIndex(p => string.Equals(Key(p), key, StringComparison.OrdinalIgnoreCase))] = Clone(l);
                    changes.Add($"未保存の登録を採用: {key}");
                }
                continue;
            }
            if (b.ProjectId is null)
            {
                if ((l is not null && Same(b, l)) || (l is null && r is null)) continue;
                if (l?.ProjectId is not null)
                {
                    conflicts.Add($"ID 設定には専用の競合回復を使用してください: {key}");
                    continue;
                }
                if (r is null || !IsLegacyRegistrationAt(b.FullPath, r) || (l is not null && !IsLegacyRegistrationAt(b.FullPath, l)))
                {
                    conflicts.Add($"ID のない一覧項目を確実に対応付けられません: {key}");
                    continue;
                }
                // This compares an existing list registration. It never assigns a physical/logical identity.
                if (!Same(b, r) && Decide(key, "ID 未設定の登録内容", l is null ? "登録を削除" : ProjectDetails(l),
                    ProjectDetails(r), conflicts, choices, useLocal) is not true) continue;
                //CSJ,variable=index,インデックス
                var index = merged.Projects.FindIndex(project => string.Equals(Key(project), key, StringComparison.OrdinalIgnoreCase));
                if (l is null) merged.Projects.RemoveAt(index);
                else merged.Projects[index] = Clone(l);
                changes.Add(l is null ? $"ID 未設定の登録を削除: {key}" : $"ID 未設定の登録を採用: {ProjectDetails(l)}");
                continue;
            }
            if (l is null)
            {
                if (r is null) continue;
                if (Same(b, r) || Decide(key, "削除と外部の編集・移動", "登録を削除", ProjectDetails(r), conflicts, choices, useLocal) is true)
                {
                    merged.Projects.RemoveAll(p => string.Equals(Key(p), key, StringComparison.OrdinalIgnoreCase));
                    changes.Add($"削除: {key}");
                }
                continue;
            }
            if (Same(b, l)) continue;
            if (b.ProjectId != l.ProjectId) { conflicts.Add($"ID 設定には専用の競合回復を使用してください: {key}"); continue; }
            if (r is null)
            {
                if (Decide(key, "編集・移動と外部の削除", ProjectDetails(l), "登録を削除", conflicts, choices, useLocal) is true)
                {
                    merged.Projects.Add(Clone(l));
                    changes.Add($"編集・移動した登録を採用: {l.FullPath}");
                }
                continue;
            }
            if (b.ProjectId != r.ProjectId) { conflicts.Add($"外部で project ID が変更されたため再適用できません: {key}"); continue; }
            var target = merged.Projects.Single(p => string.Equals(Key(p), key, StringComparison.OrdinalIgnoreCase));
            var before = conflicts.Count;
            target.DisplayName = Field(b.DisplayName, l.DisplayName, r.DisplayName, key, "DisplayName", conflicts, choices, useLocal);
            target.Pinned = Field(b.Pinned, l.Pinned, r.Pinned, key, "Pinned", conflicts, choices, useLocal);
            target.LastAccess = Field(b.LastAccess, l.LastAccess, r.LastAccess, key, "LastAccess", conflicts, choices, useLocal);
            target.FolderId = Field(b.FolderId, l.FolderId, r.FolderId, key, "FolderId", conflicts, choices, useLocal);
            var bLinks = LinksFingerprint(b); var lLinks = LinksFingerprint(l); var rLinks = LinksFingerprint(r);
            if (b.FullPath != l.FullPath || b.FullPath != r.FullPath)
            {
                // A destination must stay associated with its own physical evidence, metadata and links.
                //CSJ,variable=baseBinding,基点バインディング
                var baseBinding = BindingDetails(b, bLinks);
                //CSJ,variable=localBinding,ローカルバインディング
                var localBinding = BindingDetails(l, lLinks);
                //CSJ,variable=remoteBinding,リモートバインディング
                var remoteBinding = BindingDetails(r, rLinks);
                if (localBinding != baseBinding && localBinding != remoteBinding &&
                    (remoteBinding == baseBinding || Decide(key, "移動先と関連付け", localBinding, remoteBinding, conflicts, choices, useLocal) is true))
                {
                    target.FullPath = l.FullPath;
                    target.MetadataAssociation = l.MetadataAssociation;
                    target.FileIdentity = l.FileIdentity;
                    CopyLinks(l, target);
                }
            }
            else
            {
                target.MetadataAssociation = Field(b.MetadataAssociation, l.MetadataAssociation, r.MetadataAssociation, key, "MetadataAssociation", conflicts, choices, useLocal);
                target.FileIdentity = Field(b.FileIdentity, l.FileIdentity, r.FileIdentity, key, "FileIdentity", conflicts, choices, useLocal);
                if (lLinks != bLinks && lLinks != rLinks)
                {
                    if (rLinks == bLinks) CopyLinks(l, target);
                    else MergeLinks(b, l, r, target, key, changes, conflicts, choices, useLocal);
                }
            }
            if (before == conflicts.Count)
            {
                Describe(b.FullPath, l.FullPath, r.FullPath, key, "FullPath", changes);
                Describe(b.DisplayName, l.DisplayName, r.DisplayName, key, "DisplayName", changes);
                Describe(b.Pinned, l.Pinned, r.Pinned, key, "Pinned", changes);
                Describe(b.LastAccess, l.LastAccess, r.LastAccess, key, "LastAccess", changes);
                Describe(b.FolderId, l.FolderId, r.FolderId, key, "FolderId", changes);
                Describe(b.MetadataAssociation, l.MetadataAssociation, r.MetadataAssociation, key, "MetadataAssociation", changes);
                Describe(b.FileIdentity, l.FileIdentity, r.FileIdentity, key, "FileIdentity", changes);
                Describe(bLinks, lLinks, rLinks, key, "LinkedYmmpFiles", changes);
            }
        }
        MergeFolders(baseline, local, remote, merged, changes, conflicts, choices, useLocal);
        MergeOrder(baseline.Projects, local.Projects, remote.Projects, merged.Projects,
            Key, StringComparer.OrdinalIgnoreCase, "プロジェクト", changes, conflicts, choices, useLocal);
        MergeOrder(baseline.Folders, local.Folders, remote.Folders, merged.Folders,
            f => f.Id, EqualityComparer<Guid>.Default, "フォルダー", changes, conflicts, choices, useLocal);
        if (!Valid(merged)) conflicts.Add("再適用により project ID、パス、ファイル識別情報または folder ID が重複します。");
        if (conflicts.Count == 0 && LosesExtensionFields(local, remote, merged))
            conflicts.Add("未知の項目を保持したまま選択結果を保存できません。両方の一覧を退避してください。");
        return conflicts.Count == 0 ? new(merged, changes, conflicts) : new(null, changes, conflicts) { Choices = choices };
    }

    private static bool IsLegacyRegistrationAt(string path, ProjectEntry project) =>
        project.ProjectId is null && project.FileIdentity is null && project.MetadataAssociation is null &&
        string.Equals(path, project.FullPath, StringComparison.Ordinal);

    private static bool LosesExtensionFields(ProjectStore local, ProjectStore remote, ProjectStore merged)
    {
        //CSJ,variable=localFields,ローカルフィールド一覧
        var localFields = local.Revision?.PreservedFieldAnchors;
        //CSJ,variable=remoteFields,リモートフィールド一覧
        var remoteFields = remote.Revision?.PreservedFieldAnchors;
        if (localFields is null && remoteFields is null) return false;
        var mergedAnchors = ProjectListFieldAnchors.Enumerate(merged).ToHashSet(StringComparer.Ordinal);
        if (remoteFields is not null && remoteFields.Split(';').Any(anchor => !mergedAnchors.Contains(anchor))) return true;
        if (localFields is null) return false;
        var remoteAnchors = ProjectListFieldAnchors.Enumerate(remote).ToHashSet(StringComparer.Ordinal);
        return localFields.Split(';').Any(anchor => mergedAnchors.Contains(anchor) && !remoteAnchors.Contains(anchor));
    }

    //CSJ,method=MergeLinks,リンク一覧をマージする
    private static void MergeLinks(ProjectEntry baseline, ProjectEntry local, ProjectEntry remote, ProjectEntry target, string key,
        //CSJ,parameter=changes,変更一覧
        List<string> changes, List<string> conflicts, List<ProjectListConflict> choices, IReadOnlyDictionary<ProjectListConflict, bool>? useLocal)
    {
        var original = baseline.LinkedYmmpFiles.ToList();
        var edited = local.LinkedYmmpFiles.ToList();
        //CSJ,variable=latest,最新
        var latest = remote.LinkedYmmpFiles.ToList();
        var merged = target.LinkedYmmpFiles.ToList();
        var bs = original.ToDictionary(link => link.Id);
        var ls = edited.ToDictionary(link => link.Id);
        var rs = latest.ToDictionary(link => link.Id);
        foreach (var id in bs.Keys.Union(ls.Keys))
        {
            bs.TryGetValue(id, out var b);
            ls.TryGetValue(id, out var l);
            rs.TryGetValue(id, out var r);
            //CSJ,variable=linkKey,リンクキー
            var linkKey = $"{key} / link {id}";
            if (b is null)
            {
                if (l is null) continue;
                if (r is null) merged.Add(Clone(l));
                else if (!SameLink(l, r) && Decide(linkKey, "リンクの追加", LinkDetails(l), LinkDetails(r), conflicts, choices, useLocal) is true)
                    merged[merged.FindIndex(link => link.Id == id)] = Clone(l);
                continue;
            }
            if (l is null)
            {
                if (r is not null && (SameLink(b, r) ||
                    Decide(linkKey, "リンクの削除と変更", "リンクを削除", LinkDetails(r), conflicts, choices, useLocal) is true))
                    merged.RemoveAll(link => link.Id == id);
                continue;
            }
            if (SameLink(b, l)) continue;
            if (r is null)
            {
                if (Decide(linkKey, "リンクの変更と削除", LinkDetails(l), "リンクを削除", conflicts, choices, useLocal) is true)
                    merged.Add(Clone(l));
                continue;
            }
            MergeLink(b, l, r, merged.Single(link => link.Id == id), linkKey, conflicts, choices, useLocal);
        }
        MergeOrder(original, edited, latest, merged, link => link.Id, EqualityComparer<Guid>.Default,
            $"{key} のリンク", changes, conflicts, choices, useLocal);
        target.LinkedYmmpFiles.Clear();
        //CSJ,variable=link,リンク
        foreach (var link in merged) target.LinkedYmmpFiles.Add(link);
    }

    //CSJ,method=MergeLink,リンクをマージする
    private static void MergeLink(LinkedYmmpFile baseline, LinkedYmmpFile local, LinkedYmmpFile remote, LinkedYmmpFile target,
        //CSJ,parameter=key,キー
        string key, List<string> conflicts, List<ProjectListConflict> choices, IReadOnlyDictionary<ProjectListConflict, bool>? useLocal)
    {
        target.DisplayName = Field(baseline.DisplayName, local.DisplayName, remote.DisplayName, key, "DisplayName", conflicts, choices, useLocal);
        target.Role = Field(baseline.Role, local.Role, remote.Role, key, "Role", conflicts, choices, useLocal);
        target.Memo = Field(baseline.Memo, local.Memo, remote.Memo, key, "Memo", conflicts, choices, useLocal);
        target.RegisteredAt = Field(baseline.RegisteredAt, local.RegisteredAt, remote.RegisteredAt, key, "RegisteredAt", conflicts, choices, useLocal);
        if (baseline.FilePath != local.FilePath || baseline.FilePath != remote.FilePath)
        {
            //CSJ,method=DescribeFile,ファイルを説明する
            static string DescribeFile(LinkedYmmpFile link) => System.Text.Json.JsonSerializer.Serialize(new { link.FilePath, link.LastCheckedAt, link.Exists });
            var b = DescribeFile(baseline); var l = DescribeFile(local); var r = DescribeFile(remote);
            if (l != b && l != r && (r == b || Decide(key, "リンク先と存在確認", l, r, conflicts, choices, useLocal) is true))
            {
                target.FilePath = local.FilePath;
                target.LastCheckedAt = local.LastCheckedAt;
                target.Exists = local.Exists;
            }
        }
        else
        {
            target.LastCheckedAt = Field(baseline.LastCheckedAt, local.LastCheckedAt, remote.LastCheckedAt, key, "LastCheckedAt", conflicts, choices, useLocal);
            target.Exists = Field(baseline.Exists, local.Exists, remote.Exists, key, "Exists", conflicts, choices, useLocal);
        }
        target.UpdatedAt = local.UpdatedAt >= remote.UpdatedAt ? local.UpdatedAt : remote.UpdatedAt;
    }

    //CSJ,method=LinkDetails,リンク詳細
    private static string LinkDetails(LinkedYmmpFile link) => System.Text.Json.JsonSerializer.Serialize(new
    {
        link.Id, link.FilePath, link.DisplayName, link.Role, link.Memo, link.RegisteredAt, link.UpdatedAt, link.LastCheckedAt, link.Exists,
    });

    //CSJ,method=SameLink,同一リンク
    private static bool SameLink(LinkedYmmpFile left, LinkedYmmpFile right) => LinkDetails(left) == LinkDetails(right);

    //CSJ,method=Field,フィールド
    private static T Field<T>(T b, T l, T r, string key, string name, List<string> conflicts,
        List<ProjectListConflict> choices, IReadOnlyDictionary<ProjectListConflict, bool>? useLocal)
    {
        if (Equal(l, b)) return r;
        if (Equal(r, b) || Equal(l, r)) return l;
        return Decide(key, name, System.Text.Json.JsonSerializer.Serialize(l), System.Text.Json.JsonSerializer.Serialize(r),
            conflicts, choices, useLocal) is true ? l : r;
    }

    private static bool? Decide(string key, string field, string local, string remote, List<string> conflicts,
        List<ProjectListConflict> choices, IReadOnlyDictionary<ProjectListConflict, bool>? useLocal)
    {
        var choice = new ProjectListConflict(key, field, local, remote);
        if (useLocal is not null && useLocal.TryGetValue(choice, out var selected)) return selected;
        choices.Add(choice);
        conflicts.Add($"{key}: {field} が競合しています。");
        return null;
    }

    //CSJ,method=ProjectDetails,プロジェクト詳細
    private static string ProjectDetails(ProjectEntry p) => System.Text.Json.JsonSerializer.Serialize(new
    {
        p.FullPath, p.ProjectId, p.DisplayName, p.Pinned, p.LastAccess, p.FolderId, p.MetadataAssociation, p.FileIdentity,
        LinkedYmmpFiles = System.Text.Json.JsonSerializer.Deserialize<System.Text.Json.JsonElement>(LinksFingerprint(p)),
    });

    //CSJ,method=BindingDetails,バインディング詳細
    private static string BindingDetails(ProjectEntry project, string links) => System.Text.Json.JsonSerializer.Serialize(new
    {
        project.FullPath, project.MetadataAssociation, project.FileIdentity,
        LinkedYmmpFiles = System.Text.Json.JsonSerializer.Deserialize<System.Text.Json.JsonElement>(links),
    });

    //CSJ,method=CopyLinks,リンク一覧をコピーする
    private static void CopyLinks(ProjectEntry source, ProjectEntry target)
    {
        target.LinkedYmmpFiles.Clear();
        //CSJ,variable=link,リンク
        foreach (var link in source.LinkedYmmpFiles) target.LinkedYmmpFiles.Add(Clone(link));
    }

    //CSJ,method=Describe,説明する
    private static void Describe<T>(T b, T l, T r, string key, string name, List<string> changes)
    {
        if (Equal(b, l)) return;
        changes.Add($"{key} / {name}\n  基準: {System.Text.Json.JsonSerializer.Serialize(b)}\n  未保存: {System.Text.Json.JsonSerializer.Serialize(l)}\n  最新: {System.Text.Json.JsonSerializer.Serialize(r)}");
    }

    private static bool Equal<T>(T left, T right) => left is DateTimeOffset a && right is DateTimeOffset b
        ? a.EqualsExact(b) : EqualityComparer<T>.Default.Equals(left, right);

    //CSJ,method=MergeOrder,順序をマージする
    private static void MergeOrder<T, TKey>(List<T> baseline, List<T> local, List<T> remote, List<T> merged,
        //CSJ,parameter=key,キー
        Func<T, TKey> key, IEqualityComparer<TKey> comparer, string label, List<string> changes, List<string> conflicts,
        List<ProjectListConflict> choices, IReadOnlyDictionary<ProjectListConflict, bool>? useLocal)
        where TKey : notnull
    {
        var baseArray = baseline.Select(key).ToArray();
        var localArray = local.Select(key).ToArray();
        var remoteArray = remote.Select(key).ToArray();
        //CSJ,variable=baseSet,基点設定
        var baseSet = baseArray.ToHashSet(comparer); var localSet = localArray.ToHashSet(comparer);
        if (localArray.SequenceEqual(remoteArray, comparer)) return;
        if (baseArray.SequenceEqual(remoteArray, comparer))
        {
            // Remote may have field edits even when its sequence is unchanged.
            var byKey = merged.ToDictionary(key, comparer);
            var current = merged.Select(key).ToArray();
            if (baseArray.Where(localSet.Contains).SequenceEqual(localArray, comparer)) return;
            // A delete/edit decision can retain rows outside the local ordering.
            var ordered = localArray.Concat(current).Where(byKey.ContainsKey).Distinct(comparer).ToArray();
            if (current.SequenceEqual(ordered, comparer)) return;
            merged.Clear();
            merged.AddRange(ordered.Select(id => byKey[id]));
            changes.Add($"{label}の並び順を再適用（選んだ順序にない登録は末尾）:\n" + string.Join("\n", ordered));
            return;
        }
        if (!baseArray.Where(localSet.Contains).SequenceEqual(localArray.Where(baseSet.Contains), comparer))
        {
            ChooseOrder();
            return;
        }
        var seenNew = false;
        foreach (var item in localArray)
        {
            if (!baseSet.Contains(item)) seenNew = true;
            else if (seenNew)
            {
                ChooseOrder();
                return;
            }
        }

        void ChooseOrder()
        {
            var useEntered = Decide(label, "順序（選択した側にない登録は末尾）", string.Join("\n", localArray),
                string.Join("\n", remoteArray), conflicts, choices, useLocal);
            if (useEntered is null) return;
            var preferred = useEntered.Value ? localArray : remoteArray;
            var alternative = useEntered.Value ? remoteArray : localArray;
            //CSJ,variable=entries,項目一覧
            var entries = merged.ToDictionary(key, comparer);
            var ordered = preferred.Concat(alternative).Concat(merged.Select(key)).Where(entries.ContainsKey).Distinct(comparer).ToArray();
            merged.Clear();
            merged.AddRange(ordered.Select(id => entries[id]));
            changes.Add($"{label}の順序を確定:\n" + string.Join("\n", ordered));
        }
    }

    //CSJ,method=MergeFolders,フォルダー一覧をマージする
    private static void MergeFolders(ProjectStore baseline, ProjectStore local, ProjectStore remote, ProjectStore merged, List<string> changes,
        List<string> conflicts, List<ProjectListConflict> choices, IReadOnlyDictionary<ProjectListConflict, bool>? useLocal)
    {
        var bs = baseline.Folders.ToDictionary(x => x.Id); var ls = local.Folders.ToDictionary(x => x.Id); var rs = remote.Folders.ToDictionary(x => x.Id);
        foreach (var id in bs.Keys.Union(ls.Keys))
        {
            bs.TryGetValue(id, out var b); ls.TryGetValue(id, out var l); rs.TryGetValue(id, out var r);
            if (b is null)
            {
                if (l is null) continue;
                if (r is null) { merged.Folders.Add(Clone(l)); changes.Add($"folder 追加: {l.Name}"); }
                else if (!Same(l, r) && Decide(id.ToString(), "folder の追加", FolderDetails(l), FolderDetails(r), conflicts, choices, useLocal) is true)
                {
                    merged.Folders[merged.Folders.FindIndex(folder => folder.Id == id)] = Clone(l);
                    changes.Add($"未保存の folder を採用: {l.Name}");
                }
                continue;
            }
            if (l is null)
            {
                if (r is not null && (Same(b, r) ||
                    Decide(id.ToString(), "folder の削除と変更", "folder を削除", FolderDetails(r), conflicts, choices, useLocal) is true))
                {
                    merged.Folders.RemoveAll(folder => folder.Id == id);
                    changes.Add($"folder 削除: {b.Name}");
                }
                continue;
            }
            if (Same(b, l)) continue;
            if (r is null)
            {
                if (Decide(id.ToString(), "folder の変更と削除", FolderDetails(l), "folder を削除", conflicts, choices, useLocal) is true)
                {
                    merged.Folders.Add(Clone(l));
                    changes.Add($"変更した folder を採用: {l.Name}");
                }
                continue;
            }
            var target = merged.Folders.Single(f => f.Id == id); var before = conflicts.Count;
            target.Name = Field(b.Name, l.Name, r.Name, id.ToString(), "Name", conflicts, choices, useLocal);
            target.DisplayOrder = Field(b.DisplayOrder, l.DisplayOrder, r.DisplayOrder, id.ToString(), "DisplayOrder", conflicts, choices, useLocal);
            target.CreatedAt = Field(b.CreatedAt, l.CreatedAt, r.CreatedAt, id.ToString(), "CreatedAt", conflicts, choices, useLocal);
            target.UpdatedAt = Field(b.UpdatedAt, l.UpdatedAt, r.UpdatedAt, id.ToString(), "UpdatedAt", conflicts, choices, useLocal);
            if (before == conflicts.Count)
            {
                Describe(b.Name, l.Name, r.Name, id.ToString(), "Name", changes);
                Describe(b.DisplayOrder, l.DisplayOrder, r.DisplayOrder, id.ToString(), "DisplayOrder", changes);
                Describe(b.CreatedAt, l.CreatedAt, r.CreatedAt, id.ToString(), "CreatedAt", changes);
                Describe(b.UpdatedAt, l.UpdatedAt, r.UpdatedAt, id.ToString(), "UpdatedAt", changes);
            }
        }
    }

    //CSJ,method=FolderDetails,フォルダー詳細
    private static string FolderDetails(ProjectFolder folder) => System.Text.Json.JsonSerializer.Serialize(new
    {
        folder.Id, folder.Name, folder.DisplayOrder, folder.CreatedAt, folder.UpdatedAt,
    });

    //CSJ,method=Valid,有効
    private static bool Valid(ProjectStore value)
    {
        try
        {
            if (value.Projects.Any(x => string.IsNullOrWhiteSpace(x.FullPath) || !Path.IsPathFullyQualified(x.FullPath) || x.ProjectId == Guid.Empty)) return false;
            if (value.Projects.Select(x => Path.GetFullPath(x.FullPath)).Distinct(StringComparer.OrdinalIgnoreCase).Count() != value.Projects.Count) return false;
            if (value.Projects.Where(x => x.ProjectId.HasValue).Select(x => x.ProjectId).Distinct().Count() != value.Projects.Count(x => x.ProjectId.HasValue)) return false;
            //CSJ,variable=fileIds,ファイルID一覧
            var fileIds = new HashSet<(ulong Volume, Guid Id)>();
            if (value.Projects.Any(project => project.FileIdentity is { } file &&
                (file.FileId == Guid.Empty || !fileIds.Add((file.Volume, file.FileId))))) return false;
            if (value.Projects.Any(project => project.LinkedYmmpFiles.Any(link => link.Id == Guid.Empty) ||
                project.LinkedYmmpFiles.Select(link => link.Id).Distinct().Count() != project.LinkedYmmpFiles.Count)) return false;
            if (value.Folders.Any(x => x.Id == Guid.Empty || string.IsNullOrWhiteSpace(x.Name)) || value.Folders.Select(x => x.Id).Distinct().Count() != value.Folders.Count) return false;
            //CSJ,variable=folderIds,フォルダーID一覧
            var folderIds = value.Folders.Select(x => x.Id).ToHashSet();
            return value.Projects.All(x => x.FolderId is null || folderIds.Contains(x.FolderId.Value));
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException) { return false; }
    }

    //CSJ,method=LinksFingerprint,リンク一覧フィンガープリント
    private static string LinksFingerprint(ProjectEntry p) => System.Text.Json.JsonSerializer.Serialize(p.LinkedYmmpFiles.Select(l => new { l.Id, l.FilePath, l.DisplayName, l.Role, l.Memo, l.RegisteredAt, l.UpdatedAt, l.LastCheckedAt, l.Exists }).ToArray());
    //CSJ,method=Same,同一
    private static bool Same(ProjectEntry a, ProjectEntry b) => ProjectListRecovery.Fingerprint(new ProjectStore { Projects = [a] }) == ProjectListRecovery.Fingerprint(new ProjectStore { Projects = [b] });
    //CSJ,method=Same,同一
    private static bool Same(ProjectFolder a, ProjectFolder b) => ProjectListRecovery.Fingerprint(new ProjectStore { Folders = [a] }) == ProjectListRecovery.Fingerprint(new ProjectStore { Folders = [b] });
    //CSJ,method=Clone,クローンする
    private static ProjectEntry Clone(ProjectEntry x)
    {
        //CSJ,variable=copy,コピー
        var copy = new ProjectEntry { FullPath = x.FullPath, ProjectId = x.ProjectId, DisplayName = x.DisplayName, Pinned = x.Pinned, LastAccess = x.LastAccess, FolderId = x.FolderId, ThumbnailCacheDirectory = x.ThumbnailCacheDirectory, ThumbnailSource = x.ThumbnailSource };
        //CSJ,variable=link,リンク
        foreach (var link in x.LinkedYmmpFiles) copy.LinkedYmmpFiles.Add(Clone(link));
        copy.MetadataAssociation = x.MetadataAssociation;
        copy.FileIdentity = x.FileIdentity;
        return copy;
    }
    //CSJ,method=Clone,クローンする
    private static LinkedYmmpFile Clone(LinkedYmmpFile x) => new() { Id = x.Id, FilePath = x.FilePath, DisplayName = x.DisplayName, Role = x.Role, Memo = x.Memo, RegisteredAt = x.RegisteredAt, LastCheckedAt = x.LastCheckedAt, Exists = x.Exists, UpdatedAt = x.UpdatedAt };
    //CSJ,method=Clone,クローンする
    private static ProjectFolder Clone(ProjectFolder x) => new() { Id = x.Id, Name = x.Name, DisplayOrder = x.DisplayOrder, CreatedAt = x.CreatedAt, UpdatedAt = x.UpdatedAt };
}
