using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using YMMProjectManager.Application;

namespace YMMProjectManager.Infrastructure;

//CSJ,class=ProjectListDrafts,プロジェクト一覧の下書き管理
internal sealed class ProjectListDrafts(string directory)
{
    //CSJ,record=Evidence,証跡
    internal sealed record Evidence(Guid Id, string Hash);
    //CSJ,record=Document,文書
    private sealed record Document(int Version, Guid Id, string? BaseRevision, byte[] Baseline, byte[] Local,
        Dictionary<string, Evidence> Additions,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] Guid? PredecessorId,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] Guid? FamilyId,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] int? Generation);
    //CSJ,record=DraftIdentity,下書き識別情報
    private readonly record struct DraftIdentity(Guid Id, Guid Family, int Generation, Guid? Operation, string ReadyPath);
    //CSJ,record=Envelope,付帯情報付きデータ
    private sealed record Envelope(byte[] Payload, string Sha256);
    //CSJ,record=Draft,下書き
    internal sealed record Draft(ProjectStore Baseline, ProjectStore Local, Dictionary<string, Evidence> Additions);
    //CSJ,field=MaxBytes,最大バイト列
    private const int MaxBytes = 32 * 1024 * 1024;
    //CSJ,field=Options,オプション
    private static readonly JsonSerializerOptions Options = new()
    {
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        AllowDuplicateProperties = false,
    };

    //CSJ,property=DirectoryPath,ディレクトリのパス
    internal string DirectoryPath => directory;

    internal Guid GetReadyId(string path) => RequireCurrent(path).Id;

    internal (Guid Id, Guid? Operation) GetClaimedIdentity(string path)
    {
        //CSJ,variable=identity,識別情報
        var identity = CheckPath(path, claimed: true);
        return (identity.Id, identity.Operation);
    }

    internal string GetReadyPath(string claimedPath) => CheckPath(claimedPath, claimed: true, requireFile: false).ReadyPath;

    //CSJ,method=WriteAsync,書き込む（非同期）
    internal async Task<string> WriteAsync(ProjectStore baseline, ProjectStore local, Dictionary<string, Evidence> evidence,
        string? predecessor = null, Action? beforePublication = null, Action? afterPublication = null,
        CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        if (baseline.Revision is null) throw new InvalidDataException("Recovery draft has no base revision.");
        var id = Guid.NewGuid();
        //CSJ,variable=previous,直前の
        DraftIdentity? previous = predecessor is not null ? RequireCurrent(predecessor) : null;
        if (previous is { } prior)
        {
            if (prior.Generation == int.MaxValue) throw new InvalidDataException("Recovery generation limit reached.");
            await ReadCoreAsync(predecessor!, prior).ConfigureAwait(false);
        }
        var family = previous?.Family ?? id;
        //CSJ,variable=generation,世代
        var generation = previous.HasValue ? previous.Value.Generation + 1 : 0;
        //CSJ,variable=document,文書
        var document = new Document(previous.HasValue ? 2 : 1, id, baseline.Revision.Sha256, JsonProjectRepository.SerializeStore(baseline),
            JsonProjectRepository.SerializeStore(local), evidence, previous?.Id, generation > 0 ? family : null, generation > 0 ? generation : null);
        //CSJ,variable=payload,本体データ
        var payload = JsonSerializer.SerializeToUtf8Bytes(document, Options);
        //CSJ,variable=bytes,バイト列
        var bytes = JsonSerializer.SerializeToUtf8Bytes(new Envelope(payload, Convert.ToHexString(SHA256.HashData(payload))), Options);
        if (bytes.Length > MaxBytes) throw new InvalidDataException("Recovery draft is too large.");
        CheckDirectory();
        Directory.CreateDirectory(directory);
        //CSJ,variable=fence,整合性境界
        using var fence = OwnedFilePublication.DirectoryFence.Open(directory, enforceShare: true);
        //CSJ,variable=name,名前
        var name = generation > 0 ? $"{id:N}.{family:N}.{generation.ToString(CultureInfo.InvariantCulture)}" : $"{id:N}";
        //CSJ,variable=path,パス
        var path = Path.Combine(directory, $"{name}.draft.json");
        //CSJ,variable=temporary,一時
        var temporary = path + ".tmp";
        //CSJ,variable=stream,ストリーム
        await using (var stream = OwnedFilePublication.CreateOwnedOutputFile(temporary))
        {
            try
            {
                await stream.WriteAsync(bytes, token).ConfigureAwait(false);
                await stream.FlushAsync(token).ConfigureAwait(false);
                stream.Flush(flushToDisk: true);
                beforePublication?.Invoke();
                token.ThrowIfCancellationRequested();
                OwnedFilePublication.RenameOpenedPath(stream.SafeFileHandle, path);
            }
            catch
            {
                OwnedFilePublication.TryDeleteOpenedPath(stream.SafeFileHandle);
                throw;
            }
        }
        afterPublication?.Invoke();
        return path;
    }

    //CSJ,method=ReadAsync,読み取る（非同期）
    internal Task<Draft> ReadAsync(string path, bool claimed = false) =>
        ReadCoreAsync(path, RequireCurrent(path, claimed));

    internal async Task<(Draft Draft, bool ExportOnly)> ReadForReviewAsync(string path, bool claimed = false)
    {
        //CSJ,variable=identity,識別情報
        var identity = CheckPath(path, claimed);
        return (await ReadCoreAsync(path, identity).ConfigureAwait(false), !CanReplay(identity, path));
    }

    internal Task<Draft> ReadArchivedAsync(string path) => ReadCoreAsync(path, CheckPath(path, archived: true));

    //CSJ,method=ReadCoreAsync,中核を読み取る（非同期）
    private static async Task<Draft> ReadCoreAsync(string path, DraftIdentity identity)
    {
        //CSJ,variable=fence,整合性境界
        using var fence = OwnedFilePublication.DirectoryFence.Open(Path.GetDirectoryName(path)!, enforceShare: true);
        //CSJ,variable=stream,ストリーム
        using var stream = ProjectListReplacement.Open(path);
        if (stream.Length > MaxBytes) throw new InvalidDataException("Recovery draft is too large.");
        //CSJ,variable=envelope,付帯情報付きデータ
        var envelope = await JsonSerializer.DeserializeAsync<Envelope>(stream, Options).ConfigureAwait(false);
        if (envelope?.Payload is null || envelope.Sha256 != Convert.ToHexString(SHA256.HashData(envelope.Payload)))
            throw new InvalidDataException("Recovery draft checksum is invalid.");
        //CSJ,variable=document,文書
        var document = JsonSerializer.Deserialize<Document>(envelope.Payload, Options);
        if (document is null || !Matches(document, identity) || document.Baseline is null || document.Local is null ||
            document.Additions is null || (document.BaseRevision is not null && !IsHash(document.BaseRevision)))
            throw new InvalidDataException("Recovery draft format or identity is invalid.");
        //CSJ,variable=baseline,基準
        var baseline = JsonProjectRepository.DeserializeStore(document.Baseline);
        //CSJ,variable=local,ローカル
        var local = JsonProjectRepository.DeserializeStore(document.Local);
        if (!baseline.IsRecoverySafe || !local.IsRecoverySafe ||
            (document.BaseRevision is null && (baseline.Projects.Count != 0 || baseline.Folders.Count != 0)) ||
            document.Additions.Any(pair => pair.Value is null || pair.Value.Id == Guid.Empty || !IsHash(pair.Value.Hash)))
            throw new InvalidDataException("Recovery draft contains unsupported data.");
        baseline.Revision = baseline.Revision! with { Sha256 = document.BaseRevision };
        local.Revision = local.Revision! with { Sha256 = document.BaseRevision };
        return new Draft(baseline, local, document.Additions);
    }

    private static bool Matches(Document document, DraftIdentity identity)
    {
        if (document.Id != identity.Id) return false;
        if (identity.Generation == 0)
            return document is { Version: 1, PredecessorId: null, FamilyId: null, Generation: null };
        return document.Version == 2 && document.FamilyId == identity.Family && document.Generation == identity.Generation &&
            document.PredecessorId is Guid previous && previous != Guid.Empty && previous != identity.Id &&
            (previous == identity.Family) == (identity.Generation == 1);
    }

    internal string Claim(string path, Guid operation)
    {
        if (operation == Guid.Empty) throw new ArgumentException("A recovery claim requires an operation ID.", nameof(operation));
        //CSJ,variable=fence,整合性境界
        using var fence = OwnedFilePublication.DirectoryFence.Open(directory, enforceShare: true);
        using var source = OpenDraftForRename(path);
        RequireCurrent(path);
        var claimed = $"{path}.{operation:N}.saving";
        OwnedFilePublication.RenameOpenedPath(source.SafeFileHandle, claimed);
        return claimed;
    }

    internal string ReleaseUnpublishedClaim(string path)
    {
        //CSJ,variable=fence,整合性境界
        using var fence = OwnedFilePublication.DirectoryFence.Open(directory, enforceShare: true);
        using var source = OpenDraftForRename(path, claimed: true);
        RequireCurrent(path, claimed: true);
        var ready = GetReadyPath(path);
        OwnedFilePublication.RenameOpenedPath(source.SafeFileHandle, ready);
        return ready;
    }

    internal void Retire(string path, bool claimed = false)
    {
        //CSJ,variable=fence,整合性境界
        using var fence = OwnedFilePublication.DirectoryFence.Open(directory, enforceShare: true);
        using var source = OpenDraftForRename(path, claimed);
        var closed = claimed ? GetReadyPath(path) + ".closed" : path + ".closed";
        OwnedFilePublication.RenameOpenedPath(source.SafeFileHandle, closed);
    }

    private FileStream OpenDraftForRename(string path, bool claimed = false)
    {
        CheckPath(path, claimed);
        return ProjectListReplacement.Open(path, canRename: true);
    }

    //CSJ,method=RequireCurrent,要求現在の
    private DraftIdentity RequireCurrent(string path, bool claimed = false)
    {
        //CSJ,variable=identity,識別情報
        var identity = CheckPath(path, claimed);
        if (!CanReplay(identity, path))
            throw new InvalidDataException("This recovery generation is superseded or ambiguous. Export it instead of replaying it.");
        return identity;
    }

    private bool CanReplay(DraftIdentity identity, string path)
    {
        //CSJ,variable=full,絶対パス
        var full = Path.GetFullPath(path);
        //CSJ,variable=generations,世代
        var generations = new HashSet<int>();
        var selected = false;
        //CSJ,variable=candidates,候補一覧
        var candidates = PublishedNames($"{identity.Family:N}.draft.json*")
            .Concat(PublishedNames($"*.{identity.Family:N}.*.draft.json*"));
        foreach (var candidate in candidates)
        {
            //CSJ,variable=peer,対向先
            DraftIdentity peer;
            try
            {
                //CSJ,variable=name,名前
                var name = Path.GetFileName(candidate);
                peer = CheckPath(candidate, claimed: name.EndsWith(".saving", StringComparison.Ordinal),
                    archived: name.EndsWith(".closed", StringComparison.Ordinal), requireFile: false);
            }
            catch (InvalidDataException) { return false; }
            // The name of any later published generation fences earlier records, even if a payload is damaged
            // or intermediate generations are missing. Two names for one generation leave its outcome uncertain.
            if (peer.Family != identity.Family || !generations.Add(peer.Generation) || peer.Generation > identity.Generation)
                return false;
            if (peer.Generation != identity.Generation) continue;
            selected = string.Equals(candidate, full, StringComparison.OrdinalIgnoreCase);
            if (!selected) return false;
        }
        return selected;
    }

    //CSJ,method=PublishedNames,確定済みの名前一覧
    private IEnumerable<string> PublishedNames(string pattern) =>
        Directory.EnumerateFileSystemEntries(directory, pattern)
            .Where(path => !Path.GetFileName(path).EndsWith(".tmp", StringComparison.Ordinal));

    //CSJ,method=CheckPath,パスを確認する
    private DraftIdentity CheckPath(string path, bool claimed = false, bool requireFile = true, bool archived = false)
    {
        CheckDirectory();
        //CSJ,variable=full,絶対パス
        var full = Path.GetFullPath(path);
        //CSJ,variable=name,名前
        var name = Path.GetFileName(full);
        //CSJ,variable=operation,操作
        Guid? operation = null;
        if (archived)
        {
            //CSJ,variable=suffix,接尾辞
            const string suffix = ".closed";
            if (!name.EndsWith(suffix, StringComparison.Ordinal)) throw new InvalidDataException("Recovery archive path is invalid.");
            name = name[..^suffix.Length];
        }
        if (claimed)
        {
            if (!name.EndsWith(".saving", StringComparison.Ordinal)) throw new InvalidDataException("Recovery claim is invalid.");
            name = name[..^7];
            //CSJ,variable=separator,区切り
            var separator = name.LastIndexOf('.');
            if (!name.EndsWith(".draft.json", StringComparison.Ordinal) && separator >= 0 &&
                Guid.TryParseExact(name[(separator + 1)..], "N", out var parsed) && parsed != Guid.Empty)
            {
                operation = parsed;
                name = name[..separator];
            }
        }
        //CSJ,variable=extension,拡張子
        const string extension = ".draft.json";
        if (!string.Equals(Path.GetDirectoryName(full), directory, StringComparison.OrdinalIgnoreCase) ||
            !name.EndsWith(extension, StringComparison.Ordinal) ||
            (requireFile && (File.GetAttributes(full) & (FileAttributes.ReparsePoint | FileAttributes.Directory)) != 0))
            throw new InvalidDataException("Recovery draft path is invalid.");
        //CSJ,variable=parts,構成要素
        var parts = name[..^extension.Length].Split('.');
        if (!Guid.TryParseExact(parts[0], "N", out var id) || id == Guid.Empty)
            throw new InvalidDataException("Recovery draft identity is invalid.");
        var family = id;
        //CSJ,variable=generation,世代
        var generation = 0;
        if (parts.Length != 1 &&
            (parts.Length != 3 || !Guid.TryParseExact(parts[1], "N", out family) || family == Guid.Empty || family == id ||
                !int.TryParse(parts[2], NumberStyles.None, CultureInfo.InvariantCulture, out generation) || generation < 1 ||
                parts[2] != generation.ToString(CultureInfo.InvariantCulture)))
            throw new InvalidDataException("Recovery draft generation is invalid.");
        return new DraftIdentity(id, family, generation, operation, Path.Combine(directory, name));
    }

    //CSJ,method=CheckDirectory,ディレクトリを確認する
    private void CheckDirectory() => CheckOrdinaryDirectory(directory);

    //CSJ,method=CheckOrdinaryDirectory,通常ディレクトリを確認する
    internal static void CheckOrdinaryDirectory(string directory)
    {
        for (var item = new DirectoryInfo(directory); item is not null; item = item.Parent)
        {
            //CSJ,variable=attributes,属性
            FileAttributes attributes;
            try { attributes = File.GetAttributes(item.FullName); }
            catch (FileNotFoundException) { continue; }
            catch (DirectoryNotFoundException) { continue; }
            if ((attributes & FileAttributes.ReparsePoint) != 0) throw new IOException("Recovery directory traverses a reparse point.");
        }
    }

    //CSJ,method=IsHash,ハッシュかを判定する
    private static bool IsHash(string? text) => text is { Length: 64 } && text.All(Uri.IsHexDigit);
}
