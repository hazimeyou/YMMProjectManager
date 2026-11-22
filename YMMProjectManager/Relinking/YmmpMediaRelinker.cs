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

//CSJ,enum=RelinkStatus,再リンク状態
public enum RelinkStatus
{
    //CSJ,field=Existing,既存
    Existing,
    //CSJ,field=Missing,欠落
    Missing,
    //CSJ,field=Updated,更新済み
    Updated,
    Ambiguous,
    //CSJ,field=NotFound,未検出
    NotFound,
    //CSJ,field=Failed,失敗
    Failed,
}

//CSJ,class=RelinkRow,再リンク行
public sealed class RelinkRow : INotifyPropertyChanged
{
    //CSJ,field=status,状態
    private RelinkStatus status;
    //CSJ,field=candidateCount,候補件数
    private int candidateCount;
    //CSJ,field=selectedCandidate,選択中の候補
    private string? selectedCandidate;
    //CSJ,field=message,メッセージ
    private string message = string.Empty;

    //CSJ,property=RowIndex,行インデックス
    public int RowIndex { get; init; }
    //CSJ,property=TypeHint,型の手掛かり
    public string TypeHint { get; init; } = string.Empty;
    //CSJ,property=OriginalPath,元のパス
    public string OriginalPath { get; init; } = string.Empty;
    //CSJ,property=FileName,ファイル名
    public string FileName { get; init; } = string.Empty;
    //CSJ,property=Extension,拡張子
    public string Extension { get; init; } = string.Empty;
    //CSJ,property=Candidates,候補一覧
    public ObservableCollection<string> Candidates { get; } = [];

    //CSJ,property=Status,状態
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

    //CSJ,property=CandidateCount,候補件数
    public int CandidateCount
    {
        get => candidateCount;
        set => SetProperty(ref candidateCount, value);
    }

    //CSJ,property=SelectedCandidate,選択中の候補
    public string? SelectedCandidate
    {
        get => selectedCandidate;
        set
        {
            if (SetProperty(ref selectedCandidate, value) && Status == RelinkStatus.Ambiguous && !string.IsNullOrWhiteSpace(value))
                Status = RelinkStatus.Updated;
        }
    }

    //CSJ,property=Message,メッセージ
    public string Message
    {
        get => message;
        set => SetProperty(ref message, value);
    }

    //CSJ,property=IsEditable,編集可能か
    public bool IsEditable => Status == RelinkStatus.Ambiguous || (Status == RelinkStatus.Updated && CandidateCount > 1);

    //CSJ,event=PropertyChanged,プロパティ変更済み
    public event PropertyChangedEventHandler? PropertyChanged;

    //CSJ,method=SetProperty,プロパティを設定する
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

    //CSJ,method=OnPropertyChanged,プロパティ変更済み時の処理
    private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}

//CSJ,class=RelinkSearchInputRow,再リンク検索入力行
public sealed class RelinkSearchInputRow
{
    //CSJ,property=RowIndex,行インデックス
    public int RowIndex { get; init; }
    //CSJ,property=TypeHint,型の手掛かり
    public string TypeHint { get; init; } = string.Empty;
    //CSJ,property=OriginalPath,元のパス
    public string OriginalPath { get; init; } = string.Empty;
    //CSJ,property=FileName,ファイル名
    public string FileName { get; init; } = string.Empty;
    //CSJ,property=Extension,拡張子
    public string Extension { get; init; } = string.Empty;
    //CSJ,property=CurrentStatus,現在の状態
    public RelinkStatus CurrentStatus { get; init; }
}

//CSJ,class=RelinkRowUpdate,再リンク行更新
public sealed class RelinkRowUpdate
{
    //CSJ,property=RowIndex,行インデックス
    public int RowIndex { get; init; }
    //CSJ,property=Status,状態
    public RelinkStatus Status { get; init; }
    //CSJ,property=SelectedCandidate,選択中の候補
    public string? SelectedCandidate { get; init; }
    //CSJ,property=Candidates,候補一覧
    public IReadOnlyList<string> Candidates { get; init; } = Array.Empty<string>();
    //CSJ,property=Message,メッセージ
    public string Message { get; init; } = string.Empty;
}

//CSJ,class=RelinkSearchProgressInfo,再リンク検索進捗情報
public sealed class RelinkSearchProgressInfo
{
    public int Done { get; init; }
    //CSJ,property=Total,合計
    public int Total { get; init; }
    //CSJ,property=CurrentFileName,現在のファイル名
    public string CurrentFileName { get; init; } = string.Empty;
}

//CSJ,class=RelinkSearchExecutionResult,再リンク検索の実行結果
public sealed class RelinkSearchExecutionResult
{
    //CSJ,property=Summary,概要
    public RelinkResult Summary { get; init; } = new();
    //CSJ,property=Updates,更新一覧
    public List<RelinkRowUpdate> Updates { get; init; } = [];
}

//CSJ,class=RelinkResult,再リンク結果
public sealed class RelinkResult
{
    //CSJ,property=ScannedFilePathCount,探索したファイルパス数
    public int ScannedFilePathCount { get; set; }
    //CSJ,property=MissingCount,欠落件数
    public int MissingCount { get; set; }
    //CSJ,property=UpdatedCount,更新済みの件数
    public int UpdatedCount { get; set; }
    //CSJ,property=AmbiguousCount,曖昧な候補数
    public int AmbiguousCount { get; set; }
    //CSJ,property=NotFoundCount,未検出件数
    public int NotFoundCount { get; set; }
    //CSJ,property=SkippedCount,スキップ数
    public int SkippedCount { get; set; }
    //CSJ,property=FailedCount,失敗件数
    public int FailedCount { get; set; }
    //CSJ,property=ErrorMessage,エラーメッセージ
    public string? ErrorMessage { get; set; }
}

//CSJ,class=RelinkDocumentContext,再リンク文書コンテキスト
public sealed class RelinkDocumentContext
{
    //CSJ,property=YmmpPath,YMMPパス
    public string YmmpPath { get; init; } = string.Empty;
    //CSJ,property=OriginalJsonText,元のJSONテキスト
    public string OriginalJsonText { get; init; } = string.Empty;
    //CSJ,property=Rows,行一覧
    public List<RelinkRow> Rows { get; init; } = [];
    //CSJ,property=OriginalBytes,元のバイト列
    internal byte[]? OriginalBytes { get; init; }
}

//CSJ,class=RelinkScanService,素材リンクの調査
public sealed class RelinkScanService
{
    //CSJ,field=logger,ロガー
    private readonly FileLogger logger;

    public RelinkScanService(FileLogger logger)
    {
        this.logger = logger;
    }

    //CSJ,method=ScanAsync,走査する（非同期）
    public Task<(RelinkDocumentContext? Context, RelinkResult Result)> ScanAsync(string ymmpPath, CancellationToken token) =>
        ScanCoreAsync(ymmpPath, probeOriginalPath: true, token);

    // A received project can contain paths to unapproved drives, shares, or devices.
    //CSJ,method=ScanForStrictRelinkAsync,厳密な再リンク候補を探索する（非同期）
    public Task<(RelinkDocumentContext? Context, RelinkResult Result)> ScanForStrictRelinkAsync(string ymmpPath, CancellationToken token) =>
        ScanCoreAsync(ymmpPath, probeOriginalPath: false, token);

    //CSJ,method=ScanCoreAsync,中核を走査する（非同期）
    private async Task<(RelinkDocumentContext? Context, RelinkResult Result)> ScanCoreAsync(string ymmpPath,
        //CSJ,parameter=probeOriginalPath,調査元のパス
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

            //CSJ,variable=bytes,バイト列
            var bytes = probeOriginalPath
                ? await File.ReadAllBytesAsync(ymmpPath, token).ConfigureAwait(false)
                : await ReadSelectedProjectAsync(ymmpPath, token).ConfigureAwait(false);
            //CSJ,variable=offset,オフセット
            var offset = bytes.AsSpan().StartsWith(new byte[] { 0xef, 0xbb, 0xbf }) ? 3 : 0;
            //CSJ,variable=text,テキスト
            var text = new UTF8Encoding(false, true).GetString(bytes, offset, bytes.Length - offset);
            //CSJ,variable=root,ルート
            var root = JsonNode.Parse(text);
            if (root is null)
            {
                result.ErrorMessage = ".ymmp のJSON読み込みに失敗しました。";
                return (null, result);
            }

            //CSJ,variable=nodes,ノード一覧
            var nodes = new List<FilePathNode>();
            CollectFilePathNodes(root, false, null, nodes);
            result.ScannedFilePathCount = nodes.Count;

            //CSJ,variable=rows,行一覧
            var rows = new List<RelinkRow>(nodes.Count);
            for (var i = 0; i < nodes.Count; i++)
            {
                token.ThrowIfCancellationRequested();
                //CSJ,variable=node,ノード
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

    //CSJ,method=ReadSelectedProjectAsync,選択中のプロジェクトを読み取る（非同期）
    private static async Task<byte[]> ReadSelectedProjectAsync(string ymmpPath, CancellationToken token)
    {
        //CSJ,variable=full,絶対パス
        var full = Path.GetFullPath(ymmpPath);
        //CSJ,variable=directories,ディレクトリ一覧
        using var directories = OwnedFilePublication.DirectoryFence.Open(Path.GetDirectoryName(full)!, enforceShare: true);
        using var handle = OwnedFilePublication.OpenOrdinaryPath(full, expectDirectory: false, allowRename: false);
        //CSJ,variable=stream,ストリーム
        await using var stream = new FileStream(handle, FileAccess.Read);
        if (stream.Length > 32 * 1024 * 1024) throw new InvalidDataException("プロジェクトが大きすぎます。");
        //CSJ,variable=bytes,バイト列
        var bytes = new byte[checked((int)stream.Length)];
        await stream.ReadExactlyAsync(bytes, token).ConfigureAwait(false);
        return bytes;
    }

    //CSJ,method=CollectFilePathNodes,ファイルパスノード一覧を収集する
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
        //CSJ,variable=key,キー
        foreach (var key in new[] { "TypeHint", "Type", "$type", "ItemType" })
        {
            if (obj.TryGetPropertyValue(key, out var node) && node is JsonValue v && v.TryGetValue<string>(out var s) && !string.IsNullOrWhiteSpace(s))
            {
                return s;
            }
        }

        return fallback;
    }

    //CSJ,record=FilePathNode,ファイルパスノード
    private sealed record FilePathNode(string Path, string? TypeHint)
    {
        //CSJ,property=FileName,ファイル名
        public string FileName => System.IO.Path.GetFileName(Path) ?? string.Empty;
        //CSJ,property=Extension,拡張子
        public string Extension => System.IO.Path.GetExtension(Path) ?? string.Empty;
    }
}

//CSJ,class=RelinkSearchService,再リンク候補の検索
public sealed class RelinkSearchService
{
    //CSJ,field=logger,ロガー
    private readonly FileLogger logger;

    public RelinkSearchService(FileLogger logger)
    {
        this.logger = logger;
    }

    //CSJ,method=ExecuteAsync,実行する（非同期）
    public async Task<RelinkSearchExecutionResult> ExecuteAsync(
        //CSJ,parameter=rows,行一覧
        IReadOnlyList<RelinkSearchInputRow> rows,
        //CSJ,parameter=searchFolders,検索フォルダー一覧
        IReadOnlyList<string> searchFolders,
        CancellationToken token,
        //CSJ,parameter=progress,進捗
        IProgress<RelinkSearchProgressInfo>? progress = null)
    {
        //CSJ,variable=summary,概要
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
            //CSJ,variable=normalizedFolders,正規化済みフォルダー一覧
            var normalizedFolders = searchFolders
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            var targets = rows.Where(x => x.CurrentStatus is RelinkStatus.Missing or RelinkStatus.Ambiguous or RelinkStatus.NotFound).ToList();
            //CSJ,variable=candidateIndex,候補インデックス
            var candidateIndex = await BuildCandidateIndexAsync(normalizedFolders, summary, token, progress).ConfigureAwait(false);

            for (var i = 0; i < targets.Count; i++)
            {
                token.ThrowIfCancellationRequested();
                var row = targets[i];
                progress?.Report(new RelinkSearchProgressInfo { Done = i + 1, Total = targets.Count, CurrentFileName = row.FileName });

                try
                {
                    candidateIndex.TryGetValue(row.FileName, out var indexedCandidates);
                    //CSJ,variable=candidates,候補一覧
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

    //CSJ,method=BuildCandidateIndexAsync,候補インデックスを構築する（非同期）
    private async Task<Dictionary<string, List<string>>> BuildCandidateIndexAsync(
        //CSJ,parameter=searchFolders,検索フォルダー一覧
        IReadOnlyList<string> searchFolders,
        //CSJ,parameter=summary,概要
        RelinkResult summary,
        CancellationToken token,
        //CSJ,parameter=progress,進捗
        IProgress<RelinkSearchProgressInfo>? progress)
    {
        return await Task.Run(() =>
        {
            //CSJ,variable=index,インデックス
            var index = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
            var scanned = 0;
            //CSJ,variable=folder,フォルダー
            foreach (var folder in searchFolders)
            {
                token.ThrowIfCancellationRequested();
                if (!Directory.Exists(folder))
                {
                    continue;
                }

                //CSJ,variable=file,ファイル
                foreach (var file in EnumerateFilesSafe(folder, summary))
                {
                    token.ThrowIfCancellationRequested();
                    //CSJ,variable=fileName,ファイル名
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

    //CSJ,method=EnumerateFilesSafe,ファイル一覧安全を列挙する
    private IEnumerable<string> EnumerateFilesSafe(string root, RelinkResult summary)
    {
        var stack = new Stack<string>();
        stack.Push(root);

        while (stack.Count > 0)
        {
            var dir = stack.Pop();

            //CSJ,variable=files,ファイル一覧
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

            //CSJ,variable=file,ファイル
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

//CSJ,class=RelinkSaveService,再リンク結果の保存
public sealed class RelinkSaveService
{
    //CSJ,field=logger,ロガー
    private readonly FileLogger logger;
    //CSJ,field=write,書き込み
    private readonly Func<Stream, byte[], CancellationToken, Task> write;
    private readonly Action<FileStream> flush;
    //CSJ,field=beforeCommit,再リンク結果の保存確定前の処理
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

    //CSJ,method=SaveAsAsync,別名で保存する（非同期）
    public Task<(bool Success, string? ErrorMessage)> SaveAsAsync(RelinkDocumentContext context, string destination, CancellationToken token) =>
        SaveAsCoreAsync(context, destination, null, null, token);

    //CSJ,method=StagePreparedAsync,準備済みを準備領域に置く（非同期）
    internal Task<(bool Success, string? ErrorMessage)> StagePreparedAsync(RelinkDocumentContext context, string destination,
        //CSJ,parameter=stage,作業段階
        string stage, Func<FileStream, CancellationToken, Task> persistPublication, CancellationToken token) =>
        SaveAsCoreAsync(context, destination, stage, persistPublication, token);

    private async Task<(bool Success, string? ErrorMessage)> SaveAsCoreAsync(RelinkDocumentContext context, string destination,
        //CSJ,parameter=stage,作業段階
        string? stage, Func<FileStream, CancellationToken, Task>? persistPublication, CancellationToken token)
    {
        try
        {
            token.ThrowIfCancellationRequested();
            //CSJ,variable=rows,行一覧
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
            //CSJ,variable=directory,ディレクトリ
            using var directory = OwnedFilePublication.DirectoryFence.Open(Path.GetDirectoryName(source)!, enforceShare: true);
            var expected = SHA256.HashData(original);
            //CSJ,variable=initial,初期
            using (var initial = new FileStream(OwnedFilePublication.OpenOrdinaryPath(source, expectDirectory: false, allowRename: false), FileAccess.Read))
            {
                //CSJ,variable=initialHash,初期ハッシュ
                var initialHash = await SHA256.HashDataAsync(initial, token).ConfigureAwait(false);
                if (!expected.SequenceEqual(initialHash))
                    return (false, "プロジェクトが外部で変更されました。再読み込みしてください。");
            }
            //CSJ,variable=updated,更新済み
            var updated = RelinkJsonUpdate.Apply(original, rows);
            using (JsonDocument.Parse(updated.AsMemory(updated.AsSpan().StartsWith(new byte[] { 0xef, 0xbb, 0xbf }) ? 3 : 0))) { }
            if (persistPublication is not null)
            {
                await WritePreparedStageAsync(stage!, updated, persistPublication, token).ConfigureAwait(false);
                return (true, null);
            }
            var candidate = Path.Combine(Path.GetDirectoryName(output)!, $".ymmpm-relink.tmp-{Guid.NewGuid():N}");
            //CSJ,variable=stream,ストリーム
            await using var stream = OwnedFilePublication.CreateOwnedOutputFile(candidate, readBack: true);
            //CSJ,variable=published,確定済み
            var published = false;
            try
            {
                await write(stream, updated, token).ConfigureAwait(false);
                await stream.FlushAsync(token).ConfigureAwait(false);
                flush(stream);
                stream.Position = 0;
                //CSJ,variable=stagedHash,準備済みのハッシュ
                var stagedHash = await SHA256.HashDataAsync(stream, token).ConfigureAwait(false);
                if (!SHA256.HashData(updated).SequenceEqual(stagedHash))
                    return (false, "書き込んだ再リンク結果を検証できません。");
                beforeCommit?.Invoke();
                token.ThrowIfCancellationRequested();
                using var current = new FileStream(OwnedFilePublication.OpenOrdinaryPath(source, expectDirectory: false, allowRename: false), FileAccess.Read);
                //CSJ,variable=currentHash,現在のハッシュ
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

    //CSJ,method=WritePreparedStageAsync,準備済み作業段階を書き込む（非同期）
    private async Task WritePreparedStageAsync(string stage, byte[] updated,
        Func<FileStream, CancellationToken, Task> persistPublication, CancellationToken token)
    {
        //CSJ,variable=stream,ストリーム
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
    //CSJ,method=SaveAsync,保存する（非同期）
    public Task<(bool Success, string? ErrorMessage, string? BackupPath)> SaveAsync(RelinkDocumentContext context, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        return Task.FromResult<(bool, string?, string?)>((false, "元ファイルへの上書きは無効です。別名保存してください。", null));
    }

}
