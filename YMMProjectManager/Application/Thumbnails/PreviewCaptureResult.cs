using System.Windows.Media.Imaging;

namespace YMMProjectManager.Application.Thumbnails;

public sealed record PreviewCaptureResult
{
    public bool Success { get; init; }
    public BitmapSource? Bitmap { get; init; }
    public string? BitmapType { get; init; }
    public string? FailureReason { get; init; }

    public static PreviewCaptureResult Succeeded(BitmapSource bitmap, string bitmapType)
        => new()
        {
            Success = true,
            Bitmap = bitmap,
            BitmapType = bitmapType,
        };

    public static PreviewCaptureResult Failed(string reason)
        => new()
        {
            Success = false,
            FailureReason = reason,
        };
}
