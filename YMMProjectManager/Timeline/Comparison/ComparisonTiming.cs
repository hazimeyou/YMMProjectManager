using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using YMMProjectManager.Infrastructure;

namespace YMMProjectManager.Application.PreviewComparison;

internal readonly record struct ComparisonOffset(TimeSpan Start, TimeSpan Offset);

internal sealed class ComparisonTiming
{
    private const int MaxBytes = 64 * 1024;
    private const int MaxSections = 512;
    private static readonly JsonSerializerOptions Options = new()
    {
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        AllowDuplicateProperties = false,
        WriteIndented = true,
    };

    private sealed record Document(
        [property: JsonRequired] int Version,
        [property: JsonRequired] long DefaultOffsetTicks,
        [property: JsonRequired] Section[] Sections);
    private readonly record struct Section(
        [property: JsonRequired] long StartTicks,
        [property: JsonRequired] long OffsetTicks);

    private readonly ComparisonOffset[] sections;
    internal TimeSpan DefaultOffset { get; }
    internal IReadOnlyList<ComparisonOffset> Sections { get; }

    internal ComparisonTiming(TimeSpan defaultOffset, IReadOnlyList<ComparisonOffset> sections)
    {
        if (sections.Count > MaxSections)
            throw new InvalidDataException("比較の区間が上限を超えています。");
        this.sections = sections.ToArray();
        for (var i = 0; i < this.sections.Length; i++)
        {
            if (this.sections[i].Start < TimeSpan.Zero ||
                (i > 0 && this.sections[i - 1].Start >= this.sections[i].Start))
                throw new InvalidDataException("比較の区間開始は重複なしの昇順で指定してください。");
        }
        Sections = Array.AsReadOnly(this.sections);
        DefaultOffset = defaultOffset;
    }

    internal TimeSpan GetOffset(TimeSpan comparisonTime)
    {
        for (var i = sections.Length - 1; i >= 0; i--)
            if (comparisonTime >= sections[i].Start)
                return sections[i].Offset;
        return DefaultOffset;
    }

    internal TimeSpan? RequestedCurrentTime(TimeSpan comparisonTime) =>
        PreviewComparisonRequest.ResolveCurrentTime(comparisonTime, GetOffset(comparisonTime));

    internal PreviewComparisonRequest Bind(TimeSpan comparisonTime, PreviewSource original, PreviewSource current) =>
        new(comparisonTime, original, current, GetOffset(comparisonTime));

    internal async Task SaveCopyAsync(string path, CancellationToken token)
    {
        path = ComparisonFilePaths.GetLocalFile(path);
        using var parent = OwnedFilePublication.DirectoryFence.Open(Path.GetDirectoryName(path)!, enforceShare: true);
        var data = new Document(1, DefaultOffset.Ticks,
            sections.Select(x => new Section(x.Start.Ticks, x.Offset.Ticks)).ToArray());
        var bytes = JsonSerializer.SerializeToUtf8Bytes(data, Options);
        var staging = Path.Combine(Path.GetDirectoryName(path)!, $".ymmpm-comparison-{Guid.NewGuid():N}.tmp");
        token.ThrowIfCancellationRequested();
        using var file = OwnedFilePublication.CreateOwnedOutputFile(staging);
        try
        {
            await file.WriteAsync(bytes, token).ConfigureAwait(false);
            file.Flush(flushToDisk: true);
            token.ThrowIfCancellationRequested();
            OwnedFilePublication.RenameOpenedPath(file.SafeFileHandle, path);
        }
        catch
        {
            OwnedFilePublication.TryDeleteOpenedPath(file.SafeFileHandle);
            throw;
        }
    }

    internal static async Task<ComparisonTiming> LoadAsync(string path, CancellationToken token)
    {
        path = ComparisonFilePaths.GetLocalFile(path);
        using var parent = OwnedFilePublication.DirectoryFence.Open(Path.GetDirectoryName(path)!, enforceShare: true);
        using var handle = OwnedFilePublication.OpenOrdinaryPath(path, expectDirectory: false, allowRename: false);
        using var file = new FileStream(handle, FileAccess.Read);
        if (file.Length > MaxBytes)
            throw new InvalidDataException("比較設定のサイズが上限を超えています。");
        var data = await JsonSerializer.DeserializeAsync<Document>(file, Options, token).ConfigureAwait(false);
        if (data is not { Version: 1, Sections: not null })
            throw new InvalidDataException("対応する比較設定を読み込めません。");
        return new(TimeSpan.FromTicks(data.DefaultOffsetTicks), data.Sections
            .Select(x => new ComparisonOffset(TimeSpan.FromTicks(x.StartTicks), TimeSpan.FromTicks(x.OffsetTicks))).ToArray());
    }
}
