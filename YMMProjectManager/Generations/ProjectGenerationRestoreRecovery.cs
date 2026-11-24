using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using static YMMProjectManager.Infrastructure.OwnedFilePublication;

namespace YMMProjectManager.Infrastructure.Generations;

// This record is manual recovery evidence. Product code never trusts its paths to write files.
internal static class ProjectGenerationRestoreRecovery
{
    private sealed record Journal(int Version, Guid Operation, string TargetPath, string RetainedPath, string CandidatePath,
        ProjectListReplacement.FileVersion Original, ProjectListReplacement.FileVersion Intended);

    internal static string JournalPath(string target)
    {
        var fileName = Path.GetFileName(target).ToUpperInvariant();
        var key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(fileName)));
        return Path.Combine(Path.GetDirectoryName(target)!, $".ymm-restore-{key}.pending.json");
    }

    internal static void RequireNoPending(string target)
    {
        var journal = JournalPath(target);
        try { File.GetAttributes(journal); }
        catch (FileNotFoundException) { return; }
        catch (DirectoryNotFoundException) { return; }
        throw new IOException($"前回の復元記録が残っています。現用ファイルと退避先を保全して回復記録を確認してください。\n{journal}");
    }

    internal static async Task<FileStream> PrepareAsync(string target, string retained, string candidatePath, FileStream original,
        FileStream candidate, CancellationToken token)
    {
        var journalPath = JournalPath(target);
        var operation = Guid.NewGuid();
        var record = new Journal(1, operation, target, retained, candidatePath,
            await ProjectListReplacement.ReadFileVersionAsync(original, token).ConfigureAwait(false),
            await ProjectListReplacement.ReadFileVersionAsync(candidate, token).ConfigureAwait(false));
        var temporary = $"{journalPath}.{operation:N}.tmp";
        var output = ProjectGenerationRestorePublication.CreateCandidate(temporary, original);
        try
        {
            ProjectListReplacement.VerifyCompatibleFileSecurity(original, output, allowAclCreationMarkerDifference: true);
            await JsonSerializer.SerializeAsync(output, record, cancellationToken: token).ConfigureAwait(false);
            await output.FlushAsync(token).ConfigureAwait(false);
            output.Flush(flushToDisk: true);
            token.ThrowIfCancellationRequested();
            RenameOpenedPath(output.SafeFileHandle, journalPath);
            return output;
        }
        catch
        {
            TryDeleteOpenedPath(output.SafeFileHandle);
            await output.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }
}
