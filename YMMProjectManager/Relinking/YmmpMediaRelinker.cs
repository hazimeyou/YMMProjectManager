using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Security.Cryptography;
using System.Text;
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
        set
        {
            if (SetProperty(ref selectedCandidate, value) && Status == RelinkStatus.Ambiguous && !string.IsNullOrWhiteSpace(value))
                Status = RelinkStatus.Updated;
        }
    }

    public string Message
    {
        get => message;
        set => SetProperty(ref message, value);
    }

    public bool IsEditable => Status == RelinkStatus.Ambiguous || (Status == RelinkStatus.Updated && CandidateCount > 1);

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
    internal byte[]? OriginalBytes { get; init; }
}

public sealed class RelinkScanService
{
    private readonly FileLogger logger;

    public RelinkScanService(FileLogger logger)
    {
        this.logger = logger;
    }

    public Task<(RelinkDocumentContext? Context, RelinkResult Result)> ScanAsync(string ymmpPath, CancellationToken token) =>
        ScanCoreAsync(ymmpPath, probeOriginalPath: true, token);

    // A received project can contain paths to unapproved drives, shares, or devices.
    public Task<(RelinkDocumentContext? Context, RelinkResult Result)> ScanForStrictRelinkAsync(string ymmpPath, CancellationToken token) =>
        ScanCoreAsync(ymmpPath, probeOriginalPath: false, token);

    private async Task<(RelinkDocumentContext? Context, RelinkResult Result)> ScanCoreAsync(string ymmpPath,
        bool probeOriginalPath, CancellationToken token)
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

            var bytes = probeOriginalPath
                ? await File.ReadAllBytesAsync(ymmpPath, token).ConfigureAwait(false)
                : await ReadSelectedProjectAsync(ymmpPath, token).ConfigureAwait(false);
            var offset = bytes.AsSpan().StartsWith(new byte[] { 0xef, 0xbb, 0xbf }) ? 3 : 0;
            var text = new UTF8Encoding(false, true).GetString(bytes, offset, bytes.Length - offset);
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
                    if (!probeOriginalPath)
                    {
                        row.Status = RelinkStatus.Missing;
                        row.Message = "元の参照先は未確認";
                        result.MissingCount++;
                    }
                    else if (File.Exists(node.Path))
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
            return (new RelinkDocumentContext { YmmpPath = ymmpPath, OriginalJsonText = text, OriginalBytes = bytes, Rows = rows }, result);
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

    private static async Task<byte[]> ReadSelectedProjectAsync(string ymmpPath, CancellationToken token)
    {
        var full = Path.GetFullPath(ymmpPath);
        using var directories = OwnedFilePublication.DirectoryFence.Open(Path.GetDirectoryName(full)!, enforceShare: true);
        using var handle = OwnedFilePublication.OpenOrdinaryPath(full, expectDirectory: false, allowRename: false);
        await using var stream = new FileStream(handle, FileAccess.Read);
        if (stream.Length > 32 * 1024 * 1024) throw new InvalidDataException("プロジェクトが大きすぎます。");
        var bytes = new byte[checked((int)stream.Length)];
        await stream.ReadExactlyAsync(bytes, token).ConfigureAwait(false);
        return bytes;
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
            var targets = rows.Where(x => x.CurrentStatus is RelinkStatus.Missing or RelinkStatus.Ambiguous or RelinkStatus.NotFound).ToList();
            var candidateIndex = await BuildCandidateIndexAsync(normalizedFolders, summary, token, progress).ConfigureAwait(false);

            for (var i = 0; i < targets.Count; i++)
            {
                token.ThrowIfCancellationRequested();
                var row = targets[i];
                progress?.Report(new RelinkSearchProgressInfo { Done = i + 1, Total = targets.Count, CurrentFileName = row.FileName });

                try
                {
                    candidateIndex.TryGetValue(row.FileName, out var indexedCandidates);
                    var candidates = indexedCandidates is null
                        ? []
                        : (IReadOnlyList<string>)indexedCandidates;
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

    private async Task<Dictionary<string, List<string>>> BuildCandidateIndexAsync(
        IReadOnlyList<string> searchFolders,
        RelinkResult summary,
        CancellationToken token,
        IProgress<RelinkSearchProgressInfo>? progress)
    {
        return await Task.Run(() =>
        {
            var index = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
            var scanned = 0;
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
                    var fileName = Path.GetFileName(file);
                    if (string.IsNullOrWhiteSpace(fileName))
                    {
                        continue;
                    }

                    if (!index.TryGetValue(fileName, out var list))
                    {
                        list = [];
                        index[fileName] = list;
                    }

                    list.Add(file);
                    scanned++;
                    if (scanned % 2000 == 0)
                    {
                        progress?.Report(new RelinkSearchProgressInfo
                        {
                            Done = scanned,
                            Total = 0,
                            CurrentFileName = $"索引作成中: {fileName}",
                        });
                    }
                }
            }
            return index;
        }, token).ConfigureAwait(false);
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
    private readonly Func<Stream, byte[], CancellationToken, Task> write;
    private readonly Action<FileStream> flush;
    private readonly Action? beforeCommit;

    public RelinkSaveService(FileLogger logger) : this(logger, null, null, null) { }
    internal RelinkSaveService(FileLogger logger, Func<Stream, byte[], CancellationToken, Task>? write,
        Action<FileStream>? flush, Action? beforeCommit)
    {
        this.logger = logger;
        this.write = write ?? ((stream, bytes, token) => stream.WriteAsync(bytes, token).AsTask());
        this.flush = flush ?? (stream => stream.Flush(flushToDisk: true));
        this.beforeCommit = beforeCommit;
    }

    public Task<(bool Success, string? ErrorMessage)> SaveAsAsync(RelinkDocumentContext context, string destination, CancellationToken token) =>
        SaveAsCoreAsync(context, destination, null, null, token);

    internal Task<(bool Success, string? ErrorMessage)> StagePreparedAsync(RelinkDocumentContext context, string destination,
        string stage, Func<FileStream, CancellationToken, Task> persistPublication, CancellationToken token) =>
        SaveAsCoreAsync(context, destination, stage, persistPublication, token);

    private async Task<(bool Success, string? ErrorMessage)> SaveAsCoreAsync(RelinkDocumentContext context, string destination,
        string? stage, Func<FileStream, CancellationToken, Task>? persistPublication, CancellationToken token)
    {
        try
        {
            token.ThrowIfCancellationRequested();
            var rows = RelinkCandidateVerifier.SnapshotRows(context.Rows.Where(row => row.Status == RelinkStatus.Updated)).ToArray();
            if (rows.Length == 0) return (false, "更新対象はありません。");
            var original = context.OriginalBytes?.ToArray();
            if (original is null || rows.Any(row => string.IsNullOrWhiteSpace(row.SelectedCandidate)))
                return (false, "再リンク対象を確認できません。");
            var source = Path.GetFullPath(context.YmmpPath);
            var output = Path.GetFullPath(destination);
            if (!string.Equals(Path.GetDirectoryName(source), Path.GetDirectoryName(output), StringComparison.Ordinal))
                return (false, "相対素材パスを保護するため、元のプロジェクトと同じフォルダーに別名保存してください。");
            if (File.Exists(output) || Directory.Exists(output)) return (false, "既存ファイルは上書きできません。別名を選択してください。");
            using var directory = OwnedFilePublication.DirectoryFence.Open(Path.GetDirectoryName(source)!, enforceShare: true);
            var expected = SHA256.HashData(original);
            using (var initial = new FileStream(OwnedFilePublication.OpenOrdinaryPath(source, expectDirectory: false, allowRename: false), FileAccess.Read))
            {
                var initialHash = await SHA256.HashDataAsync(initial, token).ConfigureAwait(false);
                if (!expected.SequenceEqual(initialHash))
                    return (false, "プロジェクトが外部で変更されました。再読み込みしてください。");
            }
            var updated = RelinkJsonUpdate.Apply(original, rows);
            using (JsonDocument.Parse(updated.AsMemory(updated.AsSpan().StartsWith(new byte[] { 0xef, 0xbb, 0xbf }) ? 3 : 0))) { }
            if (persistPublication is not null)
            {
                await WritePreparedStageAsync(stage!, updated, persistPublication, token).ConfigureAwait(false);
                return (true, null);
            }
            var candidate = Path.Combine(Path.GetDirectoryName(output)!, $".ymmpm-relink.tmp-{Guid.NewGuid():N}");
            await using var stream = OwnedFilePublication.CreateOwnedOutputFile(candidate, readBack: true);
            var published = false;
            try
            {
                await write(stream, updated, token).ConfigureAwait(false);
                await stream.FlushAsync(token).ConfigureAwait(false);
                flush(stream);
                stream.Position = 0;
                var stagedHash = await SHA256.HashDataAsync(stream, token).ConfigureAwait(false);
                if (!SHA256.HashData(updated).SequenceEqual(stagedHash))
                    return (false, "書き込んだ再リンク結果を検証できません。");
                beforeCommit?.Invoke();
                token.ThrowIfCancellationRequested();
                using var current = new FileStream(OwnedFilePublication.OpenOrdinaryPath(source, expectDirectory: false, allowRename: false), FileAccess.Read);
                var currentHash = await SHA256.HashDataAsync(current, token).ConfigureAwait(false);
                if (!expected.SequenceEqual(currentHash))
                    return (false, "プロジェクトが外部で変更されました。再読み込みしてください。");
                token.ThrowIfCancellationRequested();
                OwnedFilePublication.RenameOpenedPath(stream.SafeFileHandle, output);
                published = true;
                return (true, null);
            }
            finally { if (!published) OwnedFilePublication.TryDeleteOpenedPath(stream.SafeFileHandle); }
        }
        catch (OperationCanceledException) { throw; }
        catch (InvalidDataException ex) { return (false, ex.Message); }
        catch (Exception ex)
        {
            logger.Error(ex, "Relink save-as failed.");
            return (false, "別名保存に失敗しました。出力先とログを確認してください。");
        }
    }

    private async Task WritePreparedStageAsync(string stage, byte[] updated,
        Func<FileStream, CancellationToken, Task> persistPublication, CancellationToken token)
    {
        await using var stream = OwnedFilePublication.CreateOwnedOutputFile(stage, readBack: true);
        var durable = false;
        try
        {
            await write(stream, updated, token).ConfigureAwait(false);
            await stream.FlushAsync(token).ConfigureAwait(false);
            flush(stream);
            token.ThrowIfCancellationRequested();
            stream.Position = 0;
            await persistPublication(stream, token).ConfigureAwait(false);
            durable = true;
            beforeCommit?.Invoke();
            token.ThrowIfCancellationRequested();
        }
        finally
        {
            // The metadata writer verifies the branch and publishes this exact stage. Keep its proof for retry.
            if (!durable) OwnedFilePublication.TryDeleteOpenedPath(stream.SafeFileHandle);
        }
    }

    // In-place replacement cannot prevent a concurrent external write between comparison and replace.
    // Existing callers must choose a new destination through SaveAsAsync instead.
    public Task<(bool Success, string? ErrorMessage, string? BackupPath)> SaveAsync(RelinkDocumentContext context, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        return Task.FromResult<(bool, string?, string?)>((false, "元ファイルへの上書きは無効です。別名保存してください。", null));
    }

}
