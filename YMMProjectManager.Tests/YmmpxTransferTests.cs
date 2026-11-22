using System.IO;
using System.IO.Compression;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using YMMProjectManager.Domain.Packaging;
using YMMProjectManager.Infrastructure;
using YMMProjectManager.Infrastructure.Packaging;

//CSJ,class=YmmpxTransferTests,YMMPX受け渡しテスト
internal static class YmmpxTransferTests
{
    //CSJ,method=RunAsync,実行する（非同期）
    public static async Task RunAsync(string workRoot)
    {
        //CSJ,variable=root,ルート
        var root = Path.Combine(workRoot, "r23-transfer");
        var input = Directory.CreateDirectory(Path.Combine(root, "input")).FullName;
        var outside = Directory.CreateDirectory(Path.Combine(root, "outside")).FullName;
        //CSJ,variable=left,左
        var left = CreateMaterial(Path.Combine(input, "left"), "場面.png", "first version");
        //CSJ,variable=right,右
        var right = CreateMaterial(Path.Combine(input, "right"), "場面.png", "second version");
        var alias = CreateMaterial(input, "別名.png", "first version");
        var denied = CreateMaterial(input, "再配布禁止.bin", "strictly excluded");
        //CSJ,variable=external,外部
        var external = CreateMaterial(outside, "external.bin", "external contents");
        var original = Path.Combine(input, "作品.ymmp");
        //CSJ,variable=originalJson,元のJSON
        var originalJson = new JsonObject
        {
            ["FilePath"] = original,
            ["Items"] = new JsonArray(Reference(left), Reference(right), Reference(alias), Reference(denied),
                Reference(external), Reference(@"\\unapproved.invalid\share\remote.wav")),
            ["FutureData"] = new JsonObject { ["Keep"] = "保持" },
        };
        await File.WriteAllTextAsync(original, originalJson.ToJsonString());
        //CSJ,variable=sourceBytes,元バイト列
        var sourceBytes = await File.ReadAllBytesAsync(original);
        //CSJ,variable=service,サービス
        var service = new YmmpxLibBundleService(new FileLogger(Path.Combine(root, "test.log")), baseDirectory: root);
        var permissions = new Dictionary<string, YmmpxTransferPermission>(StringComparer.Ordinal)
        {
            [left] = YmmpxTransferPermission.OwnWork,
            [right] = YmmpxTransferPermission.RedistributionAllowed,
            [alias] = YmmpxTransferPermission.OwnWork,
            [denied] = YmmpxTransferPermission.Prohibited,
        };
        //CSJ,variable=plan,計画
        YmmpxTransferPlan? plan = null;
        //CSJ,variable=created,作成済み
        YmmpxPackageResult? created = null;
        var send = Path.Combine(root, "send");
        var companion = Path.Combine(send, YmmpxTransfer.ManifestName);

        await Program.RunNamedCaseAsync("R23 transfer preview resolves only explicitly permitted roots", async () =>
        {
            plan = await service.InspectTransferAsync(original, default);
            Check(plan.Assets.Count == 6, "Candidate count");
            Check(plan.Assets.Single(asset => asset.SourcePath == left).Sha256 ==
                plan.Assets.Single(asset => asset.SourcePath == alias).Sha256, "Same contents under different names");
            Check(plan.Assets.Single(asset => asset.SourcePath == left).Sha256 !=
                plan.Assets.Single(asset => asset.SourcePath == right).Sha256, "Different contents under same name");
            Check(!plan.Assets.Single(asset => asset.SourcePath == external).CanInclude &&
                !plan.Assets.Single(asset => asset.SourcePath.StartsWith(@"\\")).CanInclude, "Unapproved path was inspected");
            var explicitlyAllowed = await service.InspectTransferAsync(original, default, [outside]);
            Check(explicitlyAllowed.Assets.Single(asset => asset.SourcePath == external).CanInclude, "Explicit outside root");
            var wrongCase = await service.InspectTransferAsync(original, default, [outside.ToUpperInvariant()]);
            Check(!wrongCase.Assets.Single(asset => asset.SourcePath == external).CanInclude, "Case-variant root was implicitly authorized");
        });

        await Program.RunNamedCaseAsync("R23 transfer publishes confirmed names and hashes with no overwrite", async () =>
        {
            created = await service.CreateTransferAsync(plan!, permissions, send, default);
            Check(created.Success && created.PackagedMaterialCount == 3 && created.MissingMaterialCount == 3, "Pack counts");
            Check(Directory.GetFiles(send).Length == 2, "Package and companion only");
            //CSJ,variable=archive,アーカイブ
            using var archive = ZipFile.OpenRead(created.OutputPath!);
            Check(archive.Comment.StartsWith(YmmpxTransfer.ArchiveCommentPrefix, StringComparison.Ordinal) &&
                archive.Entries.Count == 6, "Existing YMMPX grammar only");
            //CSJ,variable=assets,素材一覧
            var assets = archive.Entries.Where(entry => entry.FullName.StartsWith(YmmpxTransfer.MediaPrefix, StringComparison.Ordinal)).ToArray();
            Check(assets.Count(entry => entry.Name == "場面.png") == 2 &&
                assets.Count(entry => entry.Name == "別名.png") == 1, "Original filenames preserved");
            //CSJ,variable=metadata,メタデータ
            var metadata = await File.ReadAllTextAsync(companion);
            Check(!metadata.Contains(input, StringComparison.Ordinal) && !metadata.Contains(outside, StringComparison.Ordinal), "Local root leaked");
            await ExpectAsync<IOException>(() => service.CreateTransferAsync(plan!, permissions, send, default));
            //CSJ,variable=currentSource,現在の元
            var currentSource = await File.ReadAllBytesAsync(original);
            Check(sourceBytes.SequenceEqual(currentSource) &&
                await File.ReadAllTextAsync(denied) == "strictly excluded", "Source changed");
        });

        await Program.RunNamedCaseAsync("R23 transfer verifies and rewrites only referenced members and unresolved local paths", async () =>
        {
            //CSJ,variable=destination,格納先
            var destination = Path.Combine(root, "receive");
            var imported = await service.ExtractPackageAsync(created!.OutputPath!, destination, default);
            Check(imported.Success && imported.TransferEvidenceVerified && imported.ExtractedResourceCount == 3, imported.ErrorMessage ?? "Strict import");
            Check(imported.MissingMaterials.Count == 3 && imported.MissingMaterials.Contains(Path.GetFileName(denied)), "Missing list");
            Check(imported.RestoredProjectIdentity == await ProjectFileEvidence.ReadAsync(imported.RestoredProjectPath!), "Published output evidence");
            //CSJ,variable=project,プロジェクト
            var project = JsonNode.Parse(await File.ReadAllTextAsync(imported.RestoredProjectPath!))!;
            //CSJ,variable=restored,復元済み
            var restored = project["Items"]!.AsArray().Select(item => item!["FilePath"]!.GetValue<string>()).ToArray();
            Check(await File.ReadAllTextAsync(restored[0]) == "first version" &&
                await File.ReadAllTextAsync(restored[1]) == "second version" &&
                await File.ReadAllTextAsync(restored[2]) == "first version", "Reference data restored");
            Check(restored[0] != restored[1] && restored[0] != restored[2], "Reference names and paths merged");
            Check(restored.Skip(3).All(path => path.StartsWith(destination + Path.DirectorySeparatorChar, StringComparison.Ordinal) && !File.Exists(path)),
                "Unapproved paths are not opened or retained in reference fields");
            Check(project["FutureData"]!["Keep"]!.GetValue<string>() == "保持" &&
                project["FilePath"]!.GetValue<string>() == "", "Unknown fields and no source project path");
            Check(File.Exists(Path.Combine(destination, YmmpxTransfer.ManifestName)), "Evidence available for inspection");
        });

        await Program.RunNamedCaseAsync("R23 transfer rejects missing and forged per-member evidence without publishing", async () =>
        {
            var forged = Directory.CreateDirectory(Path.Combine(root, "forged")).FullName;
            //CSJ,variable=package,パッケージ
            var package = Path.Combine(forged, Path.GetFileName(created!.OutputPath!));
            //CSJ,variable=destination,格納先
            var destination = Path.Combine(root, "not-extracted");
            File.Copy(created.OutputPath!, package);
            var result = await service.ExtractPackageAsync(package, destination, default);
            Check(!result.Success && !Directory.Exists(destination), "Missing companion silently fell back");
            //CSJ,variable=changed,変更済み
            var changed = JsonNode.Parse(await File.ReadAllTextAsync(companion))!;
            var forgedCompanion = Path.Combine(forged, YmmpxTransfer.ManifestName);
            changed["assets"]![0]!["permission"] = nameof(YmmpxTransferPermission.RedistributionAllowed);
            await File.WriteAllTextAsync(forgedCompanion, changed.ToJsonString());
            result = await service.ExtractTransferAsync(package, destination, default);
            Check(!result.Success && !Directory.Exists(destination), "Changed companion metadata fell back");
            //CSJ,variable=manifestInput,目録入力
            await using var manifestInput = File.OpenRead(companion);
            //CSJ,variable=manifest,目録
            var manifest = await YmmpxTransfer.ReadManifestAsync(manifestInput, Path.GetFileName(created.OutputPath!), default);
            // Recompute the outer evidence with a deliberately false inner digest.
            manifest.Assets[0] = manifest.Assets[0] with { Sha256 = new string('0', 64) };
            //CSJ,variable=archive,アーカイブ
            using (var archive = ZipFile.Open(package, ZipArchiveMode.Update))
                archive.Comment = YmmpxTransfer.MakeArchiveComment(manifest.PackageName, manifest.Assets, manifest.Missing);
            changed["assets"]![0]!["permission"] = manifest.Assets[0].Permission.ToString();
            changed["assets"]![0]!["sha256"] = manifest.Assets[0].Sha256;
            changed["packageSha256"] = Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(package)));
            await File.WriteAllTextAsync(forgedCompanion, changed.ToJsonString());
            result = await service.ExtractTransferAsync(package, destination, default);
            Check(!result.Success && result.ErrorMessage!.Contains("素材の SHA-256", StringComparison.Ordinal) && !Directory.Exists(destination),
                "Incorrect per-asset hash fell back");
        });

        await Program.RunNamedCaseAsync("R23 transfer loses neither legacy support nor strict mode in a mixed folder", async () =>
        {
            var plain = Path.Combine(send, "legacy.ymmpx");
            //CSJ,variable=archive,アーカイブ
            using (var archive = ZipFile.Open(plain, ZipArchiveMode.Create))
            {
                WriteEntry(archive, "legacy.ymmp", "{\"FutureData\":1}");
                WriteEntry(archive, "_ymmpx_project_path.txt", "legacy.ymmp");
                WriteEntry(archive, "links.json", "{}");
            }
            var imported = await service.ExtractPackageAsync(plain, Path.Combine(root, "legacy"), default);
            Check(imported.Success && !imported.TransferEvidenceVerified, "Unrelated sidecar obstructed legacy archive");
            var stripped = Path.Combine(root, "stripped.ymmpx");
            File.Copy(created!.OutputPath!, stripped);
            //CSJ,variable=archive,アーカイブ
            using (var archive = ZipFile.Open(stripped, ZipArchiveMode.Update)) archive.Comment = "";
            //CSJ,variable=final,最終
            var final = Path.Combine(root, "stripped-output");
            imported = await service.ExtractPackageAsync(stripped, final, default);
            Check(!imported.Success && !Directory.Exists(final), "Transfer without comment or sidecar incorrectly fell back");
        });

        await Program.RunNamedCaseAsync("R23 accepts a historical exact basename alias and rejects unbound legacy data", async () =>
        {
            //CSJ,variable=legacy,旧形式
            var legacy = Path.Combine(root, "legacy-map.ymmpx");
            WriteLegacy(legacy, "絵.txt");
            var imported = await service.ExtractPackageAsync(legacy, Path.Combine(root, "verified-legacy-map"), default);
            Check(imported.Success && !imported.TransferEvidenceVerified && imported.ReplacedPathCount == 1, "Historical basename import");
            //CSJ,variable=project,プロジェクト
            var project = JsonNode.Parse(await File.ReadAllTextAsync(imported.RestoredProjectPath!))!;
            //CSJ,variable=material,素材
            var material = project["Items"]![0]!["filePath"]!.GetValue<string>();
            Check(Path.GetFileName(material) == "絵.txt" && await File.ReadAllTextAsync(material) == "legacy contents", "Legacy asset restored");

            var unmapped = Path.Combine(root, "unbound-legacy-map.ymmpx");
            //CSJ,variable=final,最終
            var final = Path.Combine(root, "unbound-legacy");
            WriteLegacy(unmapped, "..\\絵.txt");
            imported = await service.ExtractPackageAsync(unmapped, final, default);
            Check(!imported.Success && !Directory.Exists(final), "Unmatched alias must not probe a path");

            //CSJ,method=WriteLegacy,旧形式を書き込む
            static void WriteLegacy(string package, string reference)
            {
                //CSJ,variable=archive,アーカイブ
                using var archive = ZipFile.Open(package, ZipArchiveMode.Create);
                WriteEntry(archive, "source.ymmp", JsonSerializer.Serialize(new { Items = new[] { new { filePath = reference } } }));
                WriteEntry(archive, "_ymmpx_project_path.txt", "source.ymmp");
                WriteEntry(archive, "links.json", """{"絵.txt":"resources/絵.txt"}""");
                WriteEntry(archive, "resources/絵.txt", "legacy contents");
            }
        });

        await Program.RunNamedCaseAsync("R23 transfer refuses deterministic replacement of a previously staged asset", async () =>
        {
            //CSJ,variable=final,最終
            var final = Path.Combine(root, "replaced-stage");
            var injected = false;
            //CSJ,variable=outcome,処理結果
            var outcome = await service.ExtractTransferAsync(created!.OutputPath!, final, default, new InlineProgress(() =>
            {
                //CSJ,variable=directory,ディレクトリ
                var directory = Directory.EnumerateDirectories(root, ".ymmpx-*").Single();
                //CSJ,variable=media,メディア
                var media = Path.Combine(directory, "resources", "ymmpm-transfer");
                if (injected || !Directory.Exists(media)) return;
                var members = Directory.GetFiles(media, "*", SearchOption.AllDirectories);
                if (members.Length < 2) return;
                //CSJ,variable=existing,既存
                var existing = members.Single(path => Path.GetDirectoryName(path)!.EndsWith("00000000", StringComparison.Ordinal));
                File.WriteAllText(existing, "unconfirmed");
                injected = true;
            }));
            Check(injected && !outcome.Success && !Directory.Exists(final), "Modified staged asset was published");
        });

        await Program.RunNamedCaseAsync("R23 refuses to publish unknown staging files and keeps them intact", async () =>
        {
            //CSJ,variable=final,最終
            var final = Path.Combine(root, "unowned-entry");
            string? sentinel = null;
            //CSJ,variable=outcome,処理結果
            var outcome = await service.ExtractTransferAsync(created!.OutputPath!, final, default, new InlineProgress(() =>
            {
                if (sentinel is not null) return;
                //CSJ,variable=directory,ディレクトリ
                var directory = Directory.EnumerateDirectories(root, ".ymmpx-*").Single();
                sentinel = Path.Combine(directory, "unowned.txt");
                File.WriteAllText(sentinel, "external staged data");
            }));
            Check(!outcome.Success && !Directory.Exists(final) && sentinel is not null &&
                await File.ReadAllTextAsync(sentinel) == "external staged data", "An unowned staging file was published or deleted");
        });

        await Program.RunNamedCaseAsync("R23 transfer rejects approved asset replacement and leaves no partial public output", async () =>
        {
            //CSJ,variable=oldContent,旧内容
            var oldContent = await File.ReadAllBytesAsync(left);
            //CSJ,variable=destination,格納先
            var destination = Path.Combine(root, "rejected");
            try
            {
                // Same byte length isolates the SHA-256 check.
                await File.WriteAllTextAsync(left, "third version");
                await ExpectAsync<InvalidDataException>(() => service.CreateTransferAsync(plan!, permissions, destination, default));
                Check(!Directory.Exists(destination) && !Directory.EnumerateDirectories(root, ".ymmpm-transfer-*").Any(), "Failed staging cleanup");
            }
            finally { await File.WriteAllBytesAsync(left, oldContent); }
        });

        await Program.RunNamedCaseAsync("R23 transfer cancellation and late target creation preserve output and permit retry", async () =>
        {
            //CSJ,variable=retry,再試行
            var retry = Path.Combine(root, "retry");
            //CSJ,variable=cancelled,キャンセル済み
            using var cancelled = new CancellationTokenSource();
            await ExpectAsync<OperationCanceledException>(() => service.CreateTransferAsync(plan!, permissions, retry, cancelled.Token,
                new InlineProgress(cancelled.Cancel)));
            Check(!Directory.Exists(retry) && !Directory.EnumerateDirectories(root, ".ymmpm-transfer-*").Any(), "Cancelled staging cleanup");
            //CSJ,variable=collision,衝突
            var collision = Path.Combine(root, "collision");
            var sentinel = Path.Combine(collision, "sentinel");
            var setOnce = false;
            await ExpectAsync<IOException>(() => service.CreateTransferAsync(plan!, permissions, collision, default,
                new InlineProgress(() =>
                {
                    if (setOnce) return;
                    setOnce = true;
                    Directory.CreateDirectory(collision);
                    File.WriteAllText(sentinel, "external");
                })));
            Check(await File.ReadAllTextAsync(sentinel) == "external" &&
                Directory.GetFiles(collision).Length == 1 &&
                !Directory.EnumerateDirectories(root, ".ymmpm-transfer-*").Any(), "Existing target or staging changed");
            Check((await service.CreateTransferAsync(plan!, permissions, retry, default)).Success, "Safe retry");
        });

        await Program.RunNamedCaseAsync("R23 transfer keeps unsupported PNG sequences unresolved", async () =>
        {
            //CSJ,variable=sequence,順序
            var sequence = Path.Combine(input, "sequence.ymmp");
            _ = CreateMaterial(input, "frame01.png", "frame");
            await File.WriteAllTextAsync(sequence, """
                {"Items":[{"$type":"YukkuriMovieMaker.Project.Items.VideoItem, YukkuriMovieMaker","FilePath":"frame01.png"}]}
                """);
            //CSJ,variable=preview,プレビュー
            var preview = await service.InspectTransferAsync(sequence, default);
            Check(preview.Assets is [{ CanInclude: false, ExclusionReason: not null }], "Potential sequence was selectable");
            await ExpectAsync<InvalidDataException>(() => service.CreateTransferAsync(preview,
                new Dictionary<string, YmmpxTransferPermission> { [preview.Assets[0].SourcePath] = YmmpxTransferPermission.OwnWork },
                Path.Combine(root, "unexpected-sequence"), default));
        });

        await Program.RunNamedCaseAsync("R23 transfer refuses invalid source Unicode and ambiguous property spellings", async () =>
        {
            //CSJ,variable=invalid,無効
            var invalid = Path.Combine(input, "invalid.ymmp");
            await File.WriteAllBytesAsync(invalid, [(byte)'{', (byte)'"', 0xFF, (byte)'"', (byte)'}']);
            await ExpectAsync<System.Text.DecoderFallbackException>(() => service.InspectTransferAsync(invalid, default));
            await File.WriteAllTextAsync(invalid, """
                {"Items":[{"FilePath":"frame01.png","filePath":"別名.png"}]}
                """);
            await ExpectAsync<InvalidDataException>(() => service.InspectTransferAsync(invalid, default));
        });

        //CSJ,variable=library,ライブラリ
        var library = Environment.GetEnvironmentVariable("YMMPX_LIB_PATH");
        if (string.IsNullOrWhiteSpace(library) || !File.Exists(library))
        {
            Program.ReportConditionalSkip("R23 transfer compatibility with real YmmpxLib unavailable");
            return;
        }

        await Program.RunNamedCaseAsync("R23 transfer archive restores names and bytes using real YmmpxLib 1.1 API", async () =>
        {
            //CSJ,variable=assembly,アセンブリ
            var assembly = Assembly.LoadFrom(library);
            //CSJ,variable=method,メソッド
            var method = assembly.GetType("YmmpxLib.YmmpxPackageService")!.GetMethod("ExtractAndRestoreProject")!;
            //CSJ,variable=native,ネイティブ
            var native = Path.Combine(root, "native-restore");
            var extracted = method.Invoke(null, [created!.OutputPath!, native]) ?? throw new InvalidOperationException("Real extract failed");
            //CSJ,variable=restored,復元済み
            var restored = extracted.GetType().GetProperty("ProjectFilePath")!.GetValue(extracted) as string;
            Check(restored is not null && File.Exists(restored), "Real extract project");
            //CSJ,variable=document,文書
            var document = JsonNode.Parse(await File.ReadAllTextAsync(restored!))!;
            //CSJ,variable=references,参照一覧
            var references = document["Items"]!.AsArray().Select(item => item!["FilePath"]!.GetValue<string>()).ToArray();
            Check(Path.GetFileName(references[0]) == "場面.png" && Path.GetFileName(references[1]) == "場面.png" &&
                await File.ReadAllTextAsync(references[0]) == "first version" &&
                await File.ReadAllTextAsync(references[1]) == "second version", "Real extraction of namespaced resources");
            Check(references[3].StartsWith(YmmpxTransfer.MissingPrefix, StringComparison.Ordinal), "Real extraction preserves relative unresolved placeholder");
        });

        static JsonObject Reference(string path) => new() { ["FilePath"] = path };
    }

    //CSJ,method=CreateMaterial,素材を作成する
    private static string CreateMaterial(string directory, string name, string content)
    {
        Directory.CreateDirectory(directory);
        //CSJ,variable=path,パス
        var path = Path.Combine(directory, name);
        File.WriteAllText(path, content);
        return path;
    }

    //CSJ,method=WriteEntry,項目を書き込む
    private static void WriteEntry(ZipArchive archive, string name, string content)
    {
        //CSJ,variable=writer,書き込み処理
        using var writer = new StreamWriter(archive.CreateEntry(name).Open());
        writer.Write(content);
    }

    private static async Task ExpectAsync<T>(Func<Task> action) where T : Exception
    {
        try { await action(); }
        catch (T) { return; }
        throw new InvalidOperationException($"Expected {typeof(T).Name}.");
    }

    //CSJ,method=Check,確認する
    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    //CSJ,class=InlineProgress,インライン進捗
    private sealed class InlineProgress(Action action) : IProgress<double>
    {
        public void Report(double value) => action();
    }
}
