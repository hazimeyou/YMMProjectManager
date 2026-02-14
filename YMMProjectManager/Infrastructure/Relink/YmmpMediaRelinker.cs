using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using YMMProjectManager.Infrastructure;

namespace YMMProjectManager.Infrastructure.Relink;

public enum RelinkStatus
{
    Existing,
    Missing,
    Updated,
    Ambiguous,
    NotFound,
    Failed,
}

public sealed class RelinkRow : INotifyPropertyChanged
{
    private RelinkStatus status;
    private int candidateCount;
    private string? selectedCandidate;
    private string message = string.Empty;

    public int RowIndex { get; init; }
    public string TypeHint { get; init; } = string.Empty;
    public string OriginalPath { get; init; } = string.Empty;
    public string FileName { get; init; } = string.Empty;
    public string Extension { get; init; } = string.Empty;
    public ObservableCollection<string> Candidates { get; } = [];

    public RelinkStatus Status
    {
        get => status;
        set
        {
            if (SetProperty(ref status, value))
            {
                OnPropertyChanged(nameof(IsEditable));
            }
        }
    }

    public int CandidateCount
    {
        get => candidateCount;
        set => SetProperty(ref candidateCount, value);
    }

    public string? SelectedCandidate
    {
        get => selectedCandidate;
        set => SetProperty(ref selectedCandidate, value);
    }

    public string Message
    {
        get => message;
        set => SetProperty(ref message, value);
    }

    public bool IsEditable => Status == RelinkStatus.Ambiguous;

    public event PropertyChangedEventHandler? PropertyChanged;

    private bool SetProperty<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
        {
            return false;
        }

        field = value;
        OnPropertyChanged(name);
        return true;
    }

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}

public sealed class RelinkSearchInputRow
{
    public int RowIndex { get; init; }
    public string TypeHint { get; init; } = string.Empty;
    public string OriginalPath { get; init; } = string.Empty;
    public string FileName { get; init; } = string.Empty;
    public string Extension { get; init; } = string.Empty;
    public RelinkStatus CurrentStatus { get; init; }
}

public sealed class RelinkRowUpdate
{
    public int RowIndex { get; init; }
    public RelinkStatus Status { get; init; }
    public string? SelectedCandidate { get; init; }
    public IReadOnlyList<string> Candidates { get; init; } = Array.Empty<string>();
    public string Message { get; init; } = string.Empty;
}

public sealed class RelinkSearchProgressInfo
{
    public int Done { get; init; }
    public int Total { get; init; }
    public string CurrentFileName { get; init; } = string.Empty;
}

public sealed class RelinkSearchExecutionResult
{
    public RelinkResult Summary { get; init; } = new();
    public List<RelinkRowUpdate> Updates { get; init; } = [];
}

public sealed class RelinkResult
{
    public int ScannedFilePathCount { get; set; }
    public int MissingCount { get; set; }
    public int UpdatedCount { get; set; }
    public int AmbiguousCount { get; set; }
    public int NotFoundCount { get; set; }
    public int SkippedCount { get; set; }
    public int FailedCount { get; set; }
    public string? ErrorMessage { get; set; }
}

public sealed class RelinkDocumentContext
{
    public string YmmpPath { get; init; } = string.Empty;
    public string OriginalJsonText { get; init; } = string.Empty;
    public List<RelinkRow> Rows { get; init; } = [];
}

public sealed class RelinkScanService
{
    private readonly FileLogger logger;

    public RelinkScanService(FileLogger logger)
    {
        this.logger = logger;
    }

    public async Task<(RelinkDocumentContext? Context, RelinkResult Result)> ScanAsync(string ymmpPath, CancellationToken token)
    {
        var result = new RelinkResult();
        try
        {
            logger.Info($"Relink.Scan start. ymmp={ymmpPath}");
            if (!File.Exists(ymmpPath))
            {
                result.ErrorMessage = ".ymmp が見つかりません。";
                return (null, result);
            }

            var text = await File.ReadAllTextAsync(ymmpPath, token).ConfigureAwait(false);
            var root = JsonNode.Parse(text);
            if (root is null)
            {
                result.ErrorMessage = ".ymmp のJSON読み込みに失敗しました。";
                return (null, result);
            }

            var nodes = new List<FilePathNode>();
            CollectFilePathNodes(root, false, null, nodes);
            result.ScannedFilePathCount = nodes.Count;

            var rows = new List<RelinkRow>(nodes.Count);
            for (var i = 0; i < nodes.Count; i++)
            {
                token.ThrowIfCancellationRequested();
                var node = nodes[i];
                var row = new RelinkRow
                {
                    RowIndex = i,
                    TypeHint = node.TypeHint ?? string.Empty,
                    OriginalPath = node.Path,
                    FileName = node.FileName,
                    Extension = node.Extension,
                };

                try
                {
                    if (File.Exists(node.Path))
                    {
                        row.Status = RelinkStatus.Existing;
                        row.Message = "既存パス";
                        result.SkippedCount++;
                    }
                    else
                    {
                        row.Status = RelinkStatus.Missing;
                        row.Message = "リンク切れ";
                        result.MissingCount++;
                    }
                }
                catch (Exception ex)
                {
                    row.Status = RelinkStatus.Failed;
                    row.Message = ex.Message;
                    result.FailedCount++;
                    logger.Error($"Scan failed: {node.Path}", ex);
                }

                rows.Add(row);
            }

            logger.Info($"Scan completed. scannedFilePathCount={result.ScannedFilePathCount}, missingCount={result.MissingCount}, updatedCount={result.UpdatedCount}, ambiguousCount={result.AmbiguousCount}, notFoundCount={result.NotFoundCount}, skippedCount={result.SkippedCount}, failedCount={result.FailedCount}");
            logger.Flush();
            return (new RelinkDocumentContext { YmmpPath = ymmpPath, OriginalJsonText = text, Rows = rows }, result);
        }
        catch (OperationCanceledException)
        {
            logger.Info($"Relink.Scan canceled. ymmp={ymmpPath}");
            logger.Flush();
            throw;
        }
        catch (Exception ex)
        {
            logger.Error(ex, $"Relink.Scan failed. ymmp={ymmpPath}");
            result.ErrorMessage = "読み込み中にエラーが発生しました。";
            return (null, result);
        }
    }

    private static void CollectFilePathNodes(JsonNode node, bool insideItems, string? currentTypeHint, List<FilePathNode> target)
    {
        if (node is JsonObject obj)
        {
            var typeHint = GetTypeHint(obj, currentTypeHint);
            foreach (var kv in obj)
            {
                if (kv.Value is null)
                {
                    continue;
                }

                if (insideItems && kv.Key == "FilePath" && kv.Value is JsonValue p && p.TryGetValue<string>(out var path) && !string.IsNullOrWhiteSpace(path))
                {
                    target.Add(new FilePathNode(path, typeHint));
                    continue;
                }

                var nextInside = insideItems || kv.Key == "Items";
                CollectFilePathNodes(kv.Value, nextInside, typeHint, target);
            }
        }
        else if (node is JsonArray arr)
        {
            foreach (var child in arr)
            {
                if (child is not null)
                {
                    CollectFilePathNodes(child, insideItems, currentTypeHint, target);
                }
            }
        }
    }

    private static string? GetTypeHint(JsonObject obj, string? fallback)
    {
        foreach (var key in new[] { "TypeHint", "Type", "$type", "ItemType" })
        {
            if (obj.TryGetPropertyValue(key, out var node) && node is JsonValue v && v.TryGetValue<string>(out var s) && !string.IsNullOrWhiteSpace(s))
            {
                return s;
            }
        }

        return fallback;
    }

    private sealed record FilePathNode(string Path, string? TypeHint)
    {
        public string FileName => System.IO.Path.GetFileName(Path) ?? string.Empty;
        public string Extension => System.IO.Path.GetExtension(Path) ?? string.Empty;
    }
}

public sealed class RelinkSearchService
{
    private readonly FileLogger logger;

    public RelinkSearchService(FileLogger logger)
    {
        this.logger = logger;
    }

    public async Task<RelinkSearchExecutionResult> ExecuteAsync(
        IReadOnlyList<RelinkSearchInputRow> rows,
        IReadOnlyList<string> searchFolders,
        CancellationToken token,
        IProgress<RelinkSearchProgressInfo>? progress = null)
    {
        var summary = new RelinkResult
        {
            ScannedFilePathCount = rows.Count,
            MissingCount = rows.Count(x => x.CurrentStatus is RelinkStatus.Missing or RelinkStatus.Ambiguous or RelinkStatus.NotFound),
            SkippedCount = rows.Count(x => x.CurrentStatus == RelinkStatus.Existing),
        };
        var updates = new List<RelinkRowUpdate>(rows.Count);

        try
        {
            logger.Info($"Relink.SearchService start. rows={rows.Count}, folders={searchFolders.Count}");
            var normalizedFolders = searchFolders
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            var candidateCache = new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase);
            var targets = rows.Where(x => x.CurrentStatus is RelinkStatus.Missing or RelinkStatus.Ambiguous or RelinkStatus.NotFound).ToList();

            for (var i = 0; i < targets.Count; i++)
            {
                token.ThrowIfCancellationRequested();
                var row = targets[i];
                progress?.Report(new RelinkSearchProgressInfo { Done = i + 1, Total = targets.Count, CurrentFileName = row.FileName });

                try
                {
                    var candidates = await FindCandidatesByFileNameAsync(
                        row.FileName,
                        normalizedFolders,
                        candidateCache,
                        summary,
                        token).ConfigureAwait(false);
                    if (candidates.Count == 0)
                    {
                        updates.Add(new RelinkRowUpdate
                        {
                            RowIndex = row.RowIndex,
                            Status = RelinkStatus.NotFound,
                            Message = "候補未発見",
                        });
                        summary.NotFoundCount++;
                        logger.Info($"NotFound: {row.OriginalPath} -> 候補未発見");
                        continue;
                    }

                    if (candidates.Count == 1)
                    {
                        updates.Add(new RelinkRowUpdate
                        {
                            RowIndex = row.RowIndex,
                            Status = RelinkStatus.Updated,
                            SelectedCandidate = candidates[0],
                            Candidates = candidates,
                            Message = "自動更新",
                        });
                        summary.UpdatedCount++;
                        logger.Info($"Updated: {row.OriginalPath} -> {candidates[0]}");
                    }
                    else
                    {
                        updates.Add(new RelinkRowUpdate
                        {
                            RowIndex = row.RowIndex,
                            Status = RelinkStatus.Ambiguous,
                            Candidates = candidates,
                            Message = $"候補複数: {candidates.Count}",
                        });
                        summary.AmbiguousCount++;
                        logger.Info($"Ambiguous: {row.OriginalPath} -> 候補複数({candidates.Count})");
                    }
                }
                catch (Exception ex)
                {
                    updates.Add(new RelinkRowUpdate
                    {
                        RowIndex = row.RowIndex,
                        Status = RelinkStatus.Failed,
                        Message = ex.Message,
                    });
                    summary.FailedCount++;
                    logger.Error(ex, $"Failed: {row.OriginalPath} -> {ex.Message}");
                }
            }

            logger.Info($"Search completed. scannedFilePathCount={summary.ScannedFilePathCount}, missingCount={summary.MissingCount}, updatedCount={summary.UpdatedCount}, ambiguousCount={summary.AmbiguousCount}, notFoundCount={summary.NotFoundCount}, skippedCount={summary.SkippedCount}, failedCount={summary.FailedCount}");
            logger.Flush();
            return new RelinkSearchExecutionResult { Summary = summary, Updates = updates };
        }
        catch (OperationCanceledException)
        {
            logger.Info("Relink.SearchService canceled.");
            logger.Flush();
            throw;
        }
        catch (Exception ex)
        {
            logger.Error(ex, "Relink.SearchService failed.");
            summary.ErrorMessage = "探索中にエラーが発生しました。";
            return new RelinkSearchExecutionResult { Summary = summary, Updates = updates };
        }
    }

    private async Task<IReadOnlyList<string>> FindCandidatesByFileNameAsync(
        string fileName,
        IReadOnlyList<string> searchFolders,
        IDictionary<string, IReadOnlyList<string>> cache,
        RelinkResult summary,
        CancellationToken token)
    {
        if (cache.TryGetValue(fileName, out var cached))
        {
            return cached;
        }

        var candidates = await Task.Run(() =>
        {
            var results = new List<string>();
            foreach (var folder in searchFolders)
            {
                token.ThrowIfCancellationRequested();
                if (!Directory.Exists(folder))
                {
                    continue;
                }

                foreach (var file in EnumerateFilesSafe(folder, summary))
                {
                    token.ThrowIfCancellationRequested();
                    if (string.Equals(Path.GetFileName(file), fileName, StringComparison.OrdinalIgnoreCase))
                    {
                        results.Add(file);
                    }
                }
            }
            return (IReadOnlyList<string>)results;
        }, token).ConfigureAwait(false);

        cache[fileName] = candidates;
        return candidates;
    }

    private IEnumerable<string> EnumerateFilesSafe(string root, RelinkResult summary)
    {
        var stack = new Stack<string>();
        stack.Push(root);

        while (stack.Count > 0)
        {
            var dir = stack.Pop();

            IEnumerable<string> files;
            try
            {
                files = Directory.EnumerateFiles(dir);
            }
            catch (Exception ex)
            {
                summary.FailedCount++;
                logger.Error($"EnumerateFiles failed: {dir}", ex);
                continue;
            }

            foreach (var file in files)
            {
                yield return file;
            }

            IEnumerable<string> subDirs;
            try
            {
                subDirs = Directory.EnumerateDirectories(dir);
            }
            catch (Exception ex)
            {
                summary.FailedCount++;
                logger.Error($"EnumerateDirectories failed: {dir}", ex);
                continue;
            }

            foreach (var subDir in subDirs)
            {
                stack.Push(subDir);
            }
        }
    }
}

public sealed class RelinkSaveService
{
    private readonly FileLogger logger;

    public RelinkSaveService(FileLogger logger)
    {
        this.logger = logger;
    }

    public async Task<(bool Success, string? ErrorMessage, string? BackupPath)> SaveAsync(RelinkDocumentContext context, CancellationToken token)
    {
        try
        {
            logger.Info($"Relink.SaveService start. ymmp={context.YmmpPath}");
            var updatedRows = context.Rows
                .Where(x => x.Status == RelinkStatus.Updated && !string.IsNullOrWhiteSpace(x.SelectedCandidate))
                .ToList();
            if (updatedRows.Count == 0)
            {
                logger.Info($"Relink.SaveService skipped. ymmp={context.YmmpPath}, reason=no updates");
                logger.Flush();
                return (true, null, null);
            }

            var backupPath = context.YmmpPath + ".bak";
            File.Copy(context.YmmpPath, backupPath, overwrite: true);

            var text = context.OriginalJsonText;
            foreach (var row in updatedRows)
            {
                token.ThrowIfCancellationRequested();

                var oldToken = JsonSerializer.Serialize(row.OriginalPath);
                var newToken = JsonSerializer.Serialize(row.SelectedCandidate!);
                var pattern = "(\"FilePath\"\\s*:\\s*)" + Regex.Escape(oldToken);
                text = Regex.Replace(text, pattern, "$1" + newToken, RegexOptions.CultureInvariant);
            }

            await File.WriteAllTextAsync(context.YmmpPath, text, token).ConfigureAwait(false);

            logger.Info($"Save completed. updatedCount={updatedRows.Count}, backup={backupPath}");
            logger.Flush();
            return (true, null, backupPath);
        }
        catch (OperationCanceledException)
        {
            logger.Info($"Relink.SaveService canceled. ymmp={context.YmmpPath}");
            logger.Flush();
            throw;
        }
        catch (Exception ex)
        {
            logger.Error(ex, $"Relink.SaveService failed. ymmp={context.YmmpPath}");
            return (false, "保存に失敗しました。ログを確認してください。", null);
        }
    }
}
