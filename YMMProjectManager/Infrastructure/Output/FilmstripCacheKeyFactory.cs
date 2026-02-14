using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace YMMProjectManager.Infrastructure.Output;

public static class FilmstripCacheKeyFactory
{
    public static string? TryCreateHash(string? ymmpPath)
    {
        if (string.IsNullOrWhiteSpace(ymmpPath))
        {
            return null;
        }

        if (!File.Exists(ymmpPath))
        {
            return null;
        }

        var fullPath = Path.GetFullPath(ymmpPath);
        var lastWriteUtc = File.GetLastWriteTimeUtc(fullPath);
        var input = $"{fullPath.ToUpperInvariant()}|{lastWriteUtc.Ticks}";
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(input));
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }
}

