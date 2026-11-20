using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace YMMProjectManager.Infrastructure;

internal static partial class ProjectListIdentityRecovery
{
    private const int MaximumBytes = 32 * 1024 * 1024;
    private static readonly JsonSerializerOptions OutputOptions = new(JsonSerializerOptions.Default) { WriteIndented = true };

    internal sealed record Entry(int Row, string Path, bool HasMetadata);
    internal sealed record Conflict(Guid Id, IReadOnlyList<Entry> Entries);
    internal sealed record Review(string Sha256, IReadOnlyList<Conflict> Conflicts, IReadOnlyList<Entry> LegacyProjects);

    internal static Review Inspect(byte[] bytes)
    {
        using var input = Load(bytes);
        var projects = ReadProjects(input);

        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var byId = new Dictionary<Guid, List<Entry>>();
        var legacy = new List<Entry>();
        var row = 0;
        foreach (var project in projects.EnumerateArray())
        {
            var path = ReadProjectPath(project);
            if (!paths.Add(Path.GetFullPath(path)))
                throw new InvalidDataException("重複するパスがあります。先に同じファイルの重複登録を確認してください。");

            var index = row++;
            var entry = new Entry(index, path,
                project.TryGetProperty("MetadataAssociation", out var metadata) && metadata.ValueKind != JsonValueKind.Null);
            if (ReadProjectId(project) is not Guid id)
            {
                legacy.Add(entry);
                continue;
            }
            if (!byId.TryGetValue(id, out var group)) byId.Add(id, group = []);
            group.Add(entry);
        }
        return new Review(Convert.ToHexString(SHA256.HashData(bytes)),
            byId.Where(pair => pair.Value.Count > 1).Select(pair => new Conflict(pair.Key, pair.Value.AsReadOnly())).ToArray(), legacy.AsReadOnly());
    }

    internal static byte[] Repair(byte[] confirmedBytes, Review review, IReadOnlyDictionary<Guid, int> keepers,
        IReadOnlySet<int>? legacyRows = null)
    {
        // Recompute rather than trusting row indexes or groups supplied by the screen.
        var current = Inspect(confirmedBytes);
        var eligible = current.LegacyProjects.Where(entry => !entry.HasMetadata).Select(entry => entry.Row).ToHashSet();
        if (current.Sha256 != review.Sha256 || (current.Conflicts.Count == 0 && legacyRows is not { Count: > 0 }) ||
            (legacyRows is not null && !legacyRows.IsSubsetOf(eligible)) ||
            keepers.Count != current.Conflicts.Count ||
            current.Conflicts.Any(group => !keepers.TryGetValue(group.Id, out var row) || !group.Entries.Any(entry => entry.Row == row)))
            throw new InvalidDataException("選択内容が確認した一覧と一致しません。");

        using var input = Load(confirmedBytes);
        var root = JsonNode.Parse(input.RootElement.GetRawText())!.AsObject();
        var projects = root["Projects"]!.AsArray();
        var used = projects.Select(node => node!["ProjectId"]).Where(node => node is not null)
            .Select(node => Guid.Parse(node!.GetValue<string>())).ToHashSet();
        foreach (var group in current.Conflicts)
        {
            foreach (var entry in group.Entries.Where(entry => entry.Row != keepers[group.Id]))
            {
                var project = projects[entry.Row]!.AsObject();
                project["ProjectId"] = NextId(used).ToString("D");
                // The old association belongs to the retained logical project. Keep it in the untouched source list.
                if (entry.HasMetadata) project.Remove("MetadataAssociation");
            }
        }

        foreach (var row in legacyRows ?? new HashSet<int>())
        {
            var project = projects[row]!.AsObject();
            project["ProjectId"] = NextId(used).ToString("D");
            if (project["LinkedYmmpFiles"] is not JsonArray links) continue;
            var explicitIds = links.OfType<JsonObject>().Select(link => link["Id"])
                .OfType<JsonValue>().Select(value => value.TryGetValue<string>(out var raw) && Guid.TryParse(raw, out var id) ? id : Guid.Empty)
                .ToHashSet();
            foreach (var link in links.OfType<JsonObject>().Where(link => link["Id"] is null))
                link["Id"] = NextId(explicitIds).ToString("D");
        }

        var result = JsonSerializer.SerializeToUtf8Bytes(root, OutputOptions);
        if (Inspect(result).Conflicts.Count != 0) throw new InvalidDataException("修復したコピーに重複 ID が残っています。");
        return result;
    }

    internal static async Task ApplyAsync(JsonProjectRepository repository, Review review, IReadOnlyDictionary<Guid, int> keepers,
        string backup, CancellationToken token, IReadOnlySet<int>? legacyRows = null)
    {
        var current = await repository.ReadOriginalAsync(token).ConfigureAwait(false);
        var repaired = await Task.Run(() => JsonProjectRepository.DeserializeStore(Repair(current, review, keepers, legacyRows)), token).ConfigureAwait(false);
        if (!repaired.IsRecoverySafe || repaired.Revision is null)
            throw new InvalidDataException("修復した一覧に、このバージョンで安全に保存できない情報が含まれています。修復版のコピーを作成できます。");

        // The normal repository CAS still applies to the exact reviewed bytes, even though the original could not be loaded.
        repaired.Revision = repaired.Revision with { Sha256 = review.Sha256 };
        await JsonProjectRepository.WriteNewCopyAsync(backup, current, token).ConfigureAwait(false);
        await repository.SaveAsync(repaired, token).ConfigureAwait(false);
    }

    private static Guid NextId(HashSet<Guid> used)
    {
        Guid id;
        do { id = Guid.NewGuid(); } while (!used.Add(id));
        return id;
    }

    private static JsonDocument Load(byte[] bytes)
    {
        if (bytes.Length > MaximumBytes) throw new InvalidDataException("一覧が大きすぎるため、修復できません。");
        var offset = bytes.AsSpan().StartsWith(new byte[] { 0xef, 0xbb, 0xbf }) ? 3 : 0;
        return JsonDocument.Parse(bytes.AsMemory(offset));
    }

    private static JsonElement ReadProjects(JsonDocument input)
    {
        CheckProperties(input.RootElement);
        if (input.RootElement.ValueKind != JsonValueKind.Object ||
            !input.RootElement.TryGetProperty("Projects", out var projects) || projects.ValueKind != JsonValueKind.Array)
            throw new InvalidDataException("一覧の Projects 配列を確認できません。");
        return projects;
    }

    private static string ReadProjectPath(JsonElement project)
    {
        if (project.ValueKind != JsonValueKind.Object ||
            !project.TryGetProperty("FullPath", out var value) || value.ValueKind != JsonValueKind.String ||
            value.GetString() is not { } path || !Path.IsPathFullyQualified(path))
            throw new InvalidDataException("無効なプロジェクトのパスがあります。");
        return path;
    }

    private static Guid? ReadProjectId(JsonElement project)
    {
        if (!project.TryGetProperty("ProjectId", out var value) || value.ValueKind == JsonValueKind.Null)
            return null;
        if (value.ValueKind != JsonValueKind.String || !value.TryGetGuid(out var id) || id == Guid.Empty)
            throw new InvalidDataException("不正なプロジェクト ID があります。");
        return id;
    }

    private static void CheckProperties(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var property in element.EnumerateObject())
            {
                if (!names.Add(property.Name)) throw new InvalidDataException("大文字小文字を除いて同名の JSON 項目があります。");
                CheckProperties(property.Value);
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
            foreach (var item in element.EnumerateArray()) CheckProperties(item);
    }
}
