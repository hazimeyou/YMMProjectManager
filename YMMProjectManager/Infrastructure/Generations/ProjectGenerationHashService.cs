using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace YMMProjectManager.Infrastructure.Generations;

public sealed class ProjectGenerationHashService
{
    public string ComputeProjectId(string projectPath)
    {
        var normalized = NormalizePath(projectPath);
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(normalized));
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }

    public string ComputeFileSha256(string filePath)
    {
        using var stream = File.OpenRead(filePath);
        var bytes = SHA256.HashData(stream);
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }

    public static string NormalizePath(string path)
    {
        var fullPath = Path.GetFullPath(path);
        fullPath = Path.TrimEndingDirectorySeparator(fullPath);
        return fullPath.ToUpperInvariant();
    }
}
