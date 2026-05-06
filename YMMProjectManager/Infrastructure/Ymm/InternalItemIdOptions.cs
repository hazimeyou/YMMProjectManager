using System;
using System.Security.Cryptography;
using System.Text;

namespace YMMProjectManager.Infrastructure.Ymm;

public sealed class InternalItemIdOptions
{
    public bool NormalizeTextWhitespace { get; set; } = true;
    public bool NormalizeFilePathCase { get; set; } = true;
    public int FrameBucketSize { get; set; } = 5;
}
