using System;
using System.Globalization;
using System.Security.Cryptography;

namespace YMMProjectManager.Infrastructure.Generations;

//CSJ,class=ProjectGenerationIdFactory,プロジェクト世代ID発行
public sealed class ProjectGenerationIdFactory
{
    //CSJ,method=Create,作成する
    public string Create(DateTimeOffset createdAt)
    {
        //CSJ,variable=buffer,バッファー
        Span<byte> buffer = stackalloc byte[4];
        RandomNumberGenerator.Fill(buffer);
        //CSJ,variable=suffix,接尾辞
        var suffix = Convert.ToHexString(buffer).ToLowerInvariant();
        return $"{createdAt.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture)}_{suffix}";
    }
}
