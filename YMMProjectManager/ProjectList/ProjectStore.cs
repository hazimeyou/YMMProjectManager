using System.Collections.Generic;
using System.Security.Cryptography;
using System.Text.Json;
using YMMProjectManager.Domain;

namespace YMMProjectManager.Application;

public sealed class ProjectStore
{
    //CSJ,property=Projects,プロジェクト一覧
    public List<ProjectEntry> Projects { get; set; } = [];
    //CSJ,property=Folders,フォルダー一覧
    public List<ProjectFolder> Folders { get; set; } = [];
    //CSJ,property=Revision,版
    public ProjectListRevision? Revision { get; set; }
    //CSJ,property=IsRecoverySafe,復旧安全かどうか
    public bool IsRecoverySafe { get; set; }
}

//CSJ,record=ProjectListRevision,プロジェクト一覧版
public sealed record ProjectListRevision(string? Sha256, Guid? OperationId = null)
{
    // Opaque persistence data for forward-compatible root properties.
    internal string? PreservedRootFieldsJson { get; init; }
    internal string? PreservedNestedFieldsJson { get; init; }
    internal string? PreservedFieldAnchors { get; init; }
}

//CSJ,class=ProjectListFieldAnchors,プロジェクト一覧フィールドの照合キー
internal static class ProjectListFieldAnchors
{
    //CSJ,method=Project,プロジェクト
    internal static string Project(Guid id) => $"Project:{id:N}";
    //CSJ,method=Folder,フォルダー
    internal static string Folder(Guid id) => $"Folder:{id:N}";
    //CSJ,method=Link,リンク
    internal static string Link(Guid projectId, Guid id) => $"Link:{projectId:N}/{id:N}";
    //CSJ,method=Metadata,メタデータ
    internal static string Metadata(Guid id, ProjectMetadataAssociation value) => $"Metadata:{id:N}/{Fingerprint(value)}";
    //CSJ,method=FileIdentity,ファイル識別情報
    internal static string FileIdentity(Guid id, ProjectFileIdentity value) => $"FileIdentity:{id:N}/{Fingerprint(value)}";

    //CSJ,method=Fingerprint,フィンガープリント
    private static string Fingerprint<T>(T value) => Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(value)));


    //CSJ,method=Enumerate,列挙する
    internal static IEnumerable<string> Enumerate(ProjectStore store)
    {
        //CSJ,variable=project,プロジェクト
        foreach (var project in store.Projects)
        {
            if (project.ProjectId is not { } id) continue;
            yield return Project(id);
            if (project.MetadataAssociation is { } metadata) yield return Metadata(id, metadata);
            if (project.FileIdentity is { } identity) yield return FileIdentity(id, identity);
            //CSJ,variable=link,リンク
            foreach (var link in project.LinkedYmmpFiles) yield return Link(id, link.Id);
        }
        //CSJ,variable=folder,フォルダー
        foreach (var folder in store.Folders) yield return Folder(folder.Id);
    }
}
