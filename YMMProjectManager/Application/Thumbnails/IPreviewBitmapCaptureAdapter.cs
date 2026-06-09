using YMMProjectManager.Infrastructure.Thumbnails;

namespace YMMProjectManager.Application.Thumbnails;

public interface IPreviewBitmapCaptureAdapter
{
    Task<PreviewCaptureResult> TryCaptureAsync(CancellationToken cancellationToken);
}
