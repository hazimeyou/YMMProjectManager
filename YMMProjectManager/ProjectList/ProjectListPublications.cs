using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace YMMProjectManager.Infrastructure;

//CSJ,class=ProjectListPublications,プロジェクト一覧の確定記録
internal sealed class ProjectListPublications(string directory)
{
    //CSJ,enum=Outcome,処理結果
    internal enum Outcome { Unknown, Unpublished, Committed }
    //CSJ,record=Receipt,一覧保存の照合記録
    internal sealed record Receipt(int Version, Guid Id, Guid? DraftId, string? Expected, string Result,
        [property: JsonRequired] bool PublicationStarted,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] Guid? ExpectedOperation = null,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] ProjectListReplacement.FileVersion? Candidate = null);
    //CSJ,record=Envelope,付帯情報付きデータ
    private sealed record Envelope(byte[] Payload, string Hash);
    //CSJ,field=Options,オプション
    private static readonly JsonSerializerOptions Options = new()
    {
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        AllowDuplicateProperties = false,
    };

    //CSJ,method=WriteAsync,書き込む（非同期）
    internal async Task WriteAsync(Receipt receipt, CancellationToken token, Action? afterPartialWrite = null,
        //CSJ,parameter=beforePublication,確定反映前の処理
        Action? beforePublication = null)
    {
        token.ThrowIfCancellationRequested();
        ProjectListDrafts.CheckOrdinaryDirectory(directory);
        Directory.CreateDirectory(directory);
        //CSJ,variable=fence,整合性境界
        using var fence = OwnedFilePublication.DirectoryFence.Open(directory, enforceShare: true);
        //CSJ,variable=pendingPath,保留パス
        var pendingPath = Path.Combine(directory, PendingName(receipt.DraftId, receipt.Id));
        //CSJ,variable=path,パス
        var path = receipt.PublicationStarted ? Path.Combine(directory, Name(receipt.DraftId, receipt.Id)) : pendingPath;
        //CSJ,variable=temporary,一時
        var temporary = Path.Combine(directory, $".{receipt.Id:N}.tmp");
        //CSJ,variable=payload,本体データ
        var payload = JsonSerializer.SerializeToUtf8Bytes(receipt, Options);
        //CSJ,variable=bytes,バイト列
        var bytes = JsonSerializer.SerializeToUtf8Bytes(new Envelope(payload, Digest(payload)), Options);
        //CSJ,variable=stream,ストリーム
        await using var stream = OwnedFilePublication.CreateOwnedOutputFile(temporary);
        //CSJ,variable=pending,保留
        FileStream? pending = null;
        //CSJ,variable=published,確定済み
        var published = false;
        try
        {
            if (afterPartialWrite is null)
                await stream.WriteAsync(bytes, token).ConfigureAwait(false);
            else
            {
                await stream.WriteAsync(bytes.AsMemory(0, bytes.Length / 2), token).ConfigureAwait(false);
                afterPartialWrite();
                await stream.WriteAsync(bytes.AsMemory(bytes.Length / 2), token).ConfigureAwait(false);
            }
            stream.Flush(flushToDisk: true);
            token.ThrowIfCancellationRequested();
            if (receipt.PublicationStarted)
            {
                pending = OpenReceipt(pendingPath, canRename: true);
                //CSJ,variable=initial,初期
                var initial = await ReadAsync(pending, pendingPath, token).ConfigureAwait(false);
                if (initial != receipt with { PublicationStarted = false, Candidate = null })
                    throw new InvalidDataException("The prepared publication receipt changed.");
                token.ThrowIfCancellationRequested();
                // Consume the only name that proves publication has not started. If this process
                // stops before publishing the next receipt, recovery must preserve the claim as unknown.
                OwnedFilePublication.RenameOpenedPath(pending.SafeFileHandle, Path.Combine(directory, SealedName(receipt.DraftId, receipt.Id)));
            }
            beforePublication?.Invoke();
            token.ThrowIfCancellationRequested();
            OwnedFilePublication.RenameOpenedPath(stream.SafeFileHandle, path);
            published = true;
            if (pending is not null) OwnedFilePublication.TryDeleteOpenedPath(pending.SafeFileHandle);
        }
        finally
        {
            pending?.Dispose();
            if (!published) OwnedFilePublication.TryDeleteOpenedPath(stream.SafeFileHandle);
        }
    }

    internal async Task<Outcome> ReconcileAsync(Guid draftId, Guid? boundOperation, string? current, Guid? currentOperation,
        Func<Guid, ProjectListReplacement.FileVersion, CancellationToken, Task<bool>> verifyUnconsumedCandidate, CancellationToken token)
    {
        ProjectListDrafts.CheckOrdinaryDirectory(directory);
        if (!Directory.Exists(directory)) return Outcome.Unknown;
        //CSJ,variable=fence,整合性境界
        using var fence = OwnedFilePublication.DirectoryFence.Open(directory, enforceShare: true);
        //CSJ,variable=known,既知の
        var known = false;
        var unpublished = true;
        var committed = false;
        var operations = new HashSet<Guid>();
        // Older claims did not name an operation. Keep their conservative all-receipts check.
        //CSJ,variable=pattern,パターン
        var pattern = boundOperation is { } operation ? $"{Prefix(draftId)}{operation:N}" : $"{Prefix(draftId)}*";
        //CSJ,variable=paths,パス一覧
        var paths = Directory.EnumerateFiles(directory, pattern + ".publication.json")
            .Concat(Directory.EnumerateFiles(directory, pattern + ".publication-pending.json"));
        if (boundOperation is null && Directory.EnumerateFileSystemEntries(directory, pattern + ".publication-sealed.json").Any())
            return Outcome.Unknown;
        if (boundOperation is not null)
        {
            //CSJ,variable=matching,一致する
            var matching = paths.Take(2).ToArray();
            if (matching.Length != 1) return Outcome.Unknown;
            paths = matching;
        }
        //CSJ,variable=path,パス
        foreach (var path in paths)
        {
            token.ThrowIfCancellationRequested();
            //CSJ,variable=receipt,照合記録
            var receipt = await ReadAsync(path, token).ConfigureAwait(false);
            if (receipt.DraftId != draftId || (boundOperation is { } required && receipt.Id != required) || !operations.Add(receipt.Id))
                throw new InvalidDataException("Invalid publication receipt binding.");
            if (Path.GetFileName(path) == PendingName(receipt.DraftId, receipt.Id) &&
                HasFileSystemEntry(Path.Combine(directory, SealedName(receipt.DraftId, receipt.Id))))
                return Outcome.Unknown;
            known = true;
            committed |= receipt.Result == current && receipt.Id == currentOperation;
            if (boundOperation is not null)
            {
                if (committed || await IsPublishedAncestorAsync(receipt, current, currentOperation, token).ConfigureAwait(false))
                    return Outcome.Committed;
                if (!receipt.PublicationStarted) return Outcome.Unpublished;
            }
            // Publication consumes the staged file. Only its recorded identity and contents can prove that
            // it is still unconsumed: a later restore can also have the exact base bytes at the active name.
            unpublished = unpublished && receipt.Expected == current &&
                (!receipt.PublicationStarted || (receipt.Candidate is { } candidate &&
                    await verifyUnconsumedCandidate(receipt.Id, candidate, token).ConfigureAwait(false)));
        }
        return committed ? Outcome.Committed : known && unpublished ? Outcome.Unpublished : Outcome.Unknown;
    }

    // Only a chain ending in the actual current bytes can prove publication after a later save.
    // Missing, corrupt or duplicate links count as no proof.
    private async Task<bool> IsPublishedAncestorAsync(Receipt ancestor, string? currentHash, Guid? currentOperation, CancellationToken token)
    {
        if (!ancestor.PublicationStarted || currentHash is null) return false;
        var visited = new HashSet<Guid>();
        const int maxReceipts = 128;
        for (var i = 0; i < maxReceipts && currentOperation is { } operation && visited.Add(operation); i++)
        {
            token.ThrowIfCancellationRequested();
            if (operation == ancestor.Id) return currentHash == ancestor.Result;
            //CSJ,variable=candidates,候補一覧
            var candidates = Directory.EnumerateFiles(directory, $"*-{operation:N}.publication.json").Take(2).ToArray();
            if (candidates.Length != 1) return false;
            Receipt current;
            try { current = await ReadAsync(candidates[0], token).ConfigureAwait(false); }
            catch (Exception ex) when (ex is InvalidDataException or JsonException or FileNotFoundException) { return false; }
            if (current.Id != operation || !current.PublicationStarted || current.Result != currentHash || current.Expected is null)
                return false;
            currentHash = current.Expected;
            currentOperation = current.ExpectedOperation;
        }
        return false;
    }

    //CSJ,method=ReadAsync,読み取る（非同期）
    private static async Task<Receipt> ReadAsync(string path, CancellationToken token)
    {
        //CSJ,variable=stream,ストリーム
        using var stream = OpenReceipt(path);
        return await ReadAsync(stream, path, token).ConfigureAwait(false);
    }

    //CSJ,method=OpenReceipt,照合記録を開く
    private static FileStream OpenReceipt(string path, bool canRename = false)
    {
        try { return ProjectListReplacement.Open(path, canRename); }
        catch (IOException ex) when (ex.InnerException is System.ComponentModel.Win32Exception { NativeErrorCode: 2 or 3 })
        { throw new FileNotFoundException("Publication receipt was not found.", path, ex); }
    }

    //CSJ,method=ReadAsync,読み取る（非同期）
    private static async Task<Receipt> ReadAsync(FileStream stream, string path, CancellationToken token)
    {
        if (stream.Length > 4096) throw new InvalidDataException("Invalid publication receipt length.");
        //CSJ,variable=envelope,付帯情報付きデータ
        var envelope = await JsonSerializer.DeserializeAsync<Envelope>(stream, Options, token).ConfigureAwait(false);
        if (envelope?.Payload is null || Digest(envelope.Payload) != envelope.Hash)
            throw new InvalidDataException("Invalid publication receipt checksum.");
        //CSJ,variable=receipt,照合記録
        var receipt = JsonSerializer.Deserialize<Receipt>(envelope.Payload, Options);
        if (receipt is not { Version: 1 or 2 or 3 } || receipt.Id == Guid.Empty || receipt.DraftId == Guid.Empty ||
            receipt.ExpectedOperation == Guid.Empty || receipt.ExpectedOperation == receipt.Id ||
            (receipt.ExpectedOperation is not null && (receipt.Version == 1 || receipt.Expected is null)) ||
            (Path.GetFileName(path) != Name(receipt.DraftId, receipt.Id) &&
                (receipt.PublicationStarted || Path.GetFileName(path) != PendingName(receipt.DraftId, receipt.Id))) ||
            !IsDigest(receipt.Result) || (receipt.Expected is not null && !IsDigest(receipt.Expected)) ||
            (receipt.Candidate is not null) != (receipt.Version == 3 && receipt.PublicationStarted) ||
            (receipt.Candidate is { } candidate && (candidate.FileId == Guid.Empty || candidate.Sha256 != receipt.Result)))
            throw new InvalidDataException("Invalid publication receipt contents.");
        return receipt;
    }

    //CSJ,method=HasFileSystemEntry,ファイルシステム項目の有無を判定する
    private static bool HasFileSystemEntry(string path)
    {
        try { _ = File.GetAttributes(path); return true; }
        catch (FileNotFoundException) { return false; }
        catch (DirectoryNotFoundException) { return false; }
    }

    private static bool IsDigest(string? value) => value is { Length: 64 } && value.All(Uri.IsHexDigit);
    private static string Digest(byte[] data) => Convert.ToHexString(SHA256.HashData(data));
    //CSJ,method=Prefix,接頭辞
    private static string Prefix(Guid? draftId) => draftId is { } id ? $"{id:N}-" : "standalone-";
    //CSJ,method=Name,名前
    private static string Name(Guid? draftId, Guid id) => $"{Prefix(draftId)}{id:N}.publication.json";
    //CSJ,method=PendingName,保留名前
    private static string PendingName(Guid? draftId, Guid id) => $"{Prefix(draftId)}{id:N}.publication-pending.json";
    private static string SealedName(Guid? draftId, Guid id) => $"{Prefix(draftId)}{id:N}.publication-sealed.json";
}
