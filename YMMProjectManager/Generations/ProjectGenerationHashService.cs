using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace YMMProjectManager.Infrastructure.Generations;

//CSJ,class=ProjectGenerationHashService,プロジェクト世代ハッシュサービス
public sealed class ProjectGenerationHashService
{
    //CSJ,method=ComputeProjectId,プロジェクトIDを計算する
    public string ComputeProjectId(string projectPath)
    {
        //CSJ,variable=normalized,正規化済み
        var normalized = NormalizePath(projectPath);
        //CSJ,variable=bytes,バイト列
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(normalized));
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }

    //CSJ,method=ComputeFileSha256,ファイルSHA-256を計算する
    public string ComputeFileSha256(string filePath)
    {
        //CSJ,variable=stream,ストリーム
        using var stream = File.OpenRead(filePath);
        //CSJ,variable=bytes,バイト列
        var bytes = SHA256.HashData(stream);
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }

    //CSJ,method=NormalizePath,パスを正規化する
    public static string NormalizePath(string path)
    {
        //CSJ,variable=fullPath,完全パス
        var fullPath = Path.GetFullPath(path);
        fullPath = Path.TrimEndingDirectorySeparator(fullPath);
        return fullPath.ToUpperInvariant();
    }
}
