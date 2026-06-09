namespace YMMProjectManager.Application.Thumbnails;

public interface ICurrentPreviewCaptureService
{
    Task<CurrentPreviewCaptureResult> CaptureAsync(CancellationToken cancellationToken);
}
