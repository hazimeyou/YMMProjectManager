using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using YMMProjectManager.Infrastructure.Generations;

namespace YMMProjectManager.Tests;

internal static class LegacyGenerationExportTests
{
    internal static async Task RunAsync(string workRoot)
    {
        var root = Path.Combine(workRoot, nameof(LegacyGenerationExportTests));
        await Program.RunNamedCaseAsync("旧世代を独立PFへ救済し破損したmanifestと現行PFを保全", async () =>
        {
            var files = CreateSnapshot(Path.Combine(root, "export"), retired: false);
            var approved = await LegacyGenerationExport.InspectAsync(files.Metadata, default);
            await LegacyGenerationExport.ExportAsync(approved, files.Output, default);
            Equal(File.ReadAllText(files.Snapshot), File.ReadAllText(files.Output), "The exported PF should be byte-equivalent.");
            AssertPreserved(files);
        });
        await Program.RunNamedCaseAsync("退避削除済みの世代を元の保管場所から救済", async () =>
        {
            var files = CreateSnapshot(Path.Combine(root, "retired"), retired: true);
            var approved = await LegacyGenerationExport.InspectAsync(files.Metadata, default);
            await LegacyGenerationExport.ExportAsync(approved, files.Output, default);
            Equal(File.ReadAllText(files.Snapshot), File.ReadAllText(files.Output), "The retired snapshot should also be exported.");
            AssertPreserved(files);
        });
        await Program.RunNamedCaseAsync("破損または差し替えられた旧世代の公開を拒否", async () =>
        {
            var files = CreateSnapshot(Path.Combine(root, "changed"), retired: false);
            var approved = await LegacyGenerationExport.InspectAsync(files.Metadata, default);
            await File.WriteAllTextAsync(files.Snapshot, "{\"frame\":456}");
            await RefuseAsync(() => LegacyGenerationExport.ExportAsync(approved, files.Output, default));
            Equal("{\"frame\":456}", File.ReadAllText(files.Snapshot), "The changed source must remain available.");
            Check(!File.Exists(files.Output), "A hash mismatch must never be published.");
            Check(!Directory.EnumerateFiles(files.Root, ".ymmpm-legacy-export-*.tmp").Any(), "An owned stage must be cleaned.");
        });
        await Program.RunNamedCaseAsync("確認後に同一バイト列で置き換えられた旧世代を拒否", async () =>
        {
            var files = CreateSnapshot(Path.Combine(root, "replaced"), retired: false);
            var approved = await LegacyGenerationExport.InspectAsync(files.Metadata, default);
            var originalSnapshot = files.Snapshot + ".original";
            File.Move(files.Snapshot, originalSnapshot);
            File.Copy(originalSnapshot, files.Snapshot);
            await RefuseAsync(() => LegacyGenerationExport.ExportAsync(approved, files.Output, default));
            AssertPreserved(files);
            Check(!File.Exists(files.Output), "Replacement after approval must not produce a PF.");
        });
        await Program.RunNamedCaseAsync("公開直前の競合を上書きせず既存PFと旧世代を保全", async () =>
        {
            var files = CreateSnapshot(Path.Combine(root, "collision"), retired: false);
            var approved = await LegacyGenerationExport.InspectAsync(files.Metadata, default);
            await RefuseAsync(() => LegacyGenerationExport.ExportAsync(approved, files.Output, default, () =>
                File.WriteAllText(files.Output, "external")));
            Equal("external", File.ReadAllText(files.Output), "A competing output must be preserved.");
            AssertPreserved(files);
        });
        await Program.RunNamedCaseAsync("取消と元PFへの出力を拒否し所有一時ファイルを整理", async () =>
        {
            var files = CreateSnapshot(Path.Combine(root, "cancel"), retired: false);
            var approved = await LegacyGenerationExport.InspectAsync(files.Metadata, default);
            await RefuseAsync(() => LegacyGenerationExport.ExportAsync(approved, files.Original, default));
            await RefuseAsync(() => LegacyGenerationExport.ExportAsync(approved, Path.Combine(Path.GetDirectoryName(files.Metadata)!, "export.ymmp"), default));
            using var cancel = new CancellationTokenSource();
            try
            {
                await LegacyGenerationExport.ExportAsync(approved, files.Output, cancel.Token, cancel.Cancel);
                throw new Exception("The cancelled operation published a PF.");
            }
            catch (OperationCanceledException) { }
            Check(!File.Exists(files.Output), "Cancellation must not publish a PF.");
            AssertPreserved(files);
        });
        await Program.RunNamedCaseAsync("根拠を欠く・重複する旧メタデータを拒否", async () =>
        {
            var files = CreateSnapshot(Path.Combine(root, "metadata"), retired: false);
            var metadata = File.ReadAllText(files.Metadata);
            await File.WriteAllTextAsync(files.Metadata, metadata.Insert(1, "\"FileSize\":0,"));
            await RefuseAsync(() => LegacyGenerationExport.InspectAsync(files.Metadata, default));
            await File.WriteAllTextAsync(files.Metadata, "{}");
            await RefuseAsync(() => LegacyGenerationExport.InspectAsync(files.Metadata, default));
            Check(!File.Exists(files.Output), "Invalid metadata must not produce a PF.");
        });
    }

    private static async Task RefuseAsync(Func<Task> run)
    {
        try
        {
            await run();
            throw new Exception("A rejected operation unexpectedly succeeded.");
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or JsonException) { }
    }

    private static Fixture CreateSnapshot(string root, bool retired)
    {
        const string id = "20261125-023000_012345ab";
        var projectStorage = Path.Combine(root, "Generations", "projects", new string('A', 64));
        string directory = retired
            ? Path.Combine(projectStorage, "deleted", id + "_20261125-023001")
            : Path.Combine(projectStorage, "generations", id);
        Directory.CreateDirectory(directory);
        var original = Path.Combine(root, "current.ymmp");
        var manifest = Path.Combine(projectStorage, "manifest.json");
        var snapshot = Path.Combine(directory, "project.ymmp");
        var metadata = Path.Combine(directory, "metadata.json");
        File.WriteAllText(original, "{\"frame\":789}");
        File.WriteAllText(snapshot, "{\"frame\":123}");
        File.WriteAllText(manifest, "{interrupted");
        var content = File.ReadAllBytes(snapshot);
        var values = new
        {
            GenerationId = id,
            CreatedAt = DateTimeOffset.Parse("2026-11-25T02:30:00+09:00"),
            DisplayName = "旧編集版",
            Memo = (string?)null,
            SourceProjectPath = original,
            FileSize = content.LongLength,
            Sha256 = Convert.ToHexString(SHA256.HashData(content)),
            CreatedByVersion = "0.4.0",
        };
        var metadataText = JsonSerializer.Serialize(values);
        File.WriteAllText(metadata, metadataText);
        return new Fixture(root, original, snapshot, metadata, manifest, Path.Combine(root, "copy.ymmp"), metadataText);
    }

    private static void AssertPreserved(Fixture files)
    {
        Equal("{\"frame\":789}", File.ReadAllText(files.Original), "The original PF must remain untouched.");
        Equal("{\"frame\":123}", File.ReadAllText(files.Snapshot), "The snapshot must remain untouched.");
        Equal(files.MetadataText, File.ReadAllText(files.Metadata), "The metadata must remain untouched.");
        Equal("{interrupted", File.ReadAllText(files.Manifest), "The manifest must remain untouched.");
        Check(!Directory.EnumerateFiles(files.Root, ".ymmpm-legacy-export-*.tmp").Any(), "No owned stage should remain.");
    }

    private static void Equal(string expected, string actual, string message) =>
        Check(string.Equals(expected, actual, StringComparison.Ordinal), message);

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new Exception(message);
    }

    private sealed record Fixture(string Root, string Original, string Snapshot, string Metadata, string Manifest, string Output, string MetadataText);
}
