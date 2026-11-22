using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace YMMProjectManager.Infrastructure.Output;

/// <summary>
/// プロジェクトファイルごとのフィルムストリップキャッシュキーを作成します。
/// </summary>
//CSJ,class=FilmstripCacheKeyFactory,フィルムストリップキャッシュキーファクトリー
public static class FilmstripCacheKeyFactory
{
    //CSJ,method=TryCreateHash,ハッシュの作成を試みる
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

        //CSJ,variable=fullPath,完全パス
        var fullPath = Path.GetFullPath(ymmpPath);
        var lastWriteUtc = File.GetLastWriteTimeUtc(fullPath);
        // パスだけでなく更新時刻も含め、同じファイルが保存し直された場合に別キャッシュへ切り替える。
        var input = $"{fullPath.ToUpperInvariant()}|{lastWriteUtc.Ticks}";
        //CSJ,variable=bytes,バイト列
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(input));
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }
}

