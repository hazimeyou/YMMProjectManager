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
}

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
        if (info?.Timeline is null)
        {
            result.ErrorMessage = "\u30bf\u30a4\u30e0\u30e9\u30a4\u30f3\u60c5\u5831\u3092\u53d6\u5f97\u3067\u304d\u307e\u305b\u3093\u3067\u3057\u305f\u3002";
            return (null, result);
        }

        try
        {
            var targets = new List<TimelineRelinkTarget>();
            //CSJ,variable=rows,行一覧
            var rows = new List<RelinkRow>();

            foreach (var item in info.Timeline.Items)
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
            }, result);
        }
        catch (Exception ex)
        {
            logger.Error(ex, "TimelineRelink.Scan failed.");
            result.ErrorMessage = "\u8aad\u307f\u8fbc\u307f\u4e2d\u306b\u30a8\u30e9\u30fc\u304c\u767a\u751f\u3057\u307e\u3057\u305f\u3002";
            return (null, result);
        }
    }

    //CSJ,method=Save,保存する
    public (bool Success, string? ErrorMessage, int UpdatedCount) Save(TimelineRelinkContext context)
    {
        try
        {
            foreach (var row in context.Rows.Where(x => x.Status == RelinkStatus.Updated && !string.IsNullOrWhiteSpace(x.SelectedCandidate)))
                RelinkJsonUpdate.EnsureSupported(row);
            //CSJ,variable=updatedCount,更新済みの件数
            var updatedCount = 0;
            //CSJ,variable=targetMap,対象対応表
            var targetMap = context.Targets.ToDictionary(x => x.RowIndex);
            foreach (var row in context.Rows.Where(x => x.Status == RelinkStatus.Updated && !string.IsNullOrWhiteSpace(x.SelectedCandidate)))
            {
                if (!targetMap.TryGetValue(row.RowIndex, out var target) ||
                    !TimelineItemFilePathAccessor.TryGetFilePath(target.Item, out var currentPath) ||
                    !string.Equals(currentPath, row.OriginalPath, StringComparison.OrdinalIgnoreCase))
                    return (false, "タイムラインの素材パスが変更されました。再読み込みしてください。", 0);
            }
            if (context.Rows.Any(row => row.Status == RelinkStatus.Updated && !string.IsNullOrWhiteSpace(row.SelectedCandidate)))
                return (false, "タイムラインへの反映は安全性を検証中のため停止しています。", 0);
            return (true, null, updatedCount);
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
