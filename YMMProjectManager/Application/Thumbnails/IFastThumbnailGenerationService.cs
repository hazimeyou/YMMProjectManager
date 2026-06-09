namespace YMMProjectManager.Application.Thumbnails;

public interface IFastThumbnailGenerationService
{
    Task<FastThumbnailGenerationResult> GenerateAsync(string ymmpPath, object? timeline, CancellationToken cancellationToken);
}
