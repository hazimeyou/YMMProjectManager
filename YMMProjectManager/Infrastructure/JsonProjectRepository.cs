using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
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
    private readonly string jsonPath;
    private readonly FileLogger logger;

    public JsonProjectRepository(FileLogger logger){ this.logger = logger; jsonPath = ResolvePath(); }

    public async Task<ProjectStore> LoadAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            if (!File.Exists(jsonPath)) return new ProjectStore();
            await using var stream = File.OpenRead(jsonPath);
            var dto = await JsonSerializer.DeserializeAsync<ProjectListFileDto>(stream, JsonOptions, cancellationToken).ConfigureAwait(false);
            if (dto is null) return new ProjectStore();
            var store = new ProjectStore();
            foreach (var f in dto.Folders ?? [])
            {
                if (f.Id is null || string.IsNullOrWhiteSpace(f.Name)) continue;
                store.Folders.Add(new ProjectFolder { Id = f.Id.Value, Name = f.Name!, DisplayOrder = f.DisplayOrder ?? 0, CreatedAt = f.CreatedAt ?? DateTimeOffset.Now, UpdatedAt = f.UpdatedAt ?? DateTimeOffset.Now });
            }
            foreach (var x in dto.Projects ?? [])
            {
                if (string.IsNullOrWhiteSpace(x.FullPath)) continue;
                var p = new ProjectEntry { FullPath = x.FullPath!, DisplayName = string.IsNullOrWhiteSpace(x.DisplayName) ? null : x.DisplayName, Pinned = x.Pinned ?? false, LastAccess = x.LastAccess, FolderId = x.FolderId };
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
        catch (Exception ex) { logger.Error("Failed to load projects.json", ex); return new ProjectStore(); }
    }

    public async Task SaveAsync(ProjectStore store, CancellationToken cancellationToken = default)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(jsonPath)!);
            var dto = new ProjectListFileDto
            {
                Folders = store.Folders.Select(x => new ProjectFolderDto { Id = x.Id, Name = x.Name, DisplayOrder = x.DisplayOrder, CreatedAt = x.CreatedAt, UpdatedAt = x.UpdatedAt }).ToList(),
                Projects = store.Projects.Select(x => new ProjectRecordDto
                {
                    FullPath = x.FullPath, DisplayName = string.IsNullOrWhiteSpace(x.DisplayName) ? null : x.DisplayName, Pinned = x.Pinned ? true : null, LastAccess = x.LastAccess, FolderId = x.FolderId,
                    LinkedYmmpFiles = x.LinkedYmmpFiles.Select(l => new LinkedYmmpFileDto { Id = l.Id, FilePath = l.FilePath, DisplayName = l.DisplayName, Role = l.Role.ToString(), Memo = l.Memo, RegisteredAt = l.RegisteredAt, UpdatedAt = l.UpdatedAt, LastCheckedAt = l.LastCheckedAt, Exists = l.Exists }).ToList(),
                }).ToList(),
            };
            await using var stream = File.Create(jsonPath);
            await JsonSerializer.SerializeAsync(stream, dto, JsonOptions, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) { logger.Error("Failed to save projects.json", ex); }
    }

    private static string ResolvePath(){ var runtimeYmmDir = Environment.GetEnvironmentVariable("YMM4DirPath"); if (!string.IsNullOrWhiteSpace(runtimeYmmDir)) return Path.Combine(runtimeYmmDir, "user", "plugin", PluginDirectoryName, "data", "projects.json"); var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData); return Path.Combine(appData, "YMMProjectManager", "plugin", PluginDirectoryName, "data", "projects.json"); }

    private sealed class ProjectListFileDto { public List<ProjectRecordDto> Projects { get; set; } = []; public List<ProjectFolderDto> Folders { get; set; } = []; }
    private sealed class ProjectRecordDto { public string? FullPath { get; set; } public string? DisplayName { get; set; } public bool? Pinned { get; set; } public DateTimeOffset? LastAccess { get; set; } public Guid? FolderId { get; set; } public List<LinkedYmmpFileDto> LinkedYmmpFiles { get; set; } = []; }
    private sealed class LinkedYmmpFileDto { public Guid? Id { get; set; } public string? FilePath { get; set; } public string? DisplayName { get; set; } public string? Role { get; set; } public string? Memo { get; set; } public DateTimeOffset? RegisteredAt { get; set; } public DateTimeOffset? UpdatedAt { get; set; } public DateTimeOffset? LastCheckedAt { get; set; } public bool? Exists { get; set; } }
    private sealed class ProjectFolderDto { public Guid? Id { get; set; } public string? Name { get; set; } public int? DisplayOrder { get; set; } public DateTimeOffset? CreatedAt { get; set; } public DateTimeOffset? UpdatedAt { get; set; } }
}
