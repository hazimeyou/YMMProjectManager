using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using YMMProjectManager.Domain.Packaging;
using YMMProjectManager.Infrastructure;
using YMMProjectManager.Infrastructure.Packaging;

//CSJ,class=YmmpxTransferTests,YMMPX受け渡しテスト
internal static class YmmpxTransferTests
{
    internal const string RuleReservationChildArgument = "--r33-ymmpx-reserve-absent-rule";
    private const string ReservationFixture = "reservation-crash";
    private const string ReservationReady = "rule reservation acquired";
    private static readonly TimeSpan ProcessDeadline = TimeSpan.FromSeconds(20);

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
        var rules = new[] { new YmmpxExclusionRule(denied, false, true) };
        YmmpxTransferPlan? plan = null;
        var send = Directory.CreateDirectory(Path.Combine(root, "v2")).FullName;
        var companion = Path.Combine(send, YmmpxTransfer.ManifestName);
        var v2 = Path.Combine(AppContext.BaseDirectory, "Fixtures", "TransferV2");
        var created = new YmmpxPackageResult { Success = true, OutputPath = Path.Combine(send, "作品.ymmpx") };
        await WriteLegacyV2Async(v2, created.OutputPath, companion);
        var standardPath = Path.Combine(root, "standard.ymmpx");

        await Program.RunNamedCaseAsync("R23 transfer preview resolves only explicitly permitted roots", async () =>
        {
            plan = await service.InspectTransferAsync(original, default, exclusions: rules);
            Check(plan.Assets.Count == 6, "Candidate count");
            Check(plan.Assets.Single(asset => asset.SourcePath == left).Sha256 ==
                plan.Assets.Single(asset => asset.SourcePath == alias).Sha256, "Same contents under different names");
            Check(plan.Assets.Single(asset => asset.SourcePath == left).Sha256 !=
                plan.Assets.Single(asset => asset.SourcePath == right).Sha256, "Different contents under same name");
            Check(plan.Assets.Single(asset => asset.SourcePath == external).Disposition == YmmpxMaterialDisposition.Unavailable &&
                plan.Assets.Single(asset => asset.SourcePath.StartsWith(@"\\")).Disposition == YmmpxMaterialDisposition.Unavailable, "Unapproved path was inspected");
            var explicitlyAllowed = await service.InspectTransferAsync(original, default, [outside]);
            Check(explicitlyAllowed.Assets.Single(asset => asset.SourcePath == external).Disposition == YmmpxMaterialDisposition.Included, "Explicit outside root");
            var wrongCase = await service.InspectTransferAsync(original, default, [outside.ToUpperInvariant()]);
            Check(wrongCase.Assets.Single(asset => asset.SourcePath == external).Disposition == YmmpxMaterialDisposition.Unavailable, "Case-variant root was implicitly authorized");
        });

        await Program.RunNamedCaseAsync("R32 imports independent Packager global and local exclusion rules", async () =>
        {
            var packager = Path.Combine(root, "exclude.json");
            var packagerLocal = Path.Combine(input, "作品.exclude.json");
            await File.WriteAllTextAsync(packager, """
                [{"Path":"left","IsFolder":true,"IsExcluded":true,"KindText":"フォルダー"},
                 {"Path":"right","IsFolder":true,"IsExcluded":true,"KindText":"フォルダー"}]
                """);
            await File.WriteAllTextAsync(packagerLocal, """
                [{"FilePath":"RIGHT/","IsFolder":true,"IsExcluded":false},
                 {"FilePath":"別名.png"}]
                """);
            var packagerBytes = await File.ReadAllBytesAsync(packager);
            var globalName = Path.Combine(root, YmmpxExclusionRules.GlobalFileName);
            var localName = Path.Combine(input, YmmpxExclusionRules.GetLocalName(original));
            var global = await YmmpxExclusionRules.LoadAsync(packager, default);
            var local = await YmmpxExclusionRules.LoadAsync(packagerLocal, default);
            var emptyGlobal = await YmmpxExclusionRules.LoadAsync(globalName, default, missingIsEmpty: true);
            var emptyLocal = await YmmpxExclusionRules.LoadAsync(localName, default, missingIsEmpty: true);
            await YmmpxExclusionRules.ImportAsync(globalName, emptyGlobal, global.Rules, default);
            await YmmpxExclusionRules.ImportAsync(localName, emptyLocal, local.Rules, default);
            var approvedGlobal = await YmmpxExclusionRules.LoadAsync(globalName, default);
            var approvedLocal = await YmmpxExclusionRules.LoadAsync(localName, default);
            var effective = YmmpxExclusionRules.Merge(approvedGlobal.Rules, approvedLocal.Rules);
            Check(YmmpxExclusionRules.IsExcluded(new Uri(left).AbsoluteUri, input, effective) &&
                !YmmpxExclusionRules.IsExcluded(right, input, effective) &&
                !YmmpxExclusionRules.IsExcluded("leftover\\child.wav", input, effective) &&
                YmmpxExclusionRules.IsExcluded(alias, input, effective), "Packager matching and local overrides");
            var preview = await service.InspectTransferAsync(original, default, exclusions: effective);
            Check(preview.Assets.Single(item => item.SourcePath == left).Disposition == YmmpxMaterialDisposition.Excluded &&
                preview.Assets.Single(item => item.SourcePath == right).Disposition == YmmpxMaterialDisposition.Included,
                "Exclusions were not applied to actual packaging inputs");
            var bytesBeforeMerge = await File.ReadAllBytesAsync(globalName);
            await YmmpxExclusionRules.ImportAsync(globalName, approvedGlobal,
                YmmpxExclusionRules.Merge(approvedGlobal.Rules, approvedLocal.Rules), default);
            Check((await File.ReadAllBytesAsync(packager)).SequenceEqual(packagerBytes), "Packager file was changed");
            var kept = Directory.GetFiles(root, YmmpxExclusionRules.GlobalFileName + ".previous-*").Single();
            Check((await File.ReadAllBytesAsync(kept)).SequenceEqual(bytesBeforeMerge), "Old PM rules were lost");
            await ExpectAsync<IOException>(() => YmmpxExclusionRules.ImportAsync(globalName, approvedGlobal, [], default));
        });

        await Program.RunNamedCaseAsync("R32 corrupt and interrupted exclusion settings fail closed", async () =>
        {
            var target = Path.Combine(root, "invalid-exclusions.json");
            foreach (var invalid in new[] { "{", "null", "[{}]", """[{"Path":"a","IsExcluded":"false"}]""",
                         """[{"Path":"a","Path":"b","IsExcluded":true}]""",
                         """[{"Path":"https://example.invalid/asset"}]""" })
            {
                await File.WriteAllTextAsync(target, invalid);
                await ExpectAsync<InvalidDataException>(() => YmmpxExclusionRules.LoadAsync(target, default, missingIsEmpty: true));
            }
            File.Move(target, target + ".previous-interrupted");
            await ExpectAsync<InvalidDataException>(() => YmmpxExclusionRules.LoadAsync(target, default, missingIsEmpty: true));
            Check(File.Exists(target + ".previous-interrupted"), "Interrupted record destroyed");
        });

        await Program.RunNamedCaseAsync("R33 absent rules remain protected until publication finishes", async () =>
        {
            var globalName = Path.Combine(root, "reserved-global.json");
            var localName = Path.Combine(input, "reserved-local.json");
            var global = await YmmpxExclusionRules.LoadAsync(globalName, default, missingIsEmpty: true);
            var local = await YmmpxExclusionRules.LoadAsync(localName, default, missingIsEmpty: true);
            var incoming = Path.Combine(input, "incoming-exclusions.json");
            await File.WriteAllTextAsync(incoming, """[{"FilePath":"frame01.png"}]""");
            using (await YmmpxExclusionRules.PinReviewedAsync([(globalName, global), (localName, local)], default))
            {
                await ExpectRuleClaimBlockedAsync(() => File.WriteAllTextAsync(globalName, "[]"));
                await ExpectRuleClaimBlockedAsync(() => File.Move(incoming, localName));
                await ExpectRuleClaimBlockedAsync(() => YmmpxExclusionRules.ImportAsync(localName, local, [], default));
                Check(File.Exists(incoming), "Competing rename did not preserve the incoming rules.");
            }

            Check((await YmmpxExclusionRules.LoadAsync(globalName, default, missingIsEmpty: true)).Identity is null,
                "Global rule reservation remained after disposal.");
            await YmmpxExclusionRules.ImportAsync(localName, local, [new("frame01.png", false, true)], default);

            await ExpectAsync<InvalidDataException>(() =>
                YmmpxExclusionRules.PinReviewedAsync([(globalName, global), (localName, local)], default));
            Check((await YmmpxExclusionRules.LoadAsync(globalName, default, missingIsEmpty: true)).Identity is null &&
                (await YmmpxExclusionRules.LoadAsync(localName, default)).Rules.Single().Path == "frame01.png",
                "A rival won the destination but its settings or the first reservation were changed.");
        });

        await Program.RunNamedCaseAsync("R33 a recovery file appearing after review blocks an absent-rule publication", async () =>
        {
            var target = Path.Combine(root, "recovered-rules.json");
            var reviewed = await YmmpxExclusionRules.LoadAsync(target, default, missingIsEmpty: true);
            var previous = target + ".previous-interrupted";
            const string previousBytes = """[{"FilePath":"frame01.png"}]""";
            await File.WriteAllTextAsync(previous, previousBytes);

            await ExpectAsync<InvalidDataException>(() => YmmpxExclusionRules.PinReviewedAsync([(target, reviewed)], default));
            Check(await File.ReadAllTextAsync(previous) == previousBytes && !File.Exists(target),
                "Publication modified interrupted recovery or left a reservation behind.");
        });

        await Program.RunNamedCaseAsync("R33 reservation is released after abrupt helper termination", async () =>
        {
            var fixture = Directory.CreateDirectory(Path.Combine(root, ReservationFixture)).FullName;
            var target = Path.Combine(fixture, YmmpxExclusionRules.GlobalFileName);
            var reviewed = await YmmpxExclusionRules.LoadAsync(target, default, missingIsEmpty: true);
            var start = new ProcessStartInfo(Environment.ProcessPath!)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
            };
            if (string.Equals(Path.GetFileNameWithoutExtension(start.FileName), "dotnet", StringComparison.OrdinalIgnoreCase))
                start.ArgumentList.Add(typeof(YmmpxTransferTests).Assembly.Location);
            start.ArgumentList.Add(RuleReservationChildArgument);
            start.ArgumentList.Add(fixture);
            using var child = Process.Start(start) ?? throw new InvalidOperationException("Rule fixture could not start.");
            try
            {
                var ready = await child.StandardOutput.ReadLineAsync().WaitAsync(ProcessDeadline);
                Check(ready == ReservationReady, "The child did not acquire the exclusive reservation.");
                await ExpectRuleClaimBlockedAsync(() => YmmpxExclusionRules.ImportAsync(target, reviewed, [], default));
            }
            finally
            {
                if (!child.HasExited) child.Kill();
                await child.WaitForExitAsync().WaitAsync(ProcessDeadline);
            }
            Check(child.ExitCode != 0, "The child exited without demonstrating abrupt termination.");

            // Windows may signal child exit before all owned handles have detached.
            using var deadline = new CancellationTokenSource(ProcessDeadline);
            while (true)
            {
                try
                {
                    await YmmpxExclusionRules.ImportAsync(target, reviewed, [new("frame01.png", false, true)], deadline.Token);
                    break;
                }
                catch (IOException ex) when ((ex.HResult & 0xffff) == 32 || ex.InnerException is System.ComponentModel.Win32Exception { NativeErrorCode: 32 })
                {
                    await Task.Delay(TimeSpan.FromMilliseconds(20), deadline.Token);
                }
            }
            Check((await YmmpxExclusionRules.LoadAsync(target, default)).Rules.Single().Path == "frame01.png",
                "The canonical name could not be used after termination.");
        });

        await Program.RunNamedCaseAsync("R32 choice to load folder-wide and file-level rules is independent", async () =>
        {
            var globalRules = new YmmpxExclusionRule[] { new("left", true, true), new("right", true, true) };
            var localRules = new YmmpxExclusionRule[] { new("left/場面.png", false, false), new("right", true, false) };
            var effective = YmmpxExclusionRules.Merge(globalRules, localRules);
            Check(YmmpxExclusionRules.IsExcluded(left, input, effective) &&
                !YmmpxExclusionRules.IsExcluded(right, input, effective),
                "A file-level unexclude incorrectly overrode a different folder rule");
            var independent = Path.Combine(input, "identity.ymmp");
            File.Copy(original, independent);
            var approved = await service.InspectTransferAsync(independent, default, exclusions: effective);
            var changed = independent + ".alternate";
            File.Copy(independent, changed);
            File.Move(changed, independent, overwrite: true);
            if (service.TryEnsureAvailable(out _))
                await ExpectAsync<InvalidDataException>(() =>
                    service.CreateStandardPackageAsync(approved, Path.Combine(root, "stale-project.ymmpx"), true, true, default));
        });

        // Captured from the R25 tested writer (398d09a), with synthetic materials only.
        var golden = Directory.CreateDirectory(Path.Combine(root, "v1")).FullName;
        var fixture = Path.Combine(AppContext.BaseDirectory, "Fixtures", "TransferV1");
        var legacyPackage = Path.Combine(golden, "作品.ymmpx");
        File.Copy(Path.Combine(fixture, "package.fixture"), legacyPackage);
        File.Copy(Path.Combine(fixture, "manifest.json"), Path.Combine(golden, YmmpxTransfer.ManifestName));
        await Program.RunNamedCaseAsync("Transfer v1 fixture retains both same-name originals and exclusions", async () =>
        {
            var read = await service.ExtractPackageAsync(legacyPackage, Path.Combine(root, "v1-read"), default);
            Check(read.Success && read.TransferEvidenceVerified && read.MissingMaterials.Count == 3, "Historical companion");
            var items = JsonNode.Parse(await File.ReadAllTextAsync(read.RestoredProjectPath!))!["Items"]!.AsArray();
            var first = items[0]!["FilePath"]!.GetValue<string>();
            var second = items[1]!["FilePath"]!.GetValue<string>();
            Check(Path.GetFileName(first) == "場面.png" && Path.GetFileName(second) == "場面.png" &&
                File.ReadAllText(first) == "first version" && File.ReadAllText(second) == "second version", "Historical contents");
        });
        var hasLibrary = service.TryEnsureAvailable(out _);
        Task WithLibrary(string name, Func<Task> run)
        {
            if (hasLibrary) return Program.RunNamedCaseAsync(name, run);
            Program.ReportConditionalSkip(name + " (requires YmmpxLib)");
            return Task.CompletedTask;
        }
        if (!hasLibrary)
        {
            var refused = await service.CreateStandardPackageAsync(plan!, standardPath, true, true, default);
            Check(!refused.Success && refused.ErrorMessage == service.MissingLibraryMessage && !File.Exists(standardPath),
                "Missing YmmpxLib must not publish");
        }

        await Program.RunNamedCaseAsync("R32 missing assets need explicit approval", async () =>
        {
            await ExpectAsync<InvalidDataException>(() => service.CreateStandardPackageAsync(plan!, standardPath, true, false, default));
            Check(!File.Exists(standardPath), "Missing assets were implicitly accepted.");
        });

        await WithLibrary("R32 writes a standard-only package with approved names and no overwrite", async () =>
        {
            var output = await service.CreateStandardPackageAsync(plan!, standardPath, true, true, default);
            Check(output.Success && output.PackagedMaterialCount == 3 && output.MissingMaterialCount == 2, output.ErrorMessage ?? "Pack counts");
            using var archive = ZipFile.OpenRead(standardPath);
            Check(archive.Comment == "" && archive.Entries.Count == 6, "Standard library archive with no custom ZIP comment");
            Check(!File.Exists(Path.Combine(root, YmmpxTransfer.ManifestName)), "A custom sidecar was published");
            var before = await File.ReadAllBytesAsync(standardPath);
            await ExpectAsync<IOException>(() => service.CreateStandardPackageAsync(plan!, standardPath, true, true, default));
            Check((await File.ReadAllBytesAsync(standardPath)).SequenceEqual(before), "Existing output was overwritten");
            Check((await File.ReadAllBytesAsync(original)).SequenceEqual(sourceBytes) &&
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
            Check(Path.GetFileName(restored[0]) == "場面.png" && Path.GetFileName(restored[1]) == "場面.png" &&
                Path.GetFileName(restored[2]) == "別名.png" && project[YmmpxTransfer.EvidenceProperty] is null, "Original basenames restored");
            Check(restored[0] != restored[1] && restored[0] != restored[2], "Reference names and paths merged");
            Check(restored.Skip(3).All(path => path.StartsWith(destination + Path.DirectorySeparatorChar, StringComparison.Ordinal) && !File.Exists(path)),
                "Unapproved paths are not opened or retained in reference fields");
            Check(project["FutureData"]!["Keep"]!.GetValue<string>() == "保持" &&
                project["FilePath"]!.GetValue<string>() == "", "Unknown fields and no source project path");
            Check(File.Exists(Path.Combine(destination, YmmpxTransfer.ManifestName)), "Evidence available for inspection");
        });

        await WithLibrary("R32 packages native PSD references, PNG sequences and optional UI state", async () =>
        {
            var source = Path.Combine(input, "layers.ymmp");
            var layers = CreateMaterial(input, "layer.psd", "approved layer");
            var sequence = Directory.CreateDirectory(Path.Combine(input, "sequence")).FullName;
            var first = CreateMaterial(sequence, "shot01.png", "frame one");
            _ = CreateMaterial(sequence, "shot02.png", "frame two");
            var excludedFrame = CreateMaterial(sequence, "shot03.png", "frame three");
            await File.WriteAllTextAsync(source, new JsonObject
            {
                ["LayoutXml"] = "keep layout if selected",
                ["ToolStates"] = new JsonArray("UI state"),
                ["Items"] = new JsonArray(
                    new JsonObject { ["FilePath"] = left, ["EnableLayersFilePath"] = denied },
                    new JsonObject { ["FilePath"] = layers, ["EnableLayers"] = new JsonArray("Layer A") },
                    new JsonObject { ["$type"] = "YukkuriMovieMaker.Project.Items.VideoItem, YukkuriMovieMaker", ["FilePath"] = first }),
            }.ToJsonString());
            var bytes = await File.ReadAllBytesAsync(source);
            var preview = await service.InspectTransferAsync(source, default, exclusions: [.. rules, new(excludedFrame, false, true)]);
            Check(preview.Assets.Count == 6 &&
                preview.Assets.Count(item => item.SequenceMember) == 3 &&
                preview.Assets.Count(item => item.Disposition == YmmpxMaterialDisposition.Excluded) == 2, "Sequence preview");
            var packed = await service.CreateStandardPackageAsync(preview, Path.Combine(root, "sequence.ymmpx"), false, false, default);
            Check(packed.Success && packed.PackagedMaterialCount == 4, packed.ErrorMessage ?? "sequence and layer export");
            using var package = ZipFile.OpenRead(packed.OutputPath!);
            Check(package.Entries.Count(entry => entry.FullName.StartsWith("resources/sequence_", StringComparison.Ordinal)) == 2, "Native sequence entries");
            var imported = await service.ExtractPackageAsync(packed.OutputPath!, Path.Combine(root, "native-format-restored"), default);
            Check(imported.Success && imported.MissingMaterials.Count == 1, imported.ErrorMessage ?? "Standard import");
            var project = JsonNode.Parse(await File.ReadAllTextAsync(imported.RestoredProjectPath!))!.AsObject();
            var items = project["Items"]!.AsArray();
            var filePath = items[1]!["FilePath"]!.GetValue<string>();
            Check(File.Exists(filePath) && items[1]!["EnableLayersFilePath"]!.GetValue<string>() == filePath &&
                !File.Exists(items[0]!["EnableLayersFilePath"]!.GetValue<string>()) &&
                project["LayoutXml"] is null && project["ToolStates"] is null &&
                (await File.ReadAllBytesAsync(source)).SequenceEqual(bytes), "PSD binding, UI toggle, source preservation");
            var withUi = await service.CreateStandardPackageAsync(preview, Path.Combine(root, "sequence-with-ui.ymmpx"), true, false, default);
            Check(withUi.Success, withUi.ErrorMessage ?? "Include UI state");
            var importedWithUi = await service.ExtractPackageAsync(withUi.OutputPath!, Path.Combine(root, "native-format-with-ui"), default);
            Check(importedWithUi.Success, importedWithUi.ErrorMessage ?? "Import with UI state");
            var originalLayout = JsonNode.Parse(await File.ReadAllTextAsync(importedWithUi.RestoredProjectPath!))!["LayoutXml"];
            Check(originalLayout?.GetValue<string>() == "keep layout if selected", "UI flag was ignored");
        });

        await WithLibrary("R32 matches native PNG selection when the representative is missing or excluded", async () =>
        {
            var group = Directory.CreateDirectory(Path.Combine(input, "selection")).FullName;
            var source = Path.Combine(group, "frames.ymmp");
            var first = CreateMaterial(group, "shot01.png", "rep");
            var second = CreateMaterial(group, "shot02.png", "second");
            var third = CreateMaterial(group, "shot03.png", "third");
            await File.WriteAllTextAsync(source, """
                {"Items":[{"$type":"YukkuriMovieMaker.Project.Items.VideoItem, YukkuriMovieMaker","FilePath":"shot01.png"}]}
                """);
            foreach (bool representativeMissing in new[] { false, true })
            {
                var excluded = representativeMissing ? new HashSet<string>() : new HashSet<string> { first, third };
                if (representativeMissing) File.Delete(first);
                var preview = await service.InspectTransferAsync(source, default,
                    exclusions: excluded.Select(path => new YmmpxExclusionRule(path, false, true)).ToArray());
                Check(preview.Assets.Count == 3 && preview.Assets.All(candidate => candidate.SequenceMember), "Full sequence detection");
                var output = Path.Combine(root, $"sequence-edge-{representativeMissing}.ymmpx");
                var result = await service.CreateStandardPackageAsync(preview, output, true, representativeMissing, default);
                Check(result.Success, result.ErrorMessage ?? "Sequence edge-case package");
                var native = Path.Combine(root, $"sequence-native-{representativeMissing}.ymmpx");
                Check(YmmpxLibResolver.TryResolveAssembly(out var assembly, out _, out _), "Native library verification");
                var method = assembly!.GetType("YmmpxLib.YmmpxPackageService")!.GetMethod("CreatePackageAsync")!;
                var task = (Task)method.Invoke(null, [source, native, excluded, null, null, CancellationToken.None])!;
                await task;
                using var pmArchive = ZipFile.OpenRead(output);
                using var libArchive = ZipFile.OpenRead(native);
                var pm = await ReadResources(pmArchive);
                var expected = await ReadResources(libArchive);
                Check(pm.SequenceEqual(expected) && pm.Contains("second") && pm.Contains("third") == representativeMissing,
                    "The PM package differs from the native sequence selection");
                var imported = await service.ExtractPackageAsync(output, Path.Combine(root, $"sequence-edge-restored-{representativeMissing}"), default);
                Check(imported.Success && imported.MissingMaterials.Contains("shot01.png"), "Missing representative not disclosed");
            }
            Check(!Directory.EnumerateDirectories(root, ".ymmpm-package-input-*").Any(), "Sequence staging remained");
            Check(File.Exists(second) && File.Exists(third), "A source frame was removed");

            static async Task<string[]> ReadResources(ZipArchive archive)
            {
                var values = new List<string>();
                foreach (var entry in archive.Entries.Where(entry => entry.FullName.StartsWith("resources/", StringComparison.Ordinal)))
                {
                    using var reader = new StreamReader(entry.Open());
                    values.Add(await reader.ReadToEndAsync());
                }
                return values.Order(StringComparer.Ordinal).ToArray();
            }
        });

        await WithLibrary("R32 round trips a standard source whose basename resembles old PM v2", async () =>
        {
            var name = "ymmpm-asset-00000000-file.png";
            var asset = CreateMaterial(input, name, "ordinary file with a legacy-looking name");
            var source = Path.Combine(input, "historical-name.ymmp");
            await File.WriteAllTextAsync(source, JsonSerializer.Serialize(new { Items = new[] { new { FilePath = asset } } }));
            var preview = await service.InspectTransferAsync(source, default);
            var output = Path.Combine(root, "historical-name.ymmpx");
            var packed = await service.CreateStandardPackageAsync(preview, output, true, false, default);
            Check(packed.Success, packed.ErrorMessage ?? "Standard creation");
            var restored = await service.ExtractPackageAsync(output, Path.Combine(root, "historical-name"), default);
            Check(restored.Success && !restored.TransferEvidenceVerified, restored.ErrorMessage ?? "Classified as a legacy transfer");
            var project = JsonNode.Parse(await File.ReadAllTextAsync(restored.RestoredProjectPath!));
            var relative = project!["Items"]![0]!["FilePath"]!.GetValue<string>();
            Check(await File.ReadAllTextAsync(relative) == await File.ReadAllTextAsync(asset) &&
                Path.GetFileName(asset) == name, "Filename adaptation changed source content");
        });

        await WithLibrary("R32 checks the exclusion version at publication and pins an existing configuration", async () =>
        {
            var target = Path.Combine(root, "publication-exclusions.json");
            var reviewed = await YmmpxExclusionRules.LoadAsync(target, default, missingIsEmpty: true);
            var output = Path.Combine(root, "stale-exclusions-output.ymmpx");
            bool fileAppeared = false;
            await ExpectAsync<InvalidDataException>(() => service.CreateStandardPackageAsync(plan!, output, true, true, default,
                new InlineProgress(() =>
                {
                    if (fileAppeared) return;
                    File.WriteAllText(target, "[]");
                    fileAppeared = true;
                }), token => YmmpxExclusionRules.PinReviewedAsync([(target, reviewed)], token)));
            Check(fileAppeared && !File.Exists(output), "Changed configuration allowed publication");
            reviewed = await YmmpxExclusionRules.LoadAsync(target, default);
            using (await YmmpxExclusionRules.PinReviewedAsync([(target, reviewed)], default))
                await ExpectAsync<IOException>(() => File.WriteAllTextAsync(target, "overwritten"));
            Check(await File.ReadAllTextAsync(target) == "[]", "Guard altered configuration");

            var vacant = Path.Combine(input, "publication-reservation.json");
            var empty = await YmmpxExclusionRules.LoadAsync(vacant, default, missingIsEmpty: true);
            var guardEntered = false;
            var published = await service.CreateStandardPackageAsync(plan!, output, true, true, default,
                pinRules: async token =>
                {
                    var lease = await YmmpxExclusionRules.PinReviewedAsync([(vacant, empty)], token);
                    try
                    {
                        await ExpectRuleClaimBlockedAsync(() => File.WriteAllTextAsync(vacant, "[]"));
                        guardEntered = true;
                        return lease;
                    }
                    catch
                    {
                        lease.Dispose();
                        throw;
                    }
                });
            Check(guardEntered && published.Success && File.Exists(output) &&
                (await YmmpxExclusionRules.LoadAsync(vacant, default, missingIsEmpty: true)).Identity is null,
                "Publication did not release the absence reservation.");

            using var cancelledAtPublication = new CancellationTokenSource();
            var cancelledOutput = Path.Combine(root, "cancelled-after-reservation.ymmpx");
            await ExpectAsync<OperationCanceledException>(() =>
                service.CreateStandardPackageAsync(plan!, cancelledOutput, true, true, cancelledAtPublication.Token,
                    pinRules: async token =>
                    {
                        var lease = await YmmpxExclusionRules.PinReviewedAsync([(vacant, empty)], token);
                        cancelledAtPublication.Cancel();
                        return lease;
                    }));
            Check(!File.Exists(cancelledOutput) &&
                (await YmmpxExclusionRules.LoadAsync(vacant, default, missingIsEmpty: true)).Identity is null &&
                !Directory.EnumerateDirectories(root, ".ymmpm-package-input-*").Any(),
                "Cancelled publication left its output, staging data or rule reservation.");

            var occupied = Path.Combine(root, "occupied-after-reservation.ymmpx");
            await ExpectAsync<IOException>(() => service.CreateStandardPackageAsync(plan!, occupied, true, true, default,
                pinRules: async token =>
                {
                    var lease = await YmmpxExclusionRules.PinReviewedAsync([(vacant, empty)], token);
                    try
                    {
                        await File.WriteAllTextAsync(occupied, "separate output", token);
                        return lease;
                    }
                    catch
                    {
                        lease.Dispose();
                        throw;
                    }
                }));
            Check(await File.ReadAllTextAsync(occupied) == "separate output" &&
                (await YmmpxExclusionRules.LoadAsync(vacant, default, missingIsEmpty: true)).Identity is null &&
                !Directory.EnumerateDirectories(root, ".ymmpm-package-input-*").Any(),
                "A late output collision changed the rival output or left a reservation.");

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
            {
                if (manifest.Version == 1)
                    archive.Comment = YmmpxTransfer.MakeArchiveComment(manifest.PackageName, manifest.Assets, manifest.Missing);
                else
                {
                    var entry = archive.GetEntry("作品.ymmp")!;
                    JsonNode doc;
                    using (var input = entry.Open()) doc = JsonNode.Parse(input)!;
                    doc[YmmpxTransfer.EvidenceProperty]!["ManifestDigest"] =
                        YmmpxTransfer.ManifestDigest(manifest.PackageName, manifest.Assets, manifest.Missing);
                    entry.Delete();
                    WriteEntry(archive, "作品.ymmp", doc.ToJsonString());
                }
            }
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
                WriteEntry(archive, "legacy.ymmp", """{"FutureData":1,"Items":[{"FilePath":"resources/ymmpm-asset-logo.png"}]}""");
                WriteEntry(archive, "resources/ymmpm-asset-logo.png", "ordinary asset");
                WriteEntry(archive, "_ymmpx_project_path.txt", "legacy.ymmp");
                WriteEntry(archive, "links.json", """{"resources/ymmpm-asset-logo.png":"resources/ymmpm-asset-logo.png"}""");
            }
            var imported = await service.ExtractPackageAsync(plain, Path.Combine(root, "legacy"), default);
            Check(imported.Success && !imported.TransferEvidenceVerified, "Unrelated sidecar obstructed legacy archive");
            var stripped = Path.Combine(root, "stripped.ymmpx");
            File.Copy(created!.OutputPath!, stripped);
            //CSJ,variable=archive,アーカイブ
            using (var archive = ZipFile.Open(stripped, ZipArchiveMode.Update))
            {
                archive.Comment = "";

                {
                    var entry = archive.GetEntry("作品.ymmp")!;
                    JsonObject doc;
                    using (var stream = entry.Open()) doc = JsonNode.Parse(stream)!.AsObject();
                    Check(doc.Remove(YmmpxTransfer.EvidenceProperty), "Expected transfer marker");
                    entry.Delete();
                    WriteEntry(archive, "作品.ymmp", doc.ToJsonString());
                }
            }
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

        await WithLibrary("R23 transfer rejects approved asset replacement and leaves no partial public output", async () =>
        {
            //CSJ,variable=oldContent,旧内容
            var oldContent = await File.ReadAllBytesAsync(left);
            //CSJ,variable=destination,格納先
            var destination = Path.Combine(root, "rejected.ymmpx");
            try
            {
                // Same byte length isolates the SHA-256 check.
                await File.WriteAllTextAsync(left, "third version");
                await ExpectAsync<InvalidDataException>(() => service.CreateStandardPackageAsync(plan!, destination, true, true, default));
                Check(!File.Exists(destination) && !Directory.EnumerateDirectories(root, ".ymmpm-package-input-*").Any(), "Failed staging cleanup");
            }
            finally { await File.WriteAllBytesAsync(left, oldContent); }
        });

        await WithLibrary("R23 transfer cancellation and late target creation preserve output and permit retry", async () =>
        {
            //CSJ,variable=retry,再試行
            var retry = Path.Combine(root, "retry.ymmpx");
            //CSJ,variable=cancelled,キャンセル済み
            using var cancelled = new CancellationTokenSource();
            await ExpectAsync<OperationCanceledException>(() => service.CreateStandardPackageAsync(plan!, retry, true, true, cancelled.Token,
                new InlineProgress(cancelled.Cancel)));
            Check(!File.Exists(retry) && !Directory.EnumerateDirectories(root, ".ymmpm-package-input-*").Any(), "Cancelled staging cleanup");
            //CSJ,variable=collision,衝突
            var collision = Path.Combine(root, "collision.ymmpx");
            var sentinel = Path.Combine(collision, "sentinel");
            var setOnce = false;
            await ExpectAsync<IOException>(() => service.CreateStandardPackageAsync(plan!, collision, true, true, default,
                new InlineProgress(() =>
                {
                    if (setOnce) return;
                    setOnce = true;
                    Directory.CreateDirectory(collision);
                    File.WriteAllText(sentinel, "external");
                })));
            Check(await File.ReadAllTextAsync(sentinel) == "external" &&
                Directory.GetFiles(collision).Length == 1 &&
                !Directory.EnumerateDirectories(root, ".ymmpm-package-input-*").Any(), "Existing target or staging changed");
            Check((await service.CreateStandardPackageAsync(plan!, retry, true, true, default)).Success, "Safe retry");
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
            Check(YmmpxLibResolver.TryResolveAssembly(out var assembly, out _, out _), "Native library verification");
            //CSJ,variable=method,メソッド
            var method = assembly!.GetType("YmmpxLib.YmmpxPackageService")!.GetMethod("ExtractAndRestoreProject")!;
            //CSJ,variable=native,ネイティブ
            var native = Path.Combine(root, "native-restore");
            var extracted = method.Invoke(null, [standardPath, native]) ?? throw new InvalidOperationException("Real extract failed");
            //CSJ,variable=restored,復元済み
            var restored = extracted.GetType().GetProperty("ProjectFilePath")!.GetValue(extracted) as string;
            Check(restored is not null && File.Exists(restored), "Real extract project");
            //CSJ,variable=document,文書
            var document = JsonNode.Parse(await File.ReadAllTextAsync(restored!))!;
            //CSJ,variable=references,参照一覧
            var references = document["Items"]!.AsArray().Select(item => item!["FilePath"]!.GetValue<string>()).ToArray();
            Check(references[0] != references[1] &&
                await File.ReadAllTextAsync(references[0]) == "first version" &&
                await File.ReadAllTextAsync(references[1]) == "second version", "Real extraction of namespaced resources");
            Check(references[3].StartsWith(YmmpxStandardPackage.UnavailablePrefix, StringComparison.Ordinal), "Real extraction preserves relative unresolved placeholder");
        });

        static JsonObject Reference(string path) => new() { ["FilePath"] = path };
    }

    private static async Task WriteLegacyV2Async(string fixture, string package, string companion)
    {
        // The project and manifest digest come from the frozen R30 synthetic output. The test
        // stores no archive and does not need the retired writer or YmmpxLib to build it.
        var manifest = JsonNode.Parse(await File.ReadAllTextAsync(Path.Combine(fixture, "manifest.json")))!;
        var members = manifest["assets"]!.AsArray().Select(asset => asset!["archivePath"]!.GetValue<string>()).ToArray();
        using (var zip = ZipFile.Open(package, ZipArchiveMode.Create))
        {
            WriteEntry(zip, "作品.ymmp", await File.ReadAllTextAsync(Path.Combine(fixture, "project.json")));
            WriteEntry(zip, "_ymmpx_project_path.txt", "作品.ymmp");
            WriteEntry(zip, "links.json", JsonSerializer.Serialize(members.ToDictionary(path => path)));
            WriteEntry(zip, members[0], "first version");
            WriteEntry(zip, members[1], "second version");
            WriteEntry(zip, members[2], "first version");
        }
        manifest["packageSha256"] = Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(package)));
        await File.WriteAllTextAsync(companion, manifest.ToJsonString());
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

    internal static async Task HoldRuleReservationAsync(string fixture)
    {
        var testBase = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "YMMProjectManager-tests"));
        var full = Path.GetFullPath(fixture);
        var relative = Path.GetRelativePath(testBase, full).Split(Path.DirectorySeparatorChar);
        if (!string.Equals(fixture, full, StringComparison.Ordinal) || relative.Length != 3 ||
            !Guid.TryParseExact(relative[0], "N", out _) || relative[1] != "r23-transfer" ||
            relative[2] != ReservationFixture || !full.StartsWith(testBase + Path.DirectorySeparatorChar, StringComparison.Ordinal))
            throw new InvalidOperationException("Rule reservation fixture is outside the isolated test directory.");

        var target = Path.Combine(full, YmmpxExclusionRules.GlobalFileName);
        var reviewed = await YmmpxExclusionRules.LoadAsync(target, default, missingIsEmpty: true);
        if (reviewed.Identity is not null) throw new InvalidOperationException("Expected an absent rule fixture.");
        using var lease = await YmmpxExclusionRules.PinReviewedAsync([(target, reviewed)], default);
        Console.WriteLine(ReservationReady);
        Console.ReadLine();
    }

    private static Task ExpectRuleClaimBlockedAsync(Action attempt) =>
        ExpectRuleClaimBlockedAsync(() =>
        {
            attempt();
            return Task.CompletedTask;
        });

    private static async Task ExpectRuleClaimBlockedAsync(Func<Task> attempt)
    {
        try { await attempt(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return; }
        throw new InvalidOperationException("A second operation wrote to a reserved rule file name.");
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
