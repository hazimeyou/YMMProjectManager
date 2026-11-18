using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using YMMProjectManager.Application;
using YMMProjectManager.Domain;
using YMMProjectManager.Infrastructure;

internal static class ProjectListSaveTests
{
    public static async Task RunAsync(string workRoot)
    {
        var root = Path.Combine(workRoot, "project-list-save");
        Directory.CreateDirectory(root);
        var path = Path.Combine(root, "projects.json");
        var logger = new FileLogger(Path.Combine(root, "log.txt"));
        JsonProjectRepository Repo(Func<Stream, byte[], CancellationToken, Task>? writer = null, Action? beforeReplace = null)
            => new(logger, path, writer, beforeReplace);
        ProjectStore Store(string name) => new() { Projects = [new ProjectEntry { FullPath = Path.Combine(root, name + ".ymmp"), DisplayName = name, Pinned = true }] };
        async Task Save(JsonProjectRepository repo, ProjectStore store, CancellationToken token = default)
        {
            store.Revision = (await repo.LoadAsync()).Revision;
            await repo.SaveAsync(store, token);
        }
        void Check(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
        string Hash() => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));
        void NoTemps() => Check(!Directory.EnumerateFiles(root, "projects.json.tmp-*").Any(), "temporary file remains");
        async Task Expect<T>(Func<Task> action) where T : Exception
        {
            try { await action(); } catch (T) { return; }
            throw new InvalidOperationException("Expected " + typeof(T).Name);
        }
        async Task CheckPreserved(string hash, long size, byte[] bytes)
        {
            Check(Hash() == hash && new FileInfo(path).Length == size && File.ReadAllBytes(path).SequenceEqual(bytes), "original changed");
            using var document = JsonDocument.Parse(await File.ReadAllTextAsync(path));
            Check((await Repo().LoadAsync()).Projects.Single().DisplayName == "original", "reload failed");
            NoTemps();
        }

        // T01/T09: new file, parse and reload from a different repository.
        await Save(Repo(), Store("new"));
        using (JsonDocument.Parse(await File.ReadAllTextAsync(path))) { }
        Check((await Repo().LoadAsync()).Projects.Single().DisplayName == "new", "new save"); NoTemps();
        // T02/T11: successive replacements.
        await Save(Repo(), Store("first")); await Save(Repo(), Store("original"));
        Check((await Repo().LoadAsync()).Projects.Single().DisplayName == "original", "replacement"); NoTemps();
        var bytes = await File.ReadAllBytesAsync(path); var hash = Hash(); var size = bytes.LongLength;
        // T03: pre-cancellation.
        using (var cts = new CancellationTokenSource()) { cts.Cancel(); await Expect<OperationCanceledException>(() => Save(Repo(), Store("lost"), cts.Token)); }
        await CheckPreserved(hash, size, bytes);
        // T04: deterministically cancel after writing part of the temporary file.
        using (var cts = new CancellationTokenSource())
        {
            var repo = Repo(async (stream, data, token) => { await stream.WriteAsync(data.AsMemory(0, data.Length / 2), token); cts.Cancel(); token.ThrowIfCancellationRequested(); });
            await Expect<OperationCanceledException>(() => Save(repo, Store("lost"), cts.Token));
        }
        await CheckPreserved(hash, size, bytes);
        // T05/T08: synthetic partial write and disk-full-like IOException.
        var failing = Repo(async (stream, data, token) => { await stream.WriteAsync(data.AsMemory(0, data.Length / 2), token); throw new IOException("synthetic disk full"); });
        await Expect<IOException>(() => Save(failing, Store("lost"))); await CheckPreserved(hash, size, bytes);
        // T06: successful temporary write, then failure immediately before replacement.
        await Expect<IOException>(() => Save(Repo(beforeReplace: () => throw new IOException("synthetic pre-replace failure")), Store("lost")));
        await CheckPreserved(hash, size, bytes);
        using (var cts = new CancellationTokenSource())
            await Expect<OperationCanceledException>(() => Save(Repo(beforeReplace: cts.Cancel), Store("lost"), cts.Token));
        await CheckPreserved(hash, size, bytes);
        // T07: Windows sharing restriction denies the final replacement.
        var lockedStore = Store("lost"); lockedStore.Revision = (await Repo().LoadAsync()).Revision;
        using (var locked = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
            await Expect<IOException>(() => Repo().SaveAsync(lockedStore));
        await CheckPreserved(hash, size, bytes);
        using (var locked = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None))
            await Expect<IOException>(() => Repo().LoadAsync());
        await CheckPreserved(hash, size, bytes);
        // T12: separate repository instances serialize writes; final document is a complete submitted snapshot.
        var concurrentRevision = (await Repo().LoadAsync()).Revision;
        async Task<bool> Compete(int i) { var store = Store("concurrent" + i); store.Revision = concurrentRevision; try { await Repo().SaveAsync(store); return true; } catch (ProjectListConflictException) { return false; } }
        var winners = await Task.WhenAll(Enumerable.Range(0, 12).Select(Compete));
        Check(winners.Count(x => x) == 1, "stale concurrent writes were accepted");
        var concurrent = await Repo().LoadAsync(); Check(concurrent.Projects.Count == 1 && concurrent.Projects[0].DisplayName!.StartsWith("concurrent", StringComparison.Ordinal), "concurrent save"); NoTemps();
        // T13: old-form fields remain compatible after a round trip.
        await File.WriteAllTextAsync(path, "{\"Projects\":[{\"FullPath\":\"C:\\\\Temp\\\\legacy.ymmp\",\"DisplayName\":\"legacy\",\"Pinned\":true,\"LastAccess\":\"2026-06-08T00:00:00+09:00\"}]}");
        var legacy = await Repo().LoadAsync(); await Repo().SaveAsync(legacy); var roundtrip = await Repo().LoadAsync();
        Check(roundtrip.Projects.Single().DisplayName == "legacy" && roundtrip.Projects[0].Pinned && roundtrip.Projects[0].LastAccess == legacy.Projects[0].LastAccess && roundtrip.Projects[0].LinkedYmmpFiles.Count > 0, "legacy roundtrip");
        Check(roundtrip.Projects[0].ProjectId is null, "legacy identity must remain unresolved");
        var id1 = Guid.NewGuid(); var id2 = Guid.NewGuid();
        var identified = new ProjectStore { Projects = [new ProjectEntry { FullPath = Path.Combine(root, "one", "same.ymmp"), ProjectId = id1 }, new ProjectEntry { FullPath = Path.Combine(root, "two", "same.ymmp"), ProjectId = id2 }] };
        await Save(Repo(), identified);
        var reloaded = await Repo().LoadAsync();
        Check(reloaded.Projects.Select(p => p.ProjectId).SequenceEqual(new Guid?[] { id1, id2 }), "identity roundtrip");
        var identityHash = Hash();
        identified.Projects[1].ProjectId = id1;
        await Expect<InvalidDataException>(() => Repo().SaveAsync(identified));
        Check(Hash() == identityHash, "duplicate identity modified list");
        identified.Projects[1].ProjectId = Guid.Empty;
        await Expect<InvalidDataException>(() => Repo().SaveAsync(identified));
        Check(Hash() == identityHash, "empty identity modified list");
        identified.Projects[1].ProjectId = id2; identified.Projects[1].FullPath = "";
        await Expect<InvalidDataException>(() => Repo().SaveAsync(identified));
        Check(Hash() == identityHash, "identified project without path modified list");
        await File.WriteAllTextAsync(path, $"{{\"Projects\":[{{\"FullPath\":\"one.ymmp\",\"ProjectId\":\"{id1}\"}},{{\"FullPath\":\"two.ymmp\",\"ProjectId\":\"{id1}\"}}]}}");
        var duplicateHash = Hash(); await Expect<InvalidDataException>(() => Repo().LoadAsync()); Check(Hash() == duplicateHash, "duplicate load modified list");
        // Corrupt read must not silently become a new empty store.
        await File.WriteAllTextAsync(path, "{invalid"); var corruptHash=Hash();
        await Expect<JsonException>(() => Repo().LoadAsync()); Check(Hash()==corruptHash, "corrupt input changed");
        Console.WriteLine("Project list save regression checks passed.");
    }
}
