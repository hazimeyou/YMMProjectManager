using System.IO;
using System.Security.Cryptography;
using System.Text.Json;

namespace YMMProjectManager.Infrastructure.Generations;

/// <summary>Exports a verified legacy snapshot without changing generation storage or the original PF.</summary>
internal static class LegacyGenerationExport
{
    private const int MaxMetadataBytes = 4 * 1024 * 1024;

    internal sealed record Snapshot(
        string MetadataPath, string OriginalPath, string GenerationId, long Size, string Sha256,
        (ulong Volume, Guid FileId) FileIdentity, (ulong Volume, Guid FileId) MetadataIdentity);

    internal static Task<Snapshot> InspectAsync(string metadataPath, CancellationToken token) =>
        Task.Run(async () =>
        {
            var path = GetLocalPath(metadataPath);
            var directory = GetSnapshotDirectory(path);
            using var fence = OwnedFilePublication.DirectoryFence.Open(directory.FullName, enforceShare: true);
            using var metadata = OpenRead(path);
            using var snapshot = OpenRead(Path.Combine(directory.FullName, "project.ymmp"));
            return await ReadSnapshotAsync(path, directory, metadata, snapshot, token).ConfigureAwait(false);
        }, token);

    internal static Task ExportAsync(Snapshot approved, string outputPath, CancellationToken token, Action? beforePublish = null) =>
        Task.Run(async () =>
        {
            var destination = GetLocalPath(outputPath);
            var directory = GetSnapshotDirectory(approved.MetadataPath);
            var outputDirectory = Path.GetDirectoryName(destination)!;
            if (!string.Equals(Path.GetExtension(destination), ".ymmp", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(destination, approved.OriginalPath, StringComparison.OrdinalIgnoreCase) ||
                IsWithin(outputDirectory, directory.Parent!.Parent!.FullName) ||
                IsWithin(outputDirectory, StorageRoot))
                throw new IOException("旧世代の保管先と元PFを避け、新しい .ymmp ファイル名を指定してください。");

            using var sourceFence = OwnedFilePublication.DirectoryFence.Open(directory.FullName, enforceShare: true);
            using var outputFence = OwnedFilePublication.DirectoryFence.Open(outputDirectory, enforceShare: true);
            using var metadata = OpenRead(approved.MetadataPath);
            using var source = OpenRead(Path.Combine(directory.FullName, "project.ymmp"));
            var verified = await ReadSnapshotAsync(approved.MetadataPath, directory, metadata, source, token).ConfigureAwait(false);
            if (approved != verified)
                throw new IOException("確認後に旧世代データが変わりました。選択し直してください。");

            var temporary = Path.Combine(outputDirectory, $".ymmpm-legacy-export-{Guid.NewGuid():N}.tmp");
            await using var staged = OwnedFilePublication.CreateOwnedOutputFile(temporary, readBack: true);
            var published = false;
            try
            {
                await source.CopyToAsync(staged, token).ConfigureAwait(false);
                await staged.FlushAsync(token).ConfigureAwait(false);
                staged.Flush(flushToDisk: true);
                staged.Position = 0;
                var observed = Convert.ToHexString(await SHA256.HashDataAsync(staged, token).ConfigureAwait(false));
                if (staged.Length != verified.Size || !string.Equals(observed, verified.Sha256, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("旧世代のサイズまたは SHA-256 が一致しません。");

                beforePublish?.Invoke();
                token.ThrowIfCancellationRequested();
                OwnedFilePublication.RenameOpenedPath(staged.SafeFileHandle, destination);
                published = true;
            }
            finally
            {
                if (!published) OwnedFilePublication.TryDeleteOpenedPath(staged.SafeFileHandle);
            }
        }, token);

    internal static string StorageRoot => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "YMMProjectManager", "Generations");

    private static async Task<Snapshot> ReadSnapshotAsync(
        string path, DirectoryInfo directory, FileStream metadata, FileStream snapshot, CancellationToken token)
    {
        if (metadata.Length is <= 0 or > MaxMetadataBytes)
            throw new InvalidDataException("旧世代の metadata.json のサイズが不正です。");
        var values = await JsonSerializer.DeserializeAsync<SnapshotMetadata>(metadata,
            new JsonSerializerOptions { AllowDuplicateProperties = false }, token).ConfigureAwait(false);
        if (values is null || string.IsNullOrWhiteSpace(values.GenerationId) ||
            !(string.Equals(directory.Name, values.GenerationId, StringComparison.OrdinalIgnoreCase) ||
              (string.Equals(directory.Parent!.Name, "deleted", StringComparison.OrdinalIgnoreCase) &&
               directory.Name.StartsWith(values.GenerationId + "_", StringComparison.OrdinalIgnoreCase))) ||
            values.SourceProjectPath is null || !Path.IsPathFullyQualified(values.SourceProjectPath) ||
            values.FileSize < 0 || values.FileSize != snapshot.Length ||
            values.Sha256 is not { Length: 64 } || !values.Sha256.All(Uri.IsHexDigit))
            throw new InvalidDataException("旧世代の metadata.json に対応する PF・サイズ・SHA-256 を確認できません。");
        return new Snapshot(path, Path.GetFullPath(values.SourceProjectPath), values.GenerationId, values.FileSize,
            values.Sha256, ProjectFileEvidence.ReadPhysicalIdentity(snapshot), ProjectFileEvidence.ReadPhysicalIdentity(metadata));
    }

    private sealed record SnapshotMetadata(
        string? GenerationId, string? SourceProjectPath, [property: System.Text.Json.Serialization.JsonRequired] long FileSize, string? Sha256);

    private static DirectoryInfo GetSnapshotDirectory(string metadataPath)
    {
        var directory = new FileInfo(metadataPath).Directory;
        if (directory?.Parent?.Parent is null ||
            !string.Equals(Path.GetFileName(metadataPath), "metadata.json", StringComparison.OrdinalIgnoreCase) ||
            (!string.Equals(directory.Parent.Name, "generations", StringComparison.OrdinalIgnoreCase) &&
             !string.Equals(directory.Parent.Name, "deleted", StringComparison.OrdinalIgnoreCase)))
            throw new InvalidDataException("generations または deleted 内の旧世代の metadata.json を選択してください。");
        return directory;
    }

    private static bool IsWithin(string path, string directory) =>
        string.Equals(path, directory, StringComparison.OrdinalIgnoreCase) ||
        path.StartsWith(Path.TrimEndingDirectorySeparator(directory) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);

    private static string GetLocalPath(string path)
    {
        if (!Path.IsPathFullyQualified(path) || path.StartsWith(@"\\", StringComparison.Ordinal) || path.AsSpan(2).Contains(':'))
            throw new IOException("ローカルファイルの完全パスを指定してください。");
        return Path.GetFullPath(path);
    }

    private static FileStream OpenRead(string path) =>
        new(OwnedFilePublication.OpenOrdinaryPath(path, expectDirectory: false, allowRename: false), FileAccess.Read);
}
