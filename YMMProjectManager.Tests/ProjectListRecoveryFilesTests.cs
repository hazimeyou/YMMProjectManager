using System.IO;
using YMMProjectManager.Application;
using YMMProjectManager.Domain;
using YMMProjectManager.Infrastructure;

//CSJ,class=ProjectListRecoveryFilesTests,プロジェクト一覧復旧ファイル一覧テスト
internal static class ProjectListRecoveryFilesTests
{
    //CSJ,method=RunAsync,実行する（非同期）
    internal static async Task RunAsync(string workRoot)
    {
        //CSJ,variable=folder,フォルダー
        var folder = Path.Combine(workRoot, "recovery-file-lock");
        Directory.CreateDirectory(folder);
        var primary = Path.Combine(folder, "new.ymmp");
        await File.WriteAllTextAsync(primary, "{}");
        //CSJ,variable=identity,識別情報
        var identity = await ProjectFileEvidence.ReadAsync(primary);
        var entry = new ProjectEntry { ProjectId = Guid.NewGuid(), FullPath = primary, FileIdentity = identity };
        var target = new ProjectStore { Projects = [entry] };
        //CSJ,variable=empty,空
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
        //CSJ,variable=same,同一
        using var same = await ProjectListRecoveryFiles.OpenAsync(target, target, _ => throw new Exception("Unchanged rows should not be revalidated."));
    }
}
