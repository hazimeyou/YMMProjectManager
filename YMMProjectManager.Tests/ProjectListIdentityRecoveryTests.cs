using System.IO;
using System.Text;
using System.Text.Json;
using YMMProjectManager.Application;
using YMMProjectManager.Infrastructure;

internal static class ProjectListIdentityRecoveryTests
{
    internal static async Task RunAsync(string workRoot)
    {
        var root = Path.Combine(workRoot, "duplicate-identity");
        Directory.CreateDirectory(root);
        var duplicate = Guid.NewGuid();
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
        var review = ProjectListIdentityRecovery.Inspect(input);
        var conflict = review.Conflicts.Single();
        if (conflict.Id != duplicate || conflict.Entries.Count != 2 || conflict.Entries.Any(entry => !entry.HasMetadata))
            throw new Exception("Duplicate review missed an association or candidate.");
        var keepers = new Dictionary<Guid, int> { [duplicate] = conflict.Entries[0].Row };
        var repaired = ProjectListIdentityRecovery.Repair(input, review, keepers);
        using var document = JsonDocument.Parse(repaired);
        var payload = document.RootElement;
        var rows = payload.GetProperty("Projects");
        var newId = rows[1].GetProperty("ProjectId").GetGuid();
        if (rows[0].GetProperty("ProjectId").GetGuid() != duplicate || rows[0].GetProperty("MetadataAssociation").GetProperty("Detail").GetString() != "keep" ||
            newId == duplicate || newId == existing || rows[1].TryGetProperty("MetadataAssociation", out _) ||
            rows[1].GetProperty("Unknown")[0].GetInt32() != 3 || rows[2].GetProperty("ProjectId").GetGuid() != existing ||
            rows[3].TryGetProperty("ProjectId", out _) || payload.GetProperty("FutureRoot").GetProperty("info").GetString() != "preserved")
            throw new Exception("Repair changed a keeper, unrelated identity, or unknown data.");

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
    }

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
        var path = Path.Combine(root, "legacy-active.json");
        var backup = Path.Combine(root, "legacy-original.json");
        var repository = new JsonProjectRepository(new FileLogger(Path.Combine(root, "legacy-test.log")), path);
        await File.WriteAllBytesAsync(path, original);
        if ((await repository.LoadAsync()).IsRecoverySafe) throw new Exception("Fixture did not require legacy migration.");
        var review = ProjectListIdentityRecovery.Inspect(original);
        ExpectInvalid(() => ProjectListIdentityRecovery.Repair(original, review, new Dictionary<Guid, int>()));
        await ProjectListIdentityRecovery.ApplyAsync(repository, review, new Dictionary<Guid, int>(), backup, default, new HashSet<int> { 0 });
        var saved = await repository.LoadAsync();
        if (!saved.IsRecoverySafe || saved.Projects[0].ProjectId is null || saved.Projects[0].LinkedYmmpFiles.Single().Id == Guid.Empty ||
            saved.Projects[1].ProjectId is not null)
            throw new Exception("Migration did not constrain changes to the explicitly selected legacy row.");
        using var output = JsonDocument.Parse(await File.ReadAllBytesAsync(path));
        var row = output.RootElement.GetProperty("Projects")[0];
        var restored = await File.ReadAllBytesAsync(backup);
        if (row.GetProperty("FutureProject").GetInt32() != 4 ||
            row.GetProperty("LinkedYmmpFiles")[0].GetProperty("FutureLink").GetInt32() != 7 ||
            !restored.SequenceEqual(original)) throw new Exception("Migration discarded opaque fields or original bytes.");
    }

    private static async Task RunApplyAsync(string root, Guid duplicate)
    {
        var path = Path.Combine(root, "active.json");
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
        var review = ProjectListIdentityRecovery.Inspect(original);
        var chosen = new Dictionary<Guid, int> { [duplicate] = 0 };
        var logger = new FileLogger(Path.Combine(root, "test.log"));
        var backup = Path.Combine(root, "original-active.json");
        var existingReplacement = JsonSerializer.SerializeToUtf8Bytes(new { Projects = Array.Empty<object>(), Folders = Array.Empty<object>() });
        var conflictedRepo = new JsonProjectRepository(logger, path, beforeReplace: () => File.WriteAllBytes(path, existingReplacement));
        try
        {
            await ProjectListIdentityRecovery.ApplyAsync(conflictedRepo, review, chosen, backup, default);
            throw new Exception("Repair overwrote concurrent content.");
        }
        catch (ProjectListConflictException) { }
        var savedBackup = await File.ReadAllBytesAsync(backup);
        var afterConflict = await File.ReadAllBytesAsync(path);
        if (!savedBackup.SequenceEqual(original) || !afterConflict.SequenceEqual(existingReplacement))
            throw new Exception("Failed repair lost the backup or current data.");

        await File.WriteAllBytesAsync(path, original);
        var repo = new JsonProjectRepository(logger, path);
        var secondBackup = Path.Combine(root, "original-active-2.json");
        await ProjectListIdentityRecovery.ApplyAsync(repo, review, chosen, secondBackup, default);
        var reloaded = await repo.LoadAsync();
        var secondOriginal = await File.ReadAllBytesAsync(secondBackup);
        if (!secondOriginal.SequenceEqual(original) || !reloaded.IsRecoverySafe ||
            reloaded.Projects.Select(project => project.ProjectId).Distinct().Count() != 2 || reloaded.Projects[0].ProjectId != duplicate)
            throw new Exception("Applied repair did not retain the selected original or preserve backup bytes.");
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
