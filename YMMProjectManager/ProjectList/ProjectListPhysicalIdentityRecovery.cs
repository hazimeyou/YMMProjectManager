using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace YMMProjectManager.Infrastructure;

//CSJ,class=ProjectListIdentityRecovery,プロジェクト一覧識別情報復旧
internal static partial class ProjectListIdentityRecovery
{
    //CSJ,record=PhysicalEntry,ファイル実体の項目
    internal sealed record PhysicalEntry(int Row, string Path, Guid? ProjectId, bool HasMetadata, int LinkCount);
    //CSJ,record=PhysicalConflict,物理競合
    internal sealed record PhysicalConflict(ulong Volume, Guid FileId, IReadOnlyList<PhysicalEntry> Entries);
    //CSJ,record=PhysicalReview,ファイル実体の照合結果
    internal sealed record PhysicalReview(string Sha256, IReadOnlyList<PhysicalConflict> Conflicts,
        IReadOnlyList<PhysicalConflict> UnverifiedRecordedDuplicates, int UnavailableCount);

    //CSJ,record=PhysicalObservation,物理観測記録
    private sealed record PhysicalObservation(PhysicalEntry Entry, (ulong Volume, Guid FileId)? Recorded,
        (ulong Volume, Guid FileId)? Observed);

    //CSJ,method=InspectPhysicalAsync,物理を点検する（非同期）
    internal static Task<PhysicalReview> InspectPhysicalAsync(byte[] bytes, CancellationToken token = default) =>
        Task.Run(() => InspectPhysical(bytes, token), token);

    //CSJ,method=InspectPhysical,物理を点検する
    private static PhysicalReview InspectPhysical(byte[] bytes, CancellationToken token)
    {
        using var input = Load(bytes);
        var observations = new List<PhysicalObservation>();
        //CSJ,variable=project,プロジェクト
        foreach (var project in ReadProjects(input).EnumerateArray())
        {
            token.ThrowIfCancellationRequested();
            //CSJ,variable=path,パス
            var path = ReadProjectPath(project);
            var entry = new PhysicalEntry(observations.Count, path,
                ReadProjectId(project),
                project.TryGetProperty("MetadataAssociation", out var metadata) && metadata.ValueKind != JsonValueKind.Null,
                project.TryGetProperty("LinkedYmmpFiles", out var links) && links.ValueKind == JsonValueKind.Array ? links.GetArrayLength() : 0);
            observations.Add(new PhysicalObservation(entry, GetRecordedPhysicalId(project), TryObservePhysicalId(path)));
        }

        var conflicts = new List<PhysicalConflict>();
        // A matching file ID is usable only when nothing recorded for these rows points at a different file.
        //CSJ,variable=unresolved,未解決
        var unresolved = new Dictionary<(ulong Volume, Guid FileId), HashSet<PhysicalEntry>>();
        //CSJ,variable=group,グループ
        foreach (var group in observations.Where(item => item.Observed.HasValue).GroupBy(item => item.Observed!.Value))
        {
            if (group.Count() < 2) continue;
            if (group.All(item => !item.Recorded.HasValue || item.Recorded == group.Key))
                conflicts.Add(new PhysicalConflict(group.Key.Volume, group.Key.FileId, group.Select(item => item.Entry).ToArray()));
            else
                unresolved[group.Key] = group.Select(item => item.Entry).ToHashSet();
        }
        //CSJ,variable=group,グループ
        foreach (var group in observations.Where(item => item.Recorded.HasValue).GroupBy(item => item.Recorded!.Value))
        {
            if (group.Count() < 2 || group.All(item => item.Observed == group.Key)) continue;
            if (!unresolved.TryGetValue(group.Key, out var rows)) unresolved.Add(group.Key, rows = []);
            rows.UnionWith(group.Select(item => item.Entry));
        }
        return new PhysicalReview(Convert.ToHexString(SHA256.HashData(bytes)), conflicts,
            unresolved.Select(pair => new PhysicalConflict(pair.Key.Volume, pair.Key.FileId, pair.Value.OrderBy(entry => entry.Row).ToArray())).ToArray(),
            observations.Count(item => !item.Observed.HasValue));
    }

    //CSJ,method=ApplyPhysicalAsync,物理を適用する（非同期）
    internal static async Task ApplyPhysicalAsync(JsonProjectRepository repository, PhysicalReview review,
        IReadOnlyDictionary<(ulong Volume, Guid FileId), int> keepers, string backup, CancellationToken token)
    {
        var current = await repository.ReadOriginalAsync(token).ConfigureAwait(false);
        //CSJ,variable=plan,計画
        using var plan = await PreparePhysicalRepairAsync(current, review, keepers, token).ConfigureAwait(false);
        if (review.UnverifiedRecordedDuplicates.Count != 0)
            throw new InvalidDataException("現物を確認できない重複が残っています。確認済みの選択は別ファイルに保存できます。");

        var repaired = JsonProjectRepository.DeserializeStore(plan.RepairedBytes);
        if (!repaired.IsRecoverySafe || repaired.Revision is null)
            throw new InvalidDataException("修復した一覧にはこのバージョンで安全に保存できない情報があります。別ファイルに保存できます。");

        repaired.Revision = repaired.Revision with { Sha256 = review.Sha256 };
        await JsonProjectRepository.WriteNewCopyAsync(backup, current, token).ConfigureAwait(false);
        await repository.SaveAsync(repaired, token).ConfigureAwait(false);
    }

    //CSJ,method=ExportPhysicalRepairAsync,物理修復を書き出す（非同期）
    internal static async Task ExportPhysicalRepairAsync(JsonProjectRepository repository, PhysicalReview review,
        IReadOnlyDictionary<(ulong Volume, Guid FileId), int> keepers, string destination, CancellationToken token)
    {
        var current = await repository.ReadOriginalAsync(token).ConfigureAwait(false);
        //CSJ,variable=plan,計画
        using var plan = await PreparePhysicalRepairAsync(current, review, keepers, token).ConfigureAwait(false);
        await JsonProjectRepository.WriteNewCopyAsync(destination, plan.RepairedBytes, token).ConfigureAwait(false);
    }

    //CSJ,method=PreparePhysicalRepairAsync,物理修復を準備する（非同期）
    private static Task<PhysicalRepair> PreparePhysicalRepairAsync(byte[] bytes, PhysicalReview review,
        IReadOnlyDictionary<(ulong Volume, Guid FileId), int> keepers, CancellationToken token) =>
        Task.Run(() =>
        {
            var current = InspectPhysical(bytes, token);
            if (current.Sha256 != review.Sha256 || current.Conflicts.Count == 0 || keepers.Count != current.Conflicts.Count ||
                !SamePhysicalGroups(current.Conflicts, review.Conflicts) ||
                !SamePhysicalGroups(current.UnverifiedRecordedDuplicates, review.UnverifiedRecordedDuplicates) ||
                current.Conflicts.Any(group => !keepers.TryGetValue((group.Volume, group.FileId), out var row) ||
                    !group.Entries.Any(entry => entry.Row == row)))
                throw new InvalidDataException("一覧またはファイルの状態が確認時から変わりました。再度確認してください。");

            //CSJ,variable=leases,リース一覧
            var leases = new List<FileStream>();
            try
            {
                //CSJ,variable=remove,除去
                var remove = new List<int>();
                //CSJ,variable=group,グループ
                foreach (var group in current.Conflicts)
                {
                    //CSJ,variable=identity,識別情報
                    var identity = (group.Volume, group.FileId);
                    foreach (var entry in group.Entries)
                    {
                        token.ThrowIfCancellationRequested();
                        //CSJ,variable=lease,リース
                        var lease = OpenLocalProject(entry.Path);
                        leases.Add(lease);
                        if (ProjectFileEvidence.ReadPhysicalIdentity(lease) != identity)
                            throw new InvalidDataException("確認したプロジェクトファイルが変わりました。再度確認してください。");
                        if (entry.Row != keepers[identity]) remove.Add(entry.Row);
                    }
                }

                using var input = Load(bytes);
                //CSJ,variable=root,ルート
                var root = JsonNode.Parse(input.RootElement.GetRawText())!.AsObject();
                //CSJ,variable=projects,プロジェクト一覧
                var projects = root["Projects"]!.AsArray();
                foreach (var row in remove.OrderDescending()) projects.RemoveAt(row);
                return new PhysicalRepair(JsonSerializer.SerializeToUtf8Bytes(root, OutputOptions), leases);
            }
            catch
            {
                //CSJ,variable=lease,リース
                foreach (var lease in leases) lease.Dispose();
                throw;
            }
        }, token);

    private static bool SamePhysicalGroups(IReadOnlyList<PhysicalConflict> current, IReadOnlyList<PhysicalConflict> reviewed) =>
        current.Count == reviewed.Count && current.Zip(reviewed).All(pair =>
            pair.First.Volume == pair.Second.Volume && pair.First.FileId == pair.Second.FileId &&
            pair.First.Entries.SequenceEqual(pair.Second.Entries));

    private static (ulong Volume, Guid FileId)? GetRecordedPhysicalId(JsonElement project)
    {
        if (project.TryGetProperty("FileIdentity", out var identity) && identity.ValueKind == JsonValueKind.Object &&
            identity.TryGetProperty("Volume", out var volume) && volume.ValueKind == JsonValueKind.Number && volume.TryGetUInt64(out var number) &&
            identity.TryGetProperty("FileId", out var fileId) && fileId.ValueKind == JsonValueKind.String &&
            fileId.TryGetGuid(out var id) && id != Guid.Empty)
            return (number, id);
        return null;
    }

    //CSJ,method=TryObservePhysicalId,物理IDの観測を試みる
    private static (ulong Volume, Guid FileId)? TryObservePhysicalId(string path)
    {
        try
        {
            //CSJ,variable=lease,リース
            using var lease = OpenLocalProject(path);
            return ProjectFileEvidence.ReadPhysicalIdentity(lease);
        }
        catch (IOException) { return null; }
        catch (UnauthorizedAccessException) { return null; }
    }

    //CSJ,method=OpenLocalProject,ローカルプロジェクトを開く
    private static FileStream OpenLocalProject(string path)
    {
        //CSJ,variable=root,ルート
        var root = Path.GetPathRoot(path);
        // A review of a list must not connect to network locations.
        if (root is not { Length: 3 } || !char.IsAsciiLetter(root[0]) || root[1] != ':' ||
            new DriveInfo(root).DriveType != DriveType.Fixed)
            throw new IOException("Physical registration repair requires an ordinary local file.");
        return ProjectFileEvidence.OpenRead(path);
    }

    //CSJ,class=PhysicalRepair,物理修復
    private sealed class PhysicalRepair(byte[] repairedBytes, List<FileStream> leases) : IDisposable
    {
        internal byte[] RepairedBytes { get; } = repairedBytes;

        //CSJ,method=Dispose,破棄する
        public void Dispose()
        {
            //CSJ,variable=lease,リース
            foreach (var lease in leases) lease.Dispose();
        }
    }
}
