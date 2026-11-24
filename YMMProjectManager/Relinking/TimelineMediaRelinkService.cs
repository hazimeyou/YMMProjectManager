using System.IO;
using YukkuriMovieMaker.Plugin;
using YukkuriMovieMaker.Project.Items;

namespace YMMProjectManager.Infrastructure.Relink;

//CSJ,class=TimelineRelinkTarget,タイムライン再リンク対象
public sealed class TimelineRelinkTarget
{
    //CSJ,property=RowIndex,行インデックス
    public int RowIndex { get; init; }
    //CSJ,property=Item,項目
    public required IItem Item { get; init; }
}

//CSJ,class=TimelineRelinkContext,タイムライン再リンクコンテキスト
public sealed class TimelineRelinkContext
{
    //CSJ,property=Info,情報
    public required TimelineToolInfo Info { get; init; }
    //CSJ,property=Targets,対象一覧
    public required List<TimelineRelinkTarget> Targets { get; init; }
    //CSJ,property=Rows,行一覧
    public required List<RelinkRow> Rows { get; init; }
    internal object? ScannedTimeline { get; init; }
}

//CSJ,class=TimelineMediaRelinkService,タイムライン内の素材再リンク
public sealed class TimelineMediaRelinkService
{
    //CSJ,field=logger,ロガー
    private readonly FileLogger logger;

    public TimelineMediaRelinkService(FileLogger logger)
    {
        this.logger = logger;
    }

    //CSJ,method=Scan,走査する
    public (TimelineRelinkContext? Context, RelinkResult Result) Scan(TimelineToolInfo? info)
    {
        var result = new RelinkResult();
        if (info?.Timeline is not { } timeline)
        {
            result.ErrorMessage = "\u30bf\u30a4\u30e0\u30e9\u30a4\u30f3\u60c5\u5831\u3092\u53d6\u5f97\u3067\u304d\u307e\u305b\u3093\u3067\u3057\u305f\u3002";
            return (null, result);
        }

        try
        {
            var targets = new List<TimelineRelinkTarget>();
            //CSJ,variable=rows,行一覧
            var rows = new List<RelinkRow>();

            foreach (var item in timeline.Items)
            {
                if (!TimelineItemFilePathAccessor.TryGetFilePath(item, out var path) || string.IsNullOrWhiteSpace(path))
                {
                    continue;
                }

                var row = new RelinkRow
                {
                    RowIndex = rows.Count,
                    TypeHint = item.GetType().Name,
                    OriginalPath = path,
                    FileName = Path.GetFileName(path) ?? string.Empty,
                    Extension = Path.GetExtension(path) ?? string.Empty,
                };

                if (File.Exists(path))
                {
                    row.Status = RelinkStatus.Existing;
                    row.Message = "\u65e2\u5b58\u30d1\u30b9";
                    result.SkippedCount++;
                }
                else
                {
                    row.Status = RelinkStatus.Missing;
                    row.Message = "\u30ea\u30f3\u30af\u5207\u308c";
                    result.MissingCount++;
                }

                rows.Add(row);
                targets.Add(new TimelineRelinkTarget
                {
                    RowIndex = row.RowIndex,
                    Item = item,
                });
            }

            result.ScannedFilePathCount = rows.Count;
            logger.Info($"TimelineRelink.Scan completed. scanned={result.ScannedFilePathCount}, missing={result.MissingCount}, skipped={result.SkippedCount}");
            logger.Flush();

            return (new TimelineRelinkContext
            {
                Info = info,
                Targets = targets,
                Rows = rows,
                ScannedTimeline = timeline,
            }, result);
        }
        catch (Exception ex)
        {
            logger.Error(ex, "TimelineRelink.Scan failed.");
            result.ErrorMessage = "\u8aad\u307f\u8fbc\u307f\u4e2d\u306b\u30a8\u30e9\u30fc\u304c\u767a\u751f\u3057\u307e\u3057\u305f\u3002";
            return (null, result);
        }
    }

    internal static bool IsCurrentReview(TimelineRelinkContext context, IReadOnlyList<RelinkRow> rows)
    {
        var info = TimelineContextService.Info;
        return ReferenceEquals(info, context.Info) && info?.Timeline is { } current &&
            MatchesReview(context.ScannedTimeline, current, current.Items,
                context.Targets.Select(target => (target.RowIndex, target.Item)), rows,
                item => TimelineItemFilePathAccessor.TryGetFilePath(item, out var path) ? path : null);
    }

    internal static bool MatchesReview<T>(object? scannedTimeline, object? currentTimeline, IEnumerable<T> currentItems,
        IEnumerable<(int RowIndex, T Item)> targets, IReadOnlyList<RelinkRow> rows, Func<T, string?> readPath) where T : class
    {
        if (scannedTimeline is null || !ReferenceEquals(scannedTimeline, currentTimeline)) return false;
        var current = new HashSet<T>(currentItems, ReferenceEqualityComparer.Instance);
        var byRow = new Dictionary<int, T>();
        foreach (var (row, item) in targets)
            if (!byRow.TryAdd(row, item)) return false;

        var used = new HashSet<T>(ReferenceEqualityComparer.Instance);
        foreach (var row in rows.Where(x => x.Status == RelinkStatus.Updated && !string.IsNullOrWhiteSpace(x.SelectedCandidate)))
        {
            if (!byRow.TryGetValue(row.RowIndex, out var item) || !current.Contains(item) || !used.Add(item) ||
                !string.Equals(readPath(item), row.OriginalPath, StringComparison.Ordinal))
                return false;
        }
        return true;
    }

    //CSJ,method=Save,保存する
    public (bool Success, string? ErrorMessage, int UpdatedCount) Save(TimelineRelinkContext context)
    {
        try
        {
            var selected = context.Rows
                .Where(x => x.Status == RelinkStatus.Updated && !string.IsNullOrWhiteSpace(x.SelectedCandidate)).ToArray();
            foreach (var row in selected)
                RelinkJsonUpdate.EnsureSupported(row);
            if (selected.Length == 0) return (true, null, 0);
            if (!IsCurrentReview(context, selected))
                return (false, "タイムラインの素材パスまたは対象が変更されました。再読み込みしてください。", 0);
            return (false, "タイムラインへの反映は安全性を検証中のため停止しています。", 0);
        }
        catch (InvalidDataException ex)
        {
            logger.Error(ex, "TimelineRelink.Save validation failed.");
            return (false, ex.Message, 0);
        }
        catch (Exception ex)
        {
            logger.Error(ex, "TimelineRelink.Save failed.");
            return (false, "\u4fdd\u5b58\u306b\u5931\u6557\u3057\u307e\u3057\u305f\u3002\u30ed\u30b0\u3092\u78ba\u8a8d\u3057\u3066\u304f\u3060\u3055\u3044\u3002", 0);
        }
    }

}
