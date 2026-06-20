using System.Windows.Media.Imaging;

namespace YMMProjectManager.Application.Thumbnails;

/// <summary>
/// プレビューから取得したビットマップと、失敗時の理由をまとめた結果です。
/// </summary>
public sealed record PreviewCaptureResult
{
    public bool Success { get; init; }
    public BitmapSource? Bitmap { get; init; }
    public string? BitmapType { get; init; }
    public string? FailureReason { get; init; }

    /// <summary>
    /// 取得に成功したビットマップを結果として包みます。
    /// </summary>
    public static PreviewCaptureResult Succeeded(BitmapSource bitmap, string bitmapType)
        => new()
        {
            Success = true,
            Bitmap = bitmap,
            BitmapType = bitmapType,
        };

    /// <summary>
    /// 取得に失敗した理由を結果として包みます。
    /// </summary>
    public static PreviewCaptureResult Failed(string reason)
        => new()
        {
            Success = false,
            FailureReason = reason,
        };
}
