using System.IO;
using System.Security.Cryptography;

namespace YMMProjectManager.Infrastructure;

internal static class AssetContentHash
{
    // The caller holds the file and chooses the starting position.
    internal static async Task<string> ReadSha256Async(Stream content, CancellationToken token) =>
        Convert.ToHexString(await SHA256.HashDataAsync(content, token).ConfigureAwait(false));
}
