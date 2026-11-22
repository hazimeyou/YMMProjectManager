using System;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text.Json;
using YMMProjectManager.Domain;

namespace YMMProjectManager.Application;

//CSJ,class=ProjectListRecovery,プロジェクト一覧復旧
internal static class ProjectListRecovery
{
    // Only persisted fields belong in this snapshot; thumbnail UI state is intentionally excluded.
    //CSJ,method=Fingerprint,フィンガープリント
    internal static string Fingerprint(ProjectStore store, string? ignoreIdentityAtPath = null)
    {
        //CSJ,variable=data,データ
        var data = new
        {
            Folders = store.Folders.Select(f => new { f.Id, f.Name, f.DisplayOrder, f.CreatedAt, f.UpdatedAt }).ToArray(),
            Projects = store.Projects.Select(p => new
            {
                p.FullPath, ProjectId = string.Equals(p.FullPath, ignoreIdentityAtPath, StringComparison.OrdinalIgnoreCase) ? null : p.ProjectId, p.DisplayName, p.Pinned, p.LastAccess, p.FolderId,
                p.MetadataAssociation, FileIdentity = string.Equals(p.FullPath, ignoreIdentityAtPath, StringComparison.OrdinalIgnoreCase) ? null : p.FileIdentity,
                Links = p.LinkedYmmpFiles.Select(l => new { l.Id, l.FilePath, l.DisplayName, l.Role, l.Memo, l.RegisteredAt, l.UpdatedAt, l.LastCheckedAt, l.Exists }).ToArray(),
            }).ToArray(),
        };
        return Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(data)));
    }

    //CSJ,method=ApplyIdentity,識別情報を適用する
    internal static void ApplyIdentity(ProjectStore latest, string path, Guid identity, ProjectFileIdentity? evidence = null)
    {
        if (identity == Guid.Empty || latest.Revision is null || !latest.IsRecoverySafe) throw new InvalidDataException("最新一覧に未知の項目があるか ID が不正なため回復できません。");
        //CSJ,variable=matches,一致項目
        var matches = latest.Projects.Where(p => string.Equals(p.FullPath, path, StringComparison.OrdinalIgnoreCase)).ToArray();
        if (matches.Length != 1) throw new InvalidDataException("対象の一覧項目が見つからないか重複しています。");
        if (matches[0].ProjectId is not null && matches[0].ProjectId != identity) throw new InvalidDataException("対象項目の ID が別の値に変更されました。");
        if (latest.Projects.Any(p => !ReferenceEquals(p, matches[0]) && p.ProjectId == identity)) throw new InvalidDataException("ID が別の項目で使用されています。");
        if (evidence is not null)
        {
            if ((matches[0].FileIdentity is { } present && present != evidence) ||
                latest.Projects.Any(project => !ReferenceEquals(project, matches[0]) && project.FileIdentity?.IsSameFile(evidence) == true))
                throw new InvalidDataException("ファイル識別情報が変わったか、別の項目に登録されています。");
            matches[0].FileIdentity = evidence;
        }
        matches[0].ProjectId = identity;
    }
}
