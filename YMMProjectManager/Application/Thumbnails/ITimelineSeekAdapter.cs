using YMMProjectManager.Infrastructure.Thumbnails;

namespace YMMProjectManager.Application.Thumbnails;

public interface ITimelineSeekAdapter
{
    Task<SeekResult> SeekAsync(object? timeline, int targetFrame, CancellationToken cancellationToken);
}
