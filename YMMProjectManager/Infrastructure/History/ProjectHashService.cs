
namespace YMMProjectManager.Infrastructure.History;

public static class ProjectHashService
{
    public static string ComputeProjectKey(string fullPath)
    {
        var normalized = Path.GetFullPath(fullPath).Trim().ToLowerInvariant();
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(normalized));
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }
}
