using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace YMMProjectManager.Infrastructure;

internal static partial class ProjectListIdentityRecovery
{
    internal sealed record PhysicalEntry(int Row, string Path, Guid? ProjectId, bool HasMetadata, int LinkCount);
    internal sealed record PhysicalConflict(ulong Volume, Guid FileId, IReadOnlyList<PhysicalEntry> Entries);
    internal sealed record PhysicalReview(string Sha256, IReadOnlyList<PhysicalConflict> Conflicts,
        IReadOnlyList<PhysicalConflict> UnverifiedRecordedDuplicates, int UnavailableCount);

    private sealed record PhysicalObservation(PhysicalEntry Entry, (ulong Volume, Guid FileId)? Recorded,
        (ulong Volume, Guid FileId)? Observed);

    internal static Task<PhysicalReview> InspectPhysicalAsync(byte[] bytes, CancellationToken token = default) =>
        Task.Run(() => InspectPhysical(bytes, token), token);

    private static PhysicalReview InspectPhysical(byte[] bytes, CancellationToken token)
    {
        var original = Inspect(bytes);
        using var input = Load(bytes);
        var observations = new List<PhysicalObservation>();
        foreach (var project in input.RootElement.GetProperty("Projects").EnumerateArray())
        {
            token.ThrowIfCancellationRequested();
            var path = project.GetProperty("FullPath").GetString()!;
            var entry = new PhysicalEntry(observations.Count, path,
                project.TryGetProperty("ProjectId", out var id) && id.ValueKind == JsonValueKind.String ? id.GetGuid() : null,
                project.TryGetProperty("MetadataAssociation", out var metadata) && metadata.ValueKind != JsonValueKind.Null,
                project.TryGetProperty("LinkedYmmpFiles", out var links) && links.ValueKind == JsonValueKind.Array ? links.GetArrayLength() : 0);
            observations.Add(new PhysicalObservation(entry, GetRecordedPhysicalId(project), TryObservePhysicalId(path)));
        }

        var conflicts = new List<PhysicalConflict>();
        // A matching file ID is usable only when nothing recorded for these rows points at a different file.
        var unresolved = new Dictionary<(ulong Volume, Guid FileId), HashSet<PhysicalEntry>>();
        foreach (var group in observations.Where(item => item.Observed.HasValue).GroupBy(item => item.Observed!.Value))
        {
            if (group.Count() < 2) continue;
            if (group.All(item => !item.Recorded.HasValue || item.Recorded == group.Key))
                conflicts.Add(new PhysicalConflict(group.Key.Volume, group.Key.FileId, group.Select(item => item.Entry).ToArray()));
            else
                unresolved[group.Key] = group.Select(item => item.Entry).ToHashSet();
        }
        foreach (var group in observations.Where(item => item.Recorded.HasValue).GroupBy(item => item.Recorded!.Value))
        {
            if (group.Count() < 2 || group.All(item => item.Observed == group.Key)) continue;
            if (!unresolved.TryGetValue(group.Key, out var rows)) unresolved.Add(group.Key, rows = []);
            rows.UnionWith(group.Select(item => item.Entry));
        }
        return new PhysicalReview(original.Sha256, conflicts,
            unresolved.Select(pair => new PhysicalConflict(pair.Key.Volume, pair.Key.FileId, pair.Value.OrderBy(entry => entry.Row).ToArray())).ToArray(),
            observations.Count(item => !item.Observed.HasValue));
    }

    internal static async Task ApplyPhysicalAsync(JsonProjectRepository repository, PhysicalReview review,
        IReadOnlyDictionary<(ulong Volume, Guid FileId), int> keepers, string backup, CancellationToken token)
    {
        var current = await repository.ReadOriginalAsync(token).ConfigureAwait(false);
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

    internal static async Task ExportPhysicalRepairAsync(JsonProjectRepository repository, PhysicalReview review,
        IReadOnlyDictionary<(ulong Volume, Guid FileId), int> keepers, string destination, CancellationToken token)
    {
        var current = await repository.ReadOriginalAsync(token).ConfigureAwait(false);
        using var plan = await PreparePhysicalRepairAsync(current, review, keepers, token).ConfigureAwait(false);
        await JsonProjectRepository.WriteNewCopyAsync(destination, plan.RepairedBytes, token).ConfigureAwait(false);
    }

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

            var leases = new List<FileStream>();
            try
            {
                var remove = new List<int>();
                foreach (var group in current.Conflicts)
                {
                    var identity = (group.Volume, group.FileId);
                    foreach (var entry in group.Entries)
                    {
                        token.ThrowIfCancellationRequested();
                        var lease = OpenLocalProject(entry.Path);
                        leases.Add(lease);
                        if (ProjectFileEvidence.ReadPhysicalIdentity(lease) != identity)
                            throw new InvalidDataException("確認したプロジェクトファイルが変わりました。再度確認してください。");
                        if (entry.Row != keepers[identity]) remove.Add(entry.Row);
                    }
                }

                using var input = Load(bytes);
                var root = JsonNode.Parse(input.RootElement.GetRawText())!.AsObject();
                var projects = root["Projects"]!.AsArray();
                foreach (var row in remove.OrderDescending()) projects.RemoveAt(row);
                return new PhysicalRepair(JsonSerializer.SerializeToUtf8Bytes(root, OutputOptions), leases);
            }
            catch
            {
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

    private static (ulong Volume, Guid FileId)? TryObservePhysicalId(string path)
    {
        try
        {
            using var lease = OpenLocalProject(path);
            return ProjectFileEvidence.ReadPhysicalIdentity(lease);
        }
        catch (IOException) { return null; }
        catch (UnauthorizedAccessException) { return null; }
    }

    private static FileStream OpenLocalProject(string path)
    {
        var root = Path.GetPathRoot(path);
        // A review of a list must not connect to network locations.
        if (root is not { Length: 3 } || !char.IsAsciiLetter(root[0]) || root[1] != ':' ||
            new DriveInfo(root).DriveType != DriveType.Fixed)
            throw new IOException("Physical registration repair requires an ordinary local file.");
        return ProjectFileEvidence.OpenRead(path);
    }

    private sealed class PhysicalRepair(byte[] repairedBytes, List<FileStream> leases) : IDisposable
    {
        internal byte[] RepairedBytes { get; } = repairedBytes;

        public void Dispose()
        {
            foreach (var lease in leases) lease.Dispose();
        }
    }
}
