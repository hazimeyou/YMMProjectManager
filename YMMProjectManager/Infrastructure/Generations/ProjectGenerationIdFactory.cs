using System;
using System.Globalization;
using System.Security.Cryptography;

namespace YMMProjectManager.Infrastructure.Generations;

public sealed class ProjectGenerationIdFactory
{
    public string Create(DateTimeOffset createdAt)
    {
        Span<byte> buffer = stackalloc byte[4];
        RandomNumberGenerator.Fill(buffer);
        var suffix = Convert.ToHexString(buffer).ToLowerInvariant();
        return $"{createdAt.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture)}_{suffix}";
    }
}
