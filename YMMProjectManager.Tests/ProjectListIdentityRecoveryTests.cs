using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using YMMProjectManager.Application;
using YMMProjectManager.Domain;
using YMMProjectManager.Infrastructure;

//CSJ,class=ProjectListIdentityRecoveryTests,プロジェクト一覧識別情報復旧テスト
internal static class ProjectListIdentityRecoveryTests
{
    //CSJ,method=RunAsync,実行する（非同期）
    internal static async Task RunAsync(string workRoot)
    {
        //CSJ,variable=root,ルート
        var root = Path.Combine(workRoot, "duplicate-identity");
        Directory.CreateDirectory(root);
        //CSJ,variable=duplicate,重複
        var duplicate = Guid.NewGuid();
        //CSJ,variable=existing,既存
        var existing = Guid.NewGuid();
        var input = JsonSerializer.SerializeToUtf8Bytes(new
        {
            Projects = new object[]
            {
                new { FullPath = Path.Combine(root, "a.ymmp"), ProjectId = duplicate, MetadataAssociation = new { Detail = "keep" }, Unknown = new[] { 1, 2 } },
                new { FullPath = Path.Combine(root, "b.ymmp"), ProjectId = duplicate, MetadataAssociation = new { Detail = "original remains" }, Unknown = new[] { 3 } },
                new { FullPath = Path.Combine(root, "other.ymmp"), ProjectId = existing },
                new { FullPath = Path.Combine(root, "legacy.ymmp") },
            },
            Folders = Array.Empty<object>(),
            FutureRoot = new { info = "preserved" },
        });
        var source = Path.Combine(root, "projects.json");
        await File.WriteAllBytesAsync(source, input);
        //CSJ,variable=review,レビュー
        var review = ProjectListIdentityRecovery.Inspect(input);
        //CSJ,variable=conflict,競合
        var conflict = review.Conflicts.Single();
        if (conflict.Id != duplicate || conflict.Entries.Count != 2 || conflict.Entries.Any(entry => !entry.HasMetadata))
            throw new Exception("Duplicate review missed an association or candidate.");
        var keepers = new Dictionary<Guid, int> { [duplicate] = conflict.Entries[0].Row };
        var repaired = ProjectListIdentityRecovery.Repair(input, review, keepers);
        //CSJ,variable=document,文書
        using var document = JsonDocument.Parse(repaired);
        //CSJ,variable=payload,本体データ
        var payload = document.RootElement;
        //CSJ,variable=rows,行一覧
        var rows = payload.GetProperty("Projects");
        //CSJ,variable=newId,新規ID
        var newId = rows[1].GetProperty("ProjectId").GetGuid();
        if (rows[0].GetProperty("ProjectId").GetGuid() != duplicate || rows[0].GetProperty("MetadataAssociation").GetProperty("Detail").GetString() != "keep" ||
            newId == duplicate || newId == existing || rows[1].TryGetProperty("MetadataAssociation", out _) ||
            rows[1].GetProperty("Unknown")[0].GetInt32() != 3 || rows[2].GetProperty("ProjectId").GetGuid() != existing ||
            rows[3].TryGetProperty("ProjectId", out _) || payload.GetProperty("FutureRoot").GetProperty("info").GetString() != "preserved")
            throw new Exception("Repair changed a keeper, unrelated identity, or unknown data.");

        //CSJ,variable=destination,格納先
        var destination = Path.Combine(root, "recovered.json");
        await JsonProjectRepository.WriteNewCopyAsync(destination, repaired, default);
        try { await JsonProjectRepository.WriteNewCopyAsync(source, repaired, default); throw new Exception("Repair overwrote its source."); }
        catch (IOException) { }
        var originalOnDisk = await File.ReadAllBytesAsync(source);
        var recoveredOnDisk = await File.ReadAllBytesAsync(destination);
        if (!input.SequenceEqual(originalOnDisk) || ProjectListIdentityRecovery.Inspect(recoveredOnDisk).Conflicts.Count != 0)
            throw new Exception("Repair copy did not preserve the original or eliminate the conflict.");

        ExpectInvalid(() => ProjectListIdentityRecovery.Repair([.. input, (byte)' '], review, keepers));
        ExpectInvalid(() => ProjectListIdentityRecovery.Repair(input, review, new Dictionary<Guid, int> { [duplicate] = 2 }));
        ExpectInvalid(() => ProjectListIdentityRecovery.Inspect(Encoding.UTF8.GetBytes("""{"Projects":[],"projects":[]}""")));
        await RunApplyAsync(root, duplicate);
        await Program.RunNamedCaseAsync("Explicitly assign legacy project and link IDs", () => RunLegacyAsync(root));
        await Program.RunNamedCaseAsync("Resolve verified hard-link registrations without moving projects or metadata", () => RunPhysicalAsync(root));
    }

    //CSJ,method=RunPhysicalAsync,ハードリンク登録の解決を検証する（非同期）
    private static async Task RunPhysicalAsync(string parent)
    {
        //CSJ,variable=root,ルート
        var root = Path.Combine(parent, "physical");
        Directory.CreateDirectory(root);
        var source = Path.Combine(root, "source.ymmp");
        var alias = Path.Combine(root, "alias.ymmp");
        var copied = Path.Combine(root, "copy.ymmp");
        //CSJ,variable=projectBytes,プロジェクトバイト列
        var projectBytes = "synthetic project"u8.ToArray();
        await File.WriteAllBytesAsync(source, projectBytes);
        File.Copy(source, copied);
        if (!CreateHardLink(alias, source, IntPtr.Zero)) throw new Win32Exception(Marshal.GetLastWin32Error());

        //CSJ,variable=identity,識別情報
        var identity = await ProjectFileEvidence.ReadAsync(source);
        //CSJ,variable=firstId,最初ID
        var firstId = Guid.NewGuid();
        var keeperId = Guid.NewGuid();
        //CSJ,variable=copyId,コピーID
        var copyId = Guid.NewGuid();
        //CSJ,variable=firstMetadata,最初メタデータ
        var firstMetadata = CreateAssociation("first-meta");
        var keeperMetadata = CreateAssociation("keeper-meta");
        var keeperLink = Guid.NewGuid();
        //CSJ,method=CreateAssociation,関連付けを作成する
        ProjectMetadataAssociation CreateAssociation(string name)
        {
            //CSJ,variable=path,パス
            var path = Path.Combine(root, name);
            Directory.CreateDirectory(path);
            File.WriteAllBytes(Path.Combine(path, "fixture.bin"), projectBytes);
            //CSJ,variable=revision,版
            var revision = Guid.NewGuid();
            return new ProjectMetadataAssociation(path, revision, identity.Sha256, revision, identity.Sha256,
                revision, identity.Sha256);
        }

        var original = JsonSerializer.SerializeToUtf8Bytes(new
        {
            Projects = new object[]
            {
                new { FullPath = source, ProjectId = firstId, FileIdentity = identity, MetadataAssociation = firstMetadata, FutureRow = "original" },
                new { FullPath = alias, ProjectId = keeperId, MetadataAssociation = keeperMetadata, FutureRow = "keeper",
                    LinkedYmmpFiles = new[] { new { Id = keeperLink, FilePath = alias, FutureLink = 5 } } },
                new { FullPath = copied, ProjectId = copyId, FutureRow = "independent" },
            },
            Folders = Array.Empty<object>(),
            FutureRoot = 3,
        });
        //CSJ,variable=active,有効
        var active = Path.Combine(root, "projects.json");
        //CSJ,variable=logger,ロガー
        var logger = new FileLogger(Path.Combine(root, "repair.log"));
        //CSJ,variable=repository,リポジトリ
        var repository = new JsonProjectRepository(logger, active);
        await File.WriteAllBytesAsync(active, original);
        //CSJ,variable=review,レビュー
        var review = await ProjectListIdentityRecovery.InspectPhysicalAsync(original);
        //CSJ,variable=group,グループ
        var group = review.Conflicts.Single();
        if (review.UnverifiedRecordedDuplicates.Count != 0 || group.Volume != identity.Volume || group.FileId != identity.FileId ||
            !group.Entries.Select(row => row.ProjectId).SequenceEqual(new Guid?[] { firstId, keeperId }) ||
            group.Entries[1] is not { HasMetadata: true, LinkCount: 1 })
            throw new Exception("Physical review missed the hard-linked rows or confused a content-identical copy with an alias.");

        var keepers = new Dictionary<(ulong, Guid), int> { [(group.Volume, group.FileId)] = group.Entries[1].Row };
        //CSJ,variable=export,書き出し
        var export = Path.Combine(root, "recovered.json");
        await ProjectListIdentityRecovery.ExportPhysicalRepairAsync(repository, review, keepers, export, default);
        var exported = JsonProjectRepository.DeserializeStore(await File.ReadAllBytesAsync(export));
        if (!exported.Projects.Select(row => row.ProjectId).SequenceEqual(new Guid?[] { keeperId, copyId }))
            throw new Exception("Export did not keep the explicitly selected identity.");

        //CSJ,variable=backup,バックアップ
        var backup = Path.Combine(root, "original.json");
        await ProjectListIdentityRecovery.ApplyPhysicalAsync(repository, review, keepers, backup, default);
        //CSJ,variable=loaded,読み込み済み
        var loaded = await repository.LoadAsync();
        if (!loaded.IsRecoverySafe || !loaded.Projects.Select(row => row.ProjectId).SequenceEqual(new Guid?[] { keeperId, copyId }) ||
            loaded.Projects[0].MetadataAssociation != keeperMetadata || loaded.Projects[0].LinkedYmmpFiles.Single().Id != keeperLink)
            throw new Exception("Repair merged identities or changed the kept association and link.");
        //CSJ,variable=afterRepair,変更後修復
        using var afterRepair = JsonDocument.Parse(await File.ReadAllBytesAsync(active));
        //CSJ,variable=payload,本体データ
        var payload = afterRepair.RootElement;
        var archived = await File.ReadAllBytesAsync(backup);
        if (payload.GetProperty("FutureRoot").GetInt32() != 3 ||
            payload.GetProperty("Projects")[0].GetProperty("FutureRow").GetString() != "keeper" ||
            payload.GetProperty("Projects")[0].GetProperty("LinkedYmmpFiles")[0].GetProperty("FutureLink").GetInt32() != 5 ||
            payload.GetProperty("Projects")[1].GetProperty("FutureRow").GetString() != "independent" ||
            !original.SequenceEqual(archived))
            throw new Exception("Repair lost opaque data or the original archive.");
        //CSJ,variable=path,パス
        foreach (var path in new[] { source, alias, copied, Path.Combine(firstMetadata.Directory, "fixture.bin"), Path.Combine(keeperMetadata.Directory, "fixture.bin") })
        {
            //CSJ,variable=bytes,バイト列
            var bytes = await File.ReadAllBytesAsync(path);
            if (!projectBytes.SequenceEqual(bytes))
                throw new Exception("Repair modified a project or its metadata.");
        }

        // Replacing an alias with a copy preserves the SHA-256 but invalidates the approved physical identity.
        File.Delete(alias);
        File.Copy(source, alias);
        await File.WriteAllBytesAsync(active, original);
        var staleBackup = Path.Combine(root, "stale-original.json");
        try
        {
            await ProjectListIdentityRecovery.ApplyPhysicalAsync(repository, review, keepers, staleBackup, default);
            throw new Exception("Repair accepted a replaced project alias.");
        }
        catch (InvalidDataException) { }
        //CSJ,variable=unchanged,変更なし
        var unchanged = await File.ReadAllBytesAsync(active);
        if (File.Exists(staleBackup) || !original.SequenceEqual(unchanged))
            throw new Exception("Stale review wrote a backup or changed the active list.");

        var recordedOnly = JsonSerializer.SerializeToUtf8Bytes(new
        {
            Projects = new[]
            {
                new { FullPath = source, ProjectId = firstId, FileIdentity = identity },
                new { FullPath = alias, ProjectId = keeperId, FileIdentity = identity },
            },
            Folders = Array.Empty<object>(),
        });
        //CSJ,variable=unresolved,未解決
        var unresolved = await ProjectListIdentityRecovery.InspectPhysicalAsync(recordedOnly);
        if (unresolved.Conflicts.Count != 0 || unresolved.UnverifiedRecordedDuplicates.Single().Entries.Count != 2)
            throw new Exception("Stored duplicate evidence was treated as a live physical duplicate.");

        //CSJ,variable=samePath,同一パス
        var samePath = JsonSerializer.SerializeToUtf8Bytes(new
        {
            Projects = new[]
            {
                new { FullPath = source, ProjectId = firstId, FutureRow = "first" },
                new { FullPath = Path.Combine(root, ".", "source.ymmp"), ProjectId = keeperId, FutureRow = "keep this row" },
            },
            Folders = Array.Empty<object>(),
        });
        ExpectInvalid(() => ProjectListIdentityRecovery.Inspect(samePath));
        await File.WriteAllBytesAsync(active, samePath);
        //CSJ,variable=samePathReview,同一パスレビュー
        var samePathReview = await ProjectListIdentityRecovery.InspectPhysicalAsync(samePath);
        var repeatedRows = samePathReview.Conflicts.Single();
        if (repeatedRows.Entries.Count != 2) throw new Exception("Repeated normalized path was not reviewed as two registrations.");
        //CSJ,variable=samePathBackup,同一パスバックアップ
        var samePathBackup = Path.Combine(root, "same-path-original.json");
        await ProjectListIdentityRecovery.ApplyPhysicalAsync(repository, samePathReview,
            new Dictionary<(ulong, Guid), int> { [(repeatedRows.Volume, repeatedRows.FileId)] = 1 }, samePathBackup, default);
        //CSJ,variable=samePathResult,同一パス結果
        var samePathResult = await repository.LoadAsync();
        //CSJ,variable=samePathOriginal,同一パス元の
        var samePathOriginal = await File.ReadAllBytesAsync(samePathBackup);
        if (samePathResult.Projects.Single().ProjectId != keeperId || !samePath.SequenceEqual(samePathOriginal))
            throw new Exception("Repeated path repair did not use the selected registration or archive the exact original.");
    }

    [DllImport("kernel32.dll", EntryPoint = "CreateHardLinkW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateHardLink(string newFile, string existingFile, IntPtr reserved);

    //CSJ,method=RunLegacyAsync,旧形式のID割り当てを検証する（非同期）
    private static async Task RunLegacyAsync(string root)
    {
        var original = JsonSerializer.SerializeToUtf8Bytes(new
        {
            Projects = new object[]
            {
                new { FullPath = Path.Combine(root, "legacy-one.ymmp"), FutureProject = 4,
                    LinkedYmmpFiles = new[] { new { FilePath = Path.Combine(root, "legacy-one.ymmp"), FutureLink = 7 } } },
                new { FullPath = Path.Combine(root, "legacy-preserved.ymmp") },
            },
            Folders = Array.Empty<object>(),
        });
        //CSJ,variable=path,パス
        var path = Path.Combine(root, "legacy-active.json");
        //CSJ,variable=backup,バックアップ
        var backup = Path.Combine(root, "legacy-original.json");
        //CSJ,variable=repository,リポジトリ
        var repository = new JsonProjectRepository(new FileLogger(Path.Combine(root, "legacy-test.log")), path);
        await File.WriteAllBytesAsync(path, original);
        if ((await repository.LoadAsync()).IsRecoverySafe) throw new Exception("Fixture did not require legacy migration.");
        //CSJ,variable=review,レビュー
        var review = ProjectListIdentityRecovery.Inspect(original);
        ExpectInvalid(() => ProjectListIdentityRecovery.Repair(original, review, new Dictionary<Guid, int>()));
        await ProjectListIdentityRecovery.ApplyAsync(repository, review, new Dictionary<Guid, int>(), backup, default, new HashSet<int> { 0 });
        //CSJ,variable=saved,保存済み
        var saved = await repository.LoadAsync();
        if (!saved.IsRecoverySafe || saved.Projects[0].ProjectId is null || saved.Projects[0].LinkedYmmpFiles.Single().Id == Guid.Empty ||
            saved.Projects[1].ProjectId is not null)
            throw new Exception("Migration did not constrain changes to the explicitly selected legacy row.");
        using var output = JsonDocument.Parse(await File.ReadAllBytesAsync(path));
        var row = output.RootElement.GetProperty("Projects")[0];
        //CSJ,variable=restored,復元済み
        var restored = await File.ReadAllBytesAsync(backup);
        if (row.GetProperty("FutureProject").GetInt32() != 4 ||
            row.GetProperty("LinkedYmmpFiles")[0].GetProperty("FutureLink").GetInt32() != 7 ||
            !restored.SequenceEqual(original)) throw new Exception("Migration discarded opaque fields or original bytes.");
    }

    //CSJ,method=RunApplyAsync,適用を実行する（非同期）
    private static async Task RunApplyAsync(string root, Guid duplicate)
    {
        //CSJ,variable=path,パス
        var path = Path.Combine(root, "active.json");
        //CSJ,variable=folderId,フォルダーID
        var folderId = Guid.NewGuid();
        object Project(string file, string note) => new
        {
            FullPath = Path.Combine(root, file), ProjectId = duplicate, FolderId = folderId, FutureProject = note,
            LinkedYmmpFiles = new[] { new { Id = Guid.NewGuid(), FilePath = Path.Combine(root, file), FutureLink = note } },
        };
        var original = JsonSerializer.SerializeToUtf8Bytes(new
        {
            Projects = new[] { Project("a.ymmp", "a"), Project("b.ymmp", "b") },
            Folders = new[] { new { Id = folderId, Name = "fixture", FutureFolder = true } },
            FutureRoot = 42,
        });
        await File.WriteAllBytesAsync(path, original);
        //CSJ,variable=review,レビュー
        var review = ProjectListIdentityRecovery.Inspect(original);
        var chosen = new Dictionary<Guid, int> { [duplicate] = 0 };
        //CSJ,variable=logger,ロガー
        var logger = new FileLogger(Path.Combine(root, "test.log"));
        //CSJ,variable=backup,バックアップ
        var backup = Path.Combine(root, "original-active.json");
        //CSJ,variable=existingReplacement,既存置換
        var existingReplacement = JsonSerializer.SerializeToUtf8Bytes(new { Projects = Array.Empty<object>(), Folders = Array.Empty<object>() });
        var conflictedRepo = new JsonProjectRepository(logger, path, beforeReplace: () => File.WriteAllBytes(path, existingReplacement));
        try
        {
            await ProjectListIdentityRecovery.ApplyAsync(conflictedRepo, review, chosen, backup, default);
            throw new Exception("Repair overwrote concurrent content.");
        }
        catch (ProjectListConflictException) { }
        //CSJ,variable=savedBackup,保存済みバックアップ
        var savedBackup = await File.ReadAllBytesAsync(backup);
        //CSJ,variable=afterConflict,変更後競合
        var afterConflict = await File.ReadAllBytesAsync(path);
        if (!savedBackup.SequenceEqual(original) || !afterConflict.SequenceEqual(existingReplacement))
            throw new Exception("Failed repair lost the backup or current data.");

        await File.WriteAllBytesAsync(path, original);
        //CSJ,variable=repo,リポジトリ
        var repo = new JsonProjectRepository(logger, path);
        //CSJ,variable=secondBackup,2番目バックアップ
        var secondBackup = Path.Combine(root, "original-active-2.json");
        await ProjectListIdentityRecovery.ApplyAsync(repo, review, chosen, secondBackup, default);
        var reloaded = await repo.LoadAsync();
        //CSJ,variable=secondOriginal,2番目元の
        var secondOriginal = await File.ReadAllBytesAsync(secondBackup);
        if (!secondOriginal.SequenceEqual(original) || !reloaded.IsRecoverySafe ||
            reloaded.Projects.Select(project => project.ProjectId).Distinct().Count() != 2 || reloaded.Projects[0].ProjectId != duplicate)
            throw new Exception("Applied repair did not retain the selected original or preserve backup bytes.");
        //CSJ,variable=afterRepair,変更後修復
        using var afterRepair = JsonDocument.Parse(await File.ReadAllBytesAsync(path));
        var rootAfterRepair = afterRepair.RootElement;
        var repairedRows = rootAfterRepair.GetProperty("Projects");
        if (rootAfterRepair.GetProperty("FutureRoot").GetInt32() != 42 ||
            !rootAfterRepair.GetProperty("Folders")[0].GetProperty("FutureFolder").GetBoolean() ||
            repairedRows[0].GetProperty("FutureProject").GetString() != "a" ||
            repairedRows[1].GetProperty("LinkedYmmpFiles")[0].GetProperty("FutureLink").GetString() != "b")
            throw new Exception("Typed application discarded existing opaque fields.");
    }

    private static void ExpectInvalid(Action operation)
    {
        try { operation(); }
        catch (InvalidDataException) { return; }
        throw new Exception("Invalid duplicate recovery request was accepted.");
    }
}
