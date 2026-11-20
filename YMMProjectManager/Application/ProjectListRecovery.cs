using System;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text.Json;

namespace YMMProjectManager.Application;

internal static class ProjectListRecovery
{
    // Only persisted fields belong in this snapshot; thumbnail UI state is intentionally excluded.
    internal static string Fingerprint(ProjectStore store, string? ignoreIdentityAtPath = null)
    {
        var data = new
        {
            Folders = store.Folders.Select(f => new { f.Id, f.Name, f.DisplayOrder, f.CreatedAt, f.UpdatedAt }).ToArray(),
            Projects = store.Projects.Select(p => new
            {
                p.FullPath, ProjectId = string.Equals(p.FullPath, ignoreIdentityAtPath, StringComparison.OrdinalIgnoreCase) ? null : p.ProjectId, p.DisplayName, p.Pinned, p.LastAccess, p.FolderId,
                p.MetadataAssociation, p.FileIdentity,
                Links = p.LinkedYmmpFiles.Select(l => new { l.Id, l.FilePath, l.DisplayName, l.Role, l.Memo, l.RegisteredAt, l.UpdatedAt, l.LastCheckedAt, l.Exists }).ToArray(),
            }).ToArray(),
        };
        return Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(data)));
    }

    internal static void ApplyIdentity(ProjectStore latest, string path, Guid identity)
    {
        if (identity == Guid.Empty || latest.Revision is null || !latest.IsRecoverySafe) throw new InvalidDataException("最新一覧に未知の項目があるか ID が不正なため回復できません。");
        var matches = latest.Projects.Where(p => string.Equals(p.FullPath, path, StringComparison.OrdinalIgnoreCase)).ToArray();
        if (matches.Length != 1) throw new InvalidDataException("対象の一覧項目が見つからないか重複しています。");
        if (matches[0].ProjectId is not null && matches[0].ProjectId != identity) throw new InvalidDataException("対象項目の ID が別の値に変更されました。");
        if (latest.Projects.Any(p => !ReferenceEquals(p, matches[0]) && p.ProjectId == identity)) throw new InvalidDataException("ID が別の項目で使用されています。");
        matches[0].ProjectId = identity;
    }
}
