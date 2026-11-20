using System.IO;
using YMMProjectManager.Application;
using YMMProjectManager.Domain;
using YMMProjectManager.Infrastructure;

internal static class ProjectListRecoveryFilesTests
{
    internal static async Task RunAsync(string workRoot)
    {
        var folder = Path.Combine(workRoot, "recovery-file-lock");
        Directory.CreateDirectory(folder);
        var primary = Path.Combine(folder, "new.ymmp");
        await File.WriteAllTextAsync(primary, "{}");
        var identity = await ProjectFileEvidence.ReadAsync(primary);
        var entry = new ProjectEntry { ProjectId = Guid.NewGuid(), FullPath = primary, FileIdentity = identity };
        var target = new ProjectStore { Projects = [entry] };
        var empty = new ProjectStore();
        using (await ProjectListRecoveryFiles.OpenAsync(target, empty, _ => null))
        {
            try { await File.WriteAllTextAsync(primary, "changed"); throw new Exception("A verified recovery target accepted a concurrent write."); }
            catch (IOException) { }
        }

        File.Move(primary, Path.Combine(folder, "original.ymmp"));
        await File.WriteAllTextAsync(primary, "{}");
        if (identity.Sha256 != (await ProjectFileEvidence.ReadAsync(primary)).Sha256)
            throw new Exception("Replacement fixture did not have identical bytes.");
        try { using var lease = await ProjectListRecoveryFiles.OpenAsync(target, empty, _ => null); throw new Exception("Identical bytes bypassed file identity."); }
        catch (InvalidDataException) { }

        // Keeping the already-persisted alternative must not require evidence for the rejected local row.
        using var same = await ProjectListRecoveryFiles.OpenAsync(target, target, _ => throw new Exception("Unchanged rows should not be revalidated."));
    }
}
