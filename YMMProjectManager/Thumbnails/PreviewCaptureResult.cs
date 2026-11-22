using System.Windows.Media.Imaging;

namespace YMMProjectManager.Application.Thumbnails;

/// <summary>
/// プレビューから取得したビットマップと、失敗時の理由をまとめた結果です。
/// </summary>
//CSJ,record=PreviewCaptureResult,プレビューキャプチャ結果
public sealed record PreviewCaptureResult
{
    //CSJ,property=Success,成功
    public bool Success { get; init; }
    //CSJ,property=Bitmap,ビットマップ
    public BitmapSource? Bitmap { get; init; }
    //CSJ,property=BitmapType,ビットマップ型
    public string? BitmapType { get; init; }
    //CSJ,property=FailureReason,失敗理由
    public string? FailureReason { get; init; }

    /// <summary>
    /// 取得に成功したビットマップを結果として包みます。
    /// </summary>
    //CSJ,method=Succeeded,成功
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
    //CSJ,method=Failed,失敗
    public static PreviewCaptureResult Failed(string reason)
        => new()
        {
            Success = false,
            FailureReason = reason,
        };
}
