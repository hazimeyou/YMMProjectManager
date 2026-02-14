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

    public JsonProjectRepository(FileLogger logger)
    {
        this.logger = logger;
        jsonPath = ResolvePath();
    }

    public async Task<IReadOnlyList<ProjectEntry>> LoadAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            if (!File.Exists(jsonPath))
            {
                return Array.Empty<ProjectEntry>();
            }

            await using var stream = File.OpenRead(jsonPath);
            var dto = await JsonSerializer.DeserializeAsync<ProjectListFileDto>(stream, JsonOptions, cancellationToken).ConfigureAwait(false);
            if (dto?.Projects is null)
            {
                return Array.Empty<ProjectEntry>();
            }

            return dto.Projects
                .Where(x => !string.IsNullOrWhiteSpace(x.FullPath))
                .Select(x => new ProjectEntry
                {
                    FullPath = x.FullPath!,
                    DisplayName = string.IsNullOrWhiteSpace(x.DisplayName) ? null : x.DisplayName,
                    Pinned = x.Pinned ?? false,
                    LastAccess = x.LastAccess,
                })
                .ToList();
        }
        catch (Exception ex)
        {
            logger.Error("Failed to load projects.json", ex);
            return Array.Empty<ProjectEntry>();
        }
    }

    public async Task SaveAsync(IReadOnlyList<ProjectEntry> projects, CancellationToken cancellationToken = default)
    {
        try
        {
            var dir = Path.GetDirectoryName(jsonPath);
            if (!string.IsNullOrWhiteSpace(dir))
            {
                Directory.CreateDirectory(dir);
            }

            var dto = new ProjectListFileDto
            {
                Projects = projects.Select(x => new ProjectRecordDto
                {
                    FullPath = x.FullPath,
                    DisplayName = string.IsNullOrWhiteSpace(x.DisplayName) ? null : x.DisplayName,
                    Pinned = x.Pinned ? true : null,
                    LastAccess = x.LastAccess,
                }).ToList(),
            };

            await using var stream = File.Create(jsonPath);
            await JsonSerializer.SerializeAsync(stream, dto, JsonOptions, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            logger.Error("Failed to save projects.json", ex);
        }
    }

    private static string ResolvePath()
    {
        var runtimeYmmDir = Environment.GetEnvironmentVariable("YMM4DirPath");
        if (!string.IsNullOrWhiteSpace(runtimeYmmDir))
        {
            return Path.Combine(runtimeYmmDir, "user", "plugin", PluginDirectoryName, "data", "projects.json");
        }

        var userDir = AppDirectories.UserDirectory;
        return Path.Combine(userDir, "plugin", PluginDirectoryName, "data", "projects.json");
    }

    private sealed class ProjectListFileDto
    {
        public List<ProjectRecordDto> Projects { get; set; } = [];
    }

    // DTO schema: fullPath, displayName(optional), pinned(optional), lastAccess(optional)
    private sealed class ProjectRecordDto
    {
        public string? FullPath { get; set; }
        public string? DisplayName { get; set; }
        public bool? Pinned { get; set; }
        public DateTimeOffset? LastAccess { get; set; }
    }
}

