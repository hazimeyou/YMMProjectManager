using System.IO;
using System.Security.Cryptography;

namespace YMMProjectManager.Infrastructure;

//CSJ,class=AssetContentHash,素材内容のハッシュ
internal static class AssetContentHash
{
    // The caller holds the file and chooses the starting position.
    //CSJ,method=ReadSha256Async,SHA-256を読み取る（非同期）
    internal static async Task<string> ReadSha256Async(Stream content, CancellationToken token) =>
        Convert.ToHexString(await SHA256.HashDataAsync(content, token).ConfigureAwait(false));
}
