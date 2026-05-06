
namespace YMMProjectManager.Infrastructure.Ymm;

public sealed class InternalItemIdService
{
    private static readonly Regex WsRegex = new("\\s+", RegexOptions.Compiled);
    private readonly InternalItemIdOptions options;

    public InternalItemIdService(InternalItemIdOptions? options = null)
    {
        this.options = options ?? new InternalItemIdOptions();
    }

    public InternalItemIdentity BuildIdentity(YmmItemModel item)
    {
        var type = item.Type ?? string.Empty;
        var timeline = item.TimelineIndex;
        var frame = item.Frame;
        var layer = item.Layer;
        var length = item.Length;
        var text = NormalizeText(item.Text);
        var filePath = NormalizeFilePath(item.FilePath);
        var approximateFrame = options.FrameBucketSize <= 1
            ? frame
            : (frame / options.FrameBucketSize) * options.FrameBucketSize;

        var raw = string.Join("|", type, timeline, text, filePath, approximateFrame);
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(raw));
        var id = Convert.ToHexString(hash).ToLowerInvariant();

        return new InternalItemIdentity
        {
            InternalId = id,
            Type = type,
            TimelineIndex = timeline,
            Layer = layer,
            Frame = frame,
            Length = length,
            Text = item.Text,
            FilePath = item.FilePath,
        };
    }

    private string NormalizeText(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return string.Empty;
        }

        var value = text.Trim();
        if (options.NormalizeTextWhitespace)
        {
            value = WsRegex.Replace(value, " ");
        }

        return value;
    }

    private string NormalizeFilePath(string? filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath))
        {
            return string.Empty;
        }

        var value = filePath.Trim().Replace('\\', '/');
        return options.NormalizeFilePathCase ? value.ToLowerInvariant() : value;
    }
}
