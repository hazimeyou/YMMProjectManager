using System.IO;
using System.Security.AccessControl;
using System.Security.Principal;
using YMMProjectManager.Domain;
using YMMProjectManager.Infrastructure;
using YMMProjectManager.Infrastructure.Generations;

internal static class GenerationRestoreSafetyTests
{
    private const string Snapshot = "generation snapshot";
    private const string Original = "recent unsaved project";
    private const string Unrelated = "independent file";

    internal static async Task RunAsync(string root)
    {
        Directory.CreateDirectory(root);
        await Program.RunNamedCaseAsync("Restore keeps the exact original and assigns a distinct backup to each restore", async () =>
        {
            var fixture = await CreateAsync(root, "repeat");
            var original = await ProjectFileEvidence.ReadAsync(fixture.Project);
            var first = await fixture.RestoreAsync();
            Require(first.Success && File.ReadAllText(fixture.Project) == Snapshot, "Ordinary restore failed: " + first.ErrorMessage);
            var backup = Directory.EnumerateFiles(fixture.BackupDirectory).Single();
            Require(await ProjectFileEvidence.ReadAsync(backup) == original && File.ReadAllText(backup) == Original,
                "The exact original file was not retained.");
            await File.WriteAllTextAsync(fixture.Project, "subsequent work");
            var next = await fixture.RestoreAsync();
            Require(next.Success && File.ReadAllText(fixture.Project) == Snapshot && File.ReadAllText(backup) == Original,
                "The second restore changed an existing backup.");
            Require(Directory.GetFiles(fixture.BackupDirectory).Select(File.ReadAllText).ToHashSet()
                .SetEquals([Original, "subsequent work"]), "Each restore needs its own backup.");
            fixture.RequireCleanCandidate();
        });

        await Program.RunNamedCaseAsync("Restore preserves an explicit access rule and the original creation time", async () =>
        {
            var fixture = await CreateAsync(root, "explicit-acl");
            var file = new FileInfo(fixture.Project);
            var security = file.GetAccessControl();
            security.SetAccessRuleProtection(isProtected: true, preserveInheritance: true);
            security.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(WellKnownSidType.BuiltinGuestsSid, null),
                FileSystemRights.Read, AccessControlType.Allow));
            file.SetAccessControl(security);
            File.SetCreationTimeUtc(fixture.Project, new DateTime(2015, 1, 23, 12, 0, 0, DateTimeKind.Utc));
            var expectedTime = File.GetCreationTimeUtc(fixture.Project);
            const AccessControlSections sections = AccessControlSections.Access | AccessControlSections.Owner | AccessControlSections.Group;
            string Descriptor()
            {
                var acl = new RawSecurityDescriptor(file.GetAccessControl().GetSecurityDescriptorSddlForm(sections));
                acl.SetFlags(acl.ControlFlags & ~ControlFlags.DiscretionaryAclAutoInherited);
                return acl.GetSddlForm(sections);
            }
            var expected = Descriptor();

            var restored = await fixture.RestoreAsync();
            Require(restored.Success, restored.ErrorMessage ?? "Restore failed.");
            Require(Descriptor() == expected &&
                File.GetCreationTimeUtc(fixture.Project) == expectedTime,
                "Restore changed the original access rules or creation time.");
            fixture.RequireCleanCandidate();
        });

        await Program.RunNamedCaseAsync("Restore rejects unsupported extra streams before changing a project", async () =>
        {
            var fixture = await CreateAsync(root, "alternate-stream");
            var extraStream = fixture.Project + ":synthetic";
            await File.WriteAllTextAsync(extraStream, Unrelated);
            var original = await ProjectFileEvidence.ReadAsync(fixture.Project);

            var restored = await fixture.RestoreAsync();
            Require(!restored.Success && await ProjectFileEvidence.ReadAsync(fixture.Project) == original &&
                File.ReadAllText(extraStream) == Unrelated && !Directory.EnumerateFiles(fixture.BackupDirectory).Any(),
                "Restore lost or changed the extra data stream.");
            fixture.RequireCleanCandidate();
        });

        await Program.RunNamedCaseAsync("Restore refuses a read-only project before parking it", async () =>
        {
            var fixture = await CreateAsync(root, "read-only");
            var attributes = File.GetAttributes(fixture.Project);
            var original = await ProjectFileEvidence.ReadAsync(fixture.Project);
            try
            {
                File.SetAttributes(fixture.Project, attributes | FileAttributes.ReadOnly);
                var restored = await fixture.RestoreAsync();
                Require(!restored.Success && await ProjectFileEvidence.ReadAsync(fixture.Project) == original &&
                    (File.GetAttributes(fixture.Project) & FileAttributes.ReadOnly) != 0 &&
                    !Directory.EnumerateFiles(fixture.BackupDirectory).Any(),
                    "Restore modified a read-only PF.");
                fixture.RequireCleanCandidate();
            }
            finally
            {
                File.SetAttributes(fixture.Project, attributes);
            }
        });

        await Program.RunNamedCaseAsync("Restore refuses an occupied backup without changing either file", async () =>
        {
            var fixture = await CreateAsync(root, "backup-conflict");
            var original = await ProjectFileEvidence.ReadAsync(fixture.Project);
            string? occupiedBackup = null;
            var service = fixture.WithCommitHook(path =>
            {
                occupiedBackup = path;
                File.WriteAllText(path, Unrelated);
            });
            var result = await service.RestoreGenerationAsync(fixture.Project, fixture.GenerationId, GenerationRestoreMode.RestoreToOriginalWithBackup);
            Require(!result.Success && occupiedBackup is not null && File.ReadAllText(occupiedBackup) == Unrelated,
                "The competing backup was overwritten.");
            Require(await ProjectFileEvidence.ReadAsync(fixture.Project) == original, "The project changed on backup conflict.");
            fixture.RequireCleanCandidate();
        });

        await Program.RunNamedCaseAsync("Restore detects a newly occupied destination and publishes to a truly empty target", async () =>
        {
            var fixture = await CreateAsync(root, "new-destination");
            File.Delete(fixture.Project); // The test created this disposable PF.
            var competing = fixture.WithCommitHook(path => File.WriteAllText(path, Unrelated));
            var refused = await competing.RestoreGenerationAsync(fixture.Project, fixture.GenerationId, GenerationRestoreMode.RestoreToOriginalWithBackup);
            Require(!refused.Success && File.ReadAllText(fixture.Project) == Unrelated, "Restore replaced a newly created target.");
            File.Delete(fixture.Project); // The test created the competing file as well.
            var restored = await fixture.RestoreAsync();
            Require(restored.Success && File.ReadAllText(fixture.Project) == Snapshot &&
                !Directory.EnumerateFiles(fixture.BackupDirectory).Any(), "Missing target restore created the wrong state.");
            fixture.RequireCleanCandidate();
        });

        await Program.RunNamedCaseAsync("Restore prevents direct replacement of the held original and cancels before committing", async () =>
        {
            var fixture = await CreateAsync(root, "held-original");
            var original = await ProjectFileEvidence.ReadAsync(fixture.Project);
            using var cancellation = new CancellationTokenSource();
            var denied = false;
            var service = fixture.WithCommitHook(_ =>
            {
                try { using var writer = new FileStream(fixture.Project, FileMode.Open, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete); }
                catch (IOException) { denied = true; }
                cancellation.Cancel();
            });
            var result = await service.RestoreGenerationAsync(fixture.Project, fixture.GenerationId,
                GenerationRestoreMode.RestoreToOriginalWithBackup, cancellation.Token);
            Require(!result.Success && denied && await ProjectFileEvidence.ReadAsync(fixture.Project) == original,
                "Restore allowed writes after identifying the original.");
            Require(!Directory.EnumerateFiles(fixture.BackupDirectory).Any(), "Cancelled preparation parked the original.");
            fixture.RequireCleanCandidate();
        });

        await Program.RunNamedCaseAsync("Restore cancellation after parking restores the exact original", async () =>
        {
            var fixture = await CreateAsync(root, "cancel-parked");
            var original = await ProjectFileEvidence.ReadAsync(fixture.Project);
            using var cancellation = new CancellationTokenSource();
            var service = fixture.WithCommitHook(afterParking: cancellation.Cancel);
            var result = await service.RestoreGenerationAsync(fixture.Project, fixture.GenerationId,
                GenerationRestoreMode.RestoreToOriginalWithBackup, cancellation.Token);
            Require(!result.Success && await ProjectFileEvidence.ReadAsync(fixture.Project) == original,
                "Cancellation after parking did not roll back the original.");
            Require(!Directory.EnumerateFiles(fixture.BackupDirectory).Any(), "Successful rollback left an orphan backup.");
            fixture.RequireCleanCandidate();
        });

        await Program.RunNamedCaseAsync("Restore reports the retained original when a new file prevents rollback", async () =>
        {
            var fixture = await CreateAsync(root, "rollback-conflict");
            var original = await ProjectFileEvidence.ReadAsync(fixture.Project);
            var service = fixture.WithCommitHook(afterParking: () => File.WriteAllText(fixture.Project, Unrelated));
            var result = await service.RestoreGenerationAsync(fixture.Project, fixture.GenerationId, GenerationRestoreMode.RestoreToOriginalWithBackup);
            var backup = Directory.EnumerateFiles(fixture.BackupDirectory).Single();
            Require(!result.Success && File.ReadAllText(fixture.Project) == Unrelated &&
                await ProjectFileEvidence.ReadAsync(backup) == original &&
                result.ErrorMessage?.Contains(backup, StringComparison.Ordinal) == true,
                "A restore conflict lost the original or hid its recovery path.");
            fixture.RequireCleanCandidate();
        });
    }

    private static async Task<Fixture> CreateAsync(string root, string name)
    {
        var path = Path.Combine(root, name);
        Directory.CreateDirectory(path);
        var project = Path.Combine(path, "synthetic.ymmp");
        await File.WriteAllTextAsync(project, Snapshot);
        var storage = new ProjectGenerationStorage(Path.Combine(path, "storage"));
        var logger = new FileLogger(Path.Combine(path, "tests.log"));
        var service = new ProjectGenerationService(logger, storage);
        var generation = await service.CreateGenerationAsync(project, "snapshot", null);
        await File.WriteAllTextAsync(project, Original);
        return new Fixture(path, project, generation.GenerationId, storage, logger);
    }

    private sealed record Fixture(string Root, string Project, string GenerationId, ProjectGenerationStorage Storage, FileLogger Logger)
    {
        internal string BackupDirectory => Storage.GetRestoreBackupsDirectory(new ProjectGenerationHashService().ComputeProjectId(Project));

        internal Task<(bool Success, string? ErrorMessage, ProjectGenerationRecord? Generation)> RestoreAsync() =>
            new ProjectGenerationService(Logger, Storage).RestoreGenerationAsync(Project, GenerationId, GenerationRestoreMode.RestoreToOriginalWithBackup);

        internal ProjectGenerationService WithCommitHook(Action<string>? beforeCommit = null, Action? afterParking = null) =>
            new(Logger, Storage, beforeRestoreCommit: beforeCommit, afterRestoreParking: afterParking);

        internal void RequireCleanCandidate() =>
            Require(!Directory.EnumerateFiles(Root, "*.restore.tmp").Any(), "Owned restore candidate was left behind.");
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
