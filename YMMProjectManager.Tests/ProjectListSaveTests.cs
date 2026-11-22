using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using YMMProjectManager.Application;
using YMMProjectManager.Domain;
using YMMProjectManager.Infrastructure;

//CSJ,class=ProjectListSaveTests,プロジェクト一覧保存テスト
internal static class ProjectListSaveTests
{
    //CSJ,method=RunAsync,実行する（非同期）
    public static async Task RunAsync(string workRoot)
    {
        //CSJ,variable=root,ルート
        var root = Path.Combine(workRoot, "project-list-save");
        Directory.CreateDirectory(root);
        //CSJ,variable=path,パス
        var path = Path.Combine(root, "projects.json");
        //CSJ,variable=logger,ロガー
        var logger = new FileLogger(Path.Combine(root, "log.txt"));
        JsonProjectRepository Repo(Func<Stream, byte[], CancellationToken, Task>? writer = null, Action? beforeReplace = null)
            => new(logger, path, writer, beforeReplace);
        ProjectStore Store(string name) => new() { Projects = [new ProjectEntry { FullPath = Path.Combine(root, name + ".ymmp"), DisplayName = name, Pinned = true }] };
        //CSJ,method=Save,保存する
        async Task Save(JsonProjectRepository repo, ProjectStore store, CancellationToken token = default)
        {
            //CSJ,variable=baseline,基準
            var baseline = await repo.LoadAsync(); store.Revision = baseline.Revision; store.IsRecoverySafe = baseline.IsRecoverySafe;
            await repo.SaveAsync(store, token);
        }
        //CSJ,method=Check,確認する
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
            //CSJ,variable=document,文書
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
        //CSJ,variable=bytes,バイト列
        var bytes = await File.ReadAllBytesAsync(path); var hash = Hash(); var size = bytes.LongLength;
        // T03: pre-cancellation.
        using (var cts = new CancellationTokenSource()) { cts.Cancel(); await Expect<OperationCanceledException>(() => Save(Repo(), Store("lost"), cts.Token)); }
        await CheckPreserved(hash, size, bytes);
        // T04: deterministically cancel after writing part of the temporary file.
        using (var cts = new CancellationTokenSource())
        {
            //CSJ,variable=repo,リポジトリ
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
        var lockedStore = Store("lost"); var lockedBaseline = await Repo().LoadAsync(); lockedStore.Revision = lockedBaseline.Revision; lockedStore.IsRecoverySafe = lockedBaseline.IsRecoverySafe;
        using (var locked = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
            await Expect<IOException>(() => Repo().SaveAsync(lockedStore));
        await CheckPreserved(hash, size, bytes);
        using (var locked = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None))
            await Expect<IOException>(() => Repo().LoadAsync());
        await CheckPreserved(hash, size, bytes);
        // T12: separate repository instances serialize writes; final document is a complete submitted snapshot.
        //CSJ,variable=concurrentBaseline,並行基準
        var concurrentBaseline = await Repo().LoadAsync();
        async Task<bool> Compete(int i) { var store = Store("concurrent" + i); store.Revision = concurrentBaseline.Revision; store.IsRecoverySafe = concurrentBaseline.IsRecoverySafe; try { await Repo().SaveAsync(store); return true; } catch (ProjectListConflictException) { return false; } }
        var winners = await Task.WhenAll(Enumerable.Range(0, 12).Select(Compete));
        Check(winners.Count(x => x) == 1, "stale concurrent writes were accepted");
        //CSJ,variable=concurrent,並行
        var concurrent = await Repo().LoadAsync(); Check(concurrent.Projects.Count == 1 && concurrent.Projects[0].DisplayName!.StartsWith("concurrent", StringComparison.Ordinal), "concurrent save"); NoTemps();
        // T13: old-form fields remain compatible after a round trip.
        await File.WriteAllTextAsync(path, "{\"Projects\":[{\"FullPath\":\"C:\\\\Temp\\\\legacy.ymmp\",\"DisplayName\":\"legacy\",\"Pinned\":true,\"LastAccess\":\"2026-06-08T00:00:00+09:00\"}]}");
        //CSJ,variable=legacy,旧形式
        var legacy = await Repo().LoadAsync();
        Check(legacy.IsRecoverySafe, "legacy empty linked list should remain safely writable");
        await Repo().SaveAsync(legacy); var roundtrip = await Repo().LoadAsync();
        Check(roundtrip.Projects.Single().DisplayName == "legacy" && roundtrip.Projects[0].Pinned && roundtrip.Projects[0].LastAccess == legacy.Projects[0].LastAccess && roundtrip.Projects[0].LinkedYmmpFiles.Count > 0, "legacy roundtrip");
        Check(roundtrip.Projects[0].ProjectId is null, "legacy identity must remain unresolved");
        var id1 = Guid.NewGuid(); var id2 = Guid.NewGuid();
        var identified = new ProjectStore { Projects = [new ProjectEntry { FullPath = Path.Combine(root, "one", "same.ymmp"), ProjectId = id1 }, new ProjectEntry { FullPath = Path.Combine(root, "two", "same.ymmp"), ProjectId = id2 }] };
        await Save(Repo(), identified);
        var reloaded = await Repo().LoadAsync();
        Check(reloaded.Projects.Select(p => p.ProjectId).SequenceEqual(new Guid?[] { id1, id2 }), "identity roundtrip");
        //CSJ,variable=identityHash,識別情報ハッシュ
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
        identified.Projects[1].ProjectId = null;
        await Expect<InvalidDataException>(() => Repo().SaveAsync(identified));
        Check(Hash() == identityHash, "legacy project without path modified list");
        var aliases = await Repo().LoadAsync();
        //CSJ,variable=fileIndex,ファイルインデックス
        var fileIndex = Guid.NewGuid();
        aliases.Projects[0].FileIdentity = new ProjectFileIdentity(7, fileIndex, new string('1', 64));
        aliases.Projects[1].FileIdentity = new ProjectFileIdentity(7, fileIndex, new string('2', 64));
        await Expect<InvalidDataException>(() => Repo().SaveAsync(aliases));
        Check(Hash() == identityHash, "physical alias save changed original list");
        await File.WriteAllTextAsync(path, $"{{\"Projects\":[{{\"FullPath\":\"one.ymmp\",\"ProjectId\":\"{id1}\"}},{{\"FullPath\":\"two.ymmp\",\"ProjectId\":\"{id1}\"}}]}}");
        //CSJ,variable=duplicateHash,重複ハッシュ
        var duplicateHash = Hash(); await Expect<InvalidDataException>(() => Repo().LoadAsync()); Check(Hash() == duplicateHash, "duplicate load modified list");
        var bomBytes = new byte[] { 0xEF, 0xBB, 0xBF }.Concat(System.Text.Encoding.UTF8.GetBytes("{\"Projects\":[],\"Folders\":[]}")).ToArray();
        await File.WriteAllBytesAsync(path, bomBytes); var bomHash = Hash(); var bomStore = await Repo().LoadAsync();
        Check(bomStore.Projects.Count == 0 && bomStore.IsRecoverySafe && Hash() == bomHash && bomStore.Revision!.Sha256 == bomHash, "BOM list load changed content or revision");
        await File.WriteAllTextAsync(path, "{\"Projects\":[{\"FullPath\":\"p.ymmp\",\"LinkedYmmpFiles\":[{\"FilePath\":\"p.ymmp\"}]}]}");
        //CSJ,variable=legacyLink,旧形式リンク
        var legacyLink = await Repo().LoadAsync(); Check(legacyLink.IsRecoverySafe && legacyLink.Projects.Single().LinkedYmmpFiles.Count == 1, "legacy minimal link rejected");
        await Repo().SaveAsync(legacyLink); Check((await Repo().LoadAsync()).Projects.Single().LinkedYmmpFiles.Single().FilePath == "p.ymmp", "legacy minimal link lost");
        //CSJ,variable=defaultLinkPath,既定リンクパス
        var defaultLinkPath = Path.Combine(root, "legacy-default-link.ymmp");
        //CSJ,variable=folderId,フォルダーID
        var folderId = Guid.NewGuid();
        var legacyDefaults = JsonSerializer.Serialize(new
        {
            Projects = new[] { new { FullPath = defaultLinkPath, FolderId = folderId } },
            Folders = new[] { new { Id = folderId, Name = "legacy-folder" } },
        });
        await File.WriteAllTextAsync(path, legacyDefaults);
        //CSJ,variable=firstLegacy,最初旧形式
        var firstLegacy = await Repo().LoadAsync();
        await File.WriteAllTextAsync(defaultLinkPath, "{}");
        //CSJ,variable=secondLegacy,2番目旧形式
        var secondLegacy = await Repo().LoadAsync();
        Check(ProjectListRecovery.Fingerprint(firstLegacy) == ProjectListRecovery.Fingerprint(secondLegacy), "legacy defaults changed across reads or live file appearance");
        var inferredLink = secondLegacy.Projects.Single().LinkedYmmpFiles.Single();
        Check(inferredLink.Id != Guid.Empty && inferredLink.LastCheckedAt is null && inferredLink.RegisteredAt == DateTimeOffset.UnixEpoch &&
            !inferredLink.Exists && secondLegacy.Folders.Single().CreatedAt == DateTimeOffset.UnixEpoch, "legacy fallback invented runtime evidence");
        firstLegacy.Projects.Single().Pinned = true;
        await Repo().SaveAsync(firstLegacy);
        Check((await Repo().LoadAsync()).Projects.Single().LinkedYmmpFiles.Single().Id == inferredLink.Id, "legacy child identity changed on save");
        var rootExtraPath = Path.Combine(root, "future-root.json");
        var rootExtraRepo = new JsonProjectRepository(logger, rootExtraPath);
        //CSJ,variable=stableId,安定ID
        var stableId = Guid.NewGuid();
        //CSJ,variable=rootFixture,ルートテスト用データ
        var rootFixture = System.Text.Json.Nodes.JsonNode.Parse(JsonSerializer.Serialize(new
        {
            Projects = new[] { new { FullPath = defaultLinkPath, ProjectId = stableId } },
            Folders = Array.Empty<object>(),
            ForwardData = new { Value = "base", Numbers = new[] { 1, 2 }, Empty = (string?)null },
        }))!;
        await File.WriteAllTextAsync(rootExtraPath, rootFixture.ToJsonString());
        //CSJ,variable=rootBaseline,ルート基準
        var rootBaseline = await rootExtraRepo.LoadAsync();
        Check(rootBaseline.IsRecoverySafe, "ordinary root extension was rejected");
        //CSJ,variable=rootLocal,ルートローカル
        var rootLocal = ProjectListMerge.Snapshot(rootBaseline);
        rootLocal.Projects.Single().Pinned = true;
        //CSJ,variable=drafts,下書き一覧
        var drafts = new ProjectListDrafts(rootExtraRepo.RecoveryDirectory);
        //CSJ,variable=rootDraft,ルート下書き
        var rootDraft = await drafts.ReadAsync(await drafts.WriteAsync(rootBaseline, rootLocal, []));
        Check(rootDraft.Local.Revision?.PreservedRootFieldsJson == rootLocal.Revision?.PreservedRootFieldsJson,
            "draft roundtrip lost preserved root fields");
        rootFixture["ForwardData"]!["Value"] = "remote";
        await File.WriteAllTextAsync(rootExtraPath, rootFixture.ToJsonString());
        await Expect<ProjectListConflictException>(() => rootExtraRepo.SaveAsync(rootDraft.Local));
        var mergedRoot = ProjectListMerge.Merge(rootDraft.Baseline, rootDraft.Local, await rootExtraRepo.LoadAsync());
        Check(mergedRoot.Store is not null, "remote extension-only change conflicted with an independent typed edit");
        await rootExtraRepo.SaveAsync(mergedRoot.Store!);
        var mergedDocument = System.Text.Json.Nodes.JsonNode.Parse(await File.ReadAllTextAsync(rootExtraPath))!;
        Check(mergedDocument["Projects"]![0]!["Pinned"]!.GetValue<bool>() &&
            System.Text.Json.Nodes.JsonNode.DeepEquals(rootFixture["ForwardData"], mergedDocument["ForwardData"]),
            "local typed change or newest unknown root data was lost");
        //CSJ,variable=nestedPath,入れ子パス
        var nestedPath = Path.Combine(root, "future-nested.json");
        //CSJ,variable=nestedRepo,入れ子リポジトリ
        var nestedRepo = new JsonProjectRepository(logger, nestedPath);
        //CSJ,variable=nestedLinkId,入れ子リンクID
        var nestedLinkId = Guid.NewGuid();
        //CSJ,variable=nestedFixture,入れ子テスト用データ
        var nestedFixture = System.Text.Json.Nodes.JsonNode.Parse(JsonSerializer.Serialize(new
        {
            Projects = new[]
            {
                new
                {
                    FullPath = defaultLinkPath, ProjectId = stableId, FolderId = folderId, FutureProject = new { Color = "base" },
                    LinkedYmmpFiles = new[] { new { Id = nestedLinkId, FilePath = defaultLinkPath, FutureLink = new[] { 1, 2 } } },
                },
            },
            Folders = new[] { new { Id = folderId, Name = "family", FutureFolder = (string?)null } },
        }))!;
        await File.WriteAllTextAsync(nestedPath, nestedFixture.ToJsonString());
        //CSJ,variable=nestedBaseline,入れ子基準
        var nestedBaseline = await nestedRepo.LoadAsync();
        Check(nestedBaseline.IsRecoverySafe, "stable nested extensions were rejected");
        //CSJ,variable=nestedLocal,入れ子ローカル
        var nestedLocal = ProjectListMerge.Snapshot(nestedBaseline);
        nestedLocal.Projects.Single().LinkedYmmpFiles.Single().Memo = "local";
        //CSJ,variable=nestedDrafts,入れ子下書き一覧
        var nestedDrafts = new ProjectListDrafts(nestedRepo.RecoveryDirectory);
        //CSJ,variable=nestedDraft,入れ子下書き
        var nestedDraft = await nestedDrafts.ReadAsync(await nestedDrafts.WriteAsync(nestedBaseline, nestedLocal, []));
        Check(nestedDraft.Local.Revision?.PreservedNestedFieldsJson == nestedLocal.Revision?.PreservedNestedFieldsJson, "nested fields did not survive a draft");
        nestedFixture["Projects"]![0]!["FutureProject"]!["Color"] = "remote";
        await File.WriteAllTextAsync(nestedPath, nestedFixture.ToJsonString());
        //CSJ,variable=nestedPlan,入れ子計画
        var nestedPlan = ProjectListMerge.Merge(nestedDraft.Baseline, nestedDraft.Local, await nestedRepo.LoadAsync());
        Check(nestedPlan.Store is not null, "nested extension-only change conflicted with a typed link edit");
        await nestedRepo.SaveAsync(nestedPlan.Store!);
        //CSJ,variable=nestedBytes,入れ子バイト列
        var nestedBytes = await File.ReadAllBytesAsync(nestedPath);
        //CSJ,variable=nestedResult,入れ子結果
        var nestedResult = System.Text.Json.Nodes.JsonNode.Parse(nestedBytes)!;
        Check(nestedResult["Projects"]![0]!["FutureProject"]!["Color"]!.GetValue<string>() == "remote" &&
            nestedResult["Projects"]![0]!["LinkedYmmpFiles"]![0]!["Memo"]!.GetValue<string>() == "local" &&
            nestedResult["Projects"]![0]!["LinkedYmmpFiles"]![0]!["FutureLink"]!.AsArray().Count == 2 &&
            nestedResult["Folders"]![0]!.AsObject().ContainsKey("FutureFolder"), "typed save dropped a preserved nested value");
        var orphanedLink = await nestedRepo.LoadAsync();
        orphanedLink.Projects.Single().LinkedYmmpFiles.Clear();
        await Expect<InvalidDataException>(() => nestedRepo.SaveAsync(orphanedLink));
        var orphanedProject = await nestedRepo.LoadAsync();
        orphanedProject.Projects.Single().ProjectId = Guid.NewGuid();
        await Expect<InvalidDataException>(() => nestedRepo.SaveAsync(orphanedProject));
        var orphanedFolder = await nestedRepo.LoadAsync();
        orphanedFolder.Folders.Clear();
        await Expect<InvalidDataException>(() => nestedRepo.SaveAsync(orphanedFolder));
        var afterOrphanAttempts = await File.ReadAllBytesAsync(nestedPath);
        Check(nestedBytes.SequenceEqual(afterOrphanAttempts), "refused orphan changes mutated project list bytes");
        var resurrectBase = await nestedRepo.LoadAsync();
        //CSJ,variable=kind,種別
        foreach (var kind in new[] { "project", "link", "folder" })
        {
            var edited = ProjectListMerge.Snapshot(resurrectBase);
            //CSJ,variable=latestJson,最新JSON
            var latestJson = nestedResult.DeepClone();
            switch (kind)
            {
                case "project":
                    edited.Projects.Single().DisplayName = "local edit";
                    latestJson["Projects"]!.AsArray().Clear();
                    break;
                case "link":
                    edited.Projects.Single().LinkedYmmpFiles.Single().Memo = "local edit";
                    latestJson["Projects"]![0]!["LinkedYmmpFiles"]!.AsArray().Clear();
                    break;
                case "folder":
                    edited.Folders.Single().Name = "local edit";
                    latestJson["Folders"]!.AsArray().Clear();
                    latestJson["Projects"]![0]!.AsObject().Remove("FolderId");
                    break;
            }
            var latestWithoutAnchor = JsonProjectRepository.DeserializeStore(JsonSerializer.SerializeToUtf8Bytes(latestJson));
            var contested = ProjectListMerge.Merge(resurrectBase, edited, latestWithoutAnchor);
            Check(contested.CanResolveAll && contested.Choices.Count == 1, $"deleted {kind} was not represented as an explicit conflict");
            //CSJ,variable=option,オプション
            var option = contested.Choices.Single();
            var resurrect = ProjectListMerge.Merge(resurrectBase, edited, latestWithoutAnchor,
                new Dictionary<ProjectListConflict, bool> { [option] = true });
            var keepNewest = ProjectListMerge.Merge(resurrectBase, edited, latestWithoutAnchor,
                new Dictionary<ProjectListConflict, bool> { [option] = false });
            Check(resurrect.Store is null && keepNewest.Store is not null, $"resurrecting {kind} discarded unknown data or blocked the remote choice");
        }
        await Program.RunNamedCaseAsync("Opaque managed-list fields stay with the exact association and file identity", async () =>
        {
            //CSJ,variable=managedPath,管理対象パス
            var managedPath = Path.Combine(root, "forward-managed-records.json");
            //CSJ,variable=managedRepo,管理対象リポジトリ
            var managedRepo = new JsonProjectRepository(logger, managedPath);
            var sha = new string('A', 64);
            //CSJ,variable=metadata,メタデータ
            var metadata = new ProjectMetadataAssociation(Path.Combine(root, "metadata"), Guid.NewGuid(), sha,
                Guid.NewGuid(), sha, Guid.NewGuid(), sha);
            //CSJ,variable=identity,識別情報
            var identity = new ProjectFileIdentity(1, Guid.NewGuid(), sha);
            //CSJ,variable=fixture,テスト用データ
            var fixture = System.Text.Json.Nodes.JsonNode.Parse(JsonSerializer.Serialize(new
            {
                Projects = new[] { new { FullPath = defaultLinkPath, ProjectId = stableId, MetadataAssociation = metadata, FileIdentity = identity } },
                Folders = Array.Empty<object>(),
            }))!;
            //CSJ,variable=record,記録
            var record = fixture["Projects"]![0]!;
            record["MetadataAssociation"]!["FutureMetadata"] = "older";
            record["FileIdentity"]!["FutureIdentity"] = System.Text.Json.Nodes.JsonNode.Parse("[null, {\"evidence\":42}]");
            await File.WriteAllTextAsync(managedPath, fixture.ToJsonString());
            //CSJ,variable=managedBaseline,管理対象基準
            var managedBaseline = await managedRepo.LoadAsync();
            //CSJ,variable=managedLocal,管理対象ローカル
            var managedLocal = ProjectListMerge.Snapshot(managedBaseline);
            managedLocal.Projects.Single().Pinned = true;
            Check(managedBaseline.IsRecoverySafe && managedBaseline.Projects.Single().MetadataAssociation == metadata &&
                managedBaseline.Projects.Single().FileIdentity == identity, "compatible managed record could not be loaded");
            var ready = await managedRepo.WritePendingDraftAsync(managedBaseline, managedLocal, [], null);
            var decoded = await new ProjectListDrafts(managedRepo.RecoveryDirectory).ReadAsync(ready);

            record["MetadataAssociation"]!["FutureMetadata"] = "external change";
            await File.WriteAllTextAsync(managedPath, fixture.ToJsonString());
            await Expect<ProjectListConflictException>(() => managedRepo.SaveAsync(decoded.Local));
            var merged = ProjectListMerge.Merge(decoded.Baseline, decoded.Local, await managedRepo.LoadAsync());
            Check(merged.Store is not null, "independent pin change conflicted with a forward-compatible field");
            await managedRepo.SaveReadyDraftAsync(merged.Store!, ready);
            var mergedBytes = await File.ReadAllBytesAsync(managedPath);
            var written = System.Text.Json.Nodes.JsonNode.Parse(mergedBytes)!["Projects"]![0]!;
            Check(written["Pinned"]!.GetValue<bool>() &&
                written["MetadataAssociation"]!["FutureMetadata"]!.GetValue<string>() == "external change" &&
                System.Text.Json.Nodes.JsonNode.DeepEquals(written["FileIdentity"]!["FutureIdentity"], record["FileIdentity"]!["FutureIdentity"]),
                "managed record lost its newest opaque fields or its ordinary edit");

            var current = await managedRepo.LoadAsync();
            var switched = ProjectListMerge.Snapshot(current);
            switched.Projects.Single().MetadataAssociation = metadata with { SelectedRevisionId = Guid.NewGuid() };
            Check(ProjectListMerge.Merge(current, switched, current).Store is null,
                "a changed association was allowed to inherit opaque data");
            await Expect<InvalidDataException>(() => managedRepo.SaveAsync(switched));
            switched = ProjectListMerge.Snapshot(current);
            switched.Projects.Single().FileIdentity = identity with { Sha256 = new string('B', 64) };
            await Expect<InvalidDataException>(() => managedRepo.SaveAsync(switched));
            //CSJ,variable=unchanged,変更なし
            var unchanged = await File.ReadAllBytesAsync(managedPath);
            Check(mergedBytes.SequenceEqual(unchanged), "refused managed-record edits changed the list");

            record["MetadataAssociation"]!["SchemaVersion"] = 99;
            await File.WriteAllTextAsync(managedPath, fixture.ToJsonString());
            var unknownSchema = await managedRepo.LoadAsync();
            Check(!unknownSchema.IsRecoverySafe, "an unrecognized managed schema version was accepted");
            await Expect<InvalidDataException>(() => managedRepo.SaveAsync(unknownSchema));
            var quarantine = Path.Combine(root, "forward-managed-original.json");
            await managedRepo.ExportOriginalAsync(quarantine);
            var exported = await File.ReadAllBytesAsync(quarantine);
            var original = await File.ReadAllBytesAsync(managedPath);
            Check(exported.SequenceEqual(original), "unknown managed schema was not exported byte-for-byte");
        });
        //CSJ,variable=copyPath,コピーパス
        var copyPath = Path.Combine(root, "recovery-copy.json"); var copyRepo = new JsonProjectRepository(logger, copyPath);
        await File.WriteAllTextAsync(copyPath, "existing synthetic data");
        await Expect<IOException>(() => copyRepo.SaveNewCopyAsync(legacyLink));
        Check(await File.ReadAllTextAsync(copyPath) == "existing synthetic data", "recovery copy overwrote existing file");
        //CSJ,variable=newCopyPath,新規コピーパス
        var newCopyPath = Path.Combine(root, "recovery-new.json"); var newCopy = new JsonProjectRepository(logger, newCopyPath);
        await newCopy.SaveNewCopyAsync(legacyLink); Check((await newCopy.LoadAsync()).Projects.Count == 1, "recovery copy did not reload");
        // Corrupt read must not silently become a new empty store.
        await File.WriteAllTextAsync(path, "{invalid"); var corruptHash=Hash();
        await Expect<JsonException>(() => Repo().LoadAsync()); Check(Hash()==corruptHash, "corrupt input changed");
        //CSJ,variable=rawCopy,生データコピー
        var rawCopy = Path.Combine(root, "original-raw.json");
        await Repo().ExportOriginalAsync(rawCopy);
        //CSJ,variable=rawExport,生データ書き出し
        var rawExport = await File.ReadAllBytesAsync(rawCopy);
        //CSJ,variable=originalRaw,元の生データ
        var originalRaw = await File.ReadAllBytesAsync(path);
        Check(rawExport.SequenceEqual(originalRaw), "raw recovery export altered original bytes");
        await Expect<IOException>(() => Repo().ExportOriginalAsync(rawCopy));
        Check(Hash() == corruptHash, "raw recovery export changed original file");
        Console.WriteLine("Project list save regression checks passed.");
    }
}
