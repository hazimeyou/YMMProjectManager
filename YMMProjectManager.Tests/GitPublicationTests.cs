using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using YMMProjectManager.Infrastructure.Git;

//CSJ,class=GitPublicationTests,Git確定反映テスト
internal static class GitPublicationTests
{
    //CSJ,method=RunAsync,実行する（非同期）
    internal static async Task RunAsync(string root)
    {
        await Program.RunNamedCaseAsync("R21 Push accepts checked plain history and ignores pre-push hook", () =>
            TestAsync(root, "plain", async (source, _) =>
            {
                //CSJ,variable=marker,マーカー
                var marker = Path.Combine(source, "local-hook-ran");
                await File.WriteAllTextAsync(Path.Combine(source, ".git", "hooks", "pre-push"),
                    "#!/bin/sh\ntouch local-hook-ran\nexit 1\n");
                await CommitFileAsync(source, "README.txt", Encoding.UTF8.GetBytes("Japanese 日本語\n"));
                return (true, marker);
            }));
        await Program.RunNamedCaseAsync("R21 Push refuses bytes in deleted or renamed history", () =>
            TestAsync(root, "binary", async (source, _) =>
            {
                await CommitFileAsync(source, "unmarked.txt", [0x89, (byte)'P', (byte)'N', (byte)'G', 0, 5]);
                await CommandAsync(source, "mv", "unmarked.txt", "renamed.txt");
                await CommandAsync(source, "commit", "-m", "rename");
                await CommandAsync(source, "rm", "renamed.txt");
                await CommandAsync(source, "commit", "-m", "remove");
                return (false, null);
            }));
        await Program.RunNamedCaseAsync("R21 Push refuses PF-referenced plain text media", () =>
            TestAsync(root, "pf", async (source, _) =>
            {
                await CommitFileAsync(source, "scene.ymmp", """{"Timelines":[{"Items":[{"File\u0050ath":"private-captions.txt"}]}]}"""u8.ToArray());
                await CommitFileAsync(source, "private-captions.txt", "a caption is still an asset"u8.ToArray());
                return (false, null);
            }));
        await Program.RunNamedCaseAsync("R21 Push refuses renamed registered asset by full SHA-256", () =>
            TestAsync(root, "hash", async (source, git) =>
            {
                //CSJ,variable=bytes,バイト列
                var bytes = "known asset registered outside Git"u8.ToArray();
                git.SetPublicationAssets(["voice.txt"], [Convert.ToHexString(SHA256.HashData(bytes))]);
                await CommitFileAsync(source, "newname.txt", bytes);
                return (false, null);
            }));
        await Program.RunNamedCaseAsync("R21 Push refuses packaged and metadata-identified payloads", async () =>
        {
            await TestAsync(root, "package", async (source, _) =>
            {
                await CommitFileAsync(source, "material.ymmpx", "synthetic package marker"u8.ToArray());
                return (false, null);
            });
            await TestAsync(root, "manifest", async (source, _) =>
            {
                //CSJ,variable=asset,素材
                var asset = "a pool asset with a different checkout name"u8.ToArray();
                var sha = Convert.ToHexString(SHA256.HashData(asset));
                await CommitFileAsync(source, "asset-pool.json",
                    Encoding.UTF8.GetBytes($$"""{"SchemaVersion":1,"OriginalFileName":"one.txt","DistributionFileName":"one_hash.txt","Sha256":"{{sha}}"}"""));
                await CommitFileAsync(source, "copy.txt", asset);
                return (false, null);
            });
        });
        await Program.RunNamedCaseAsync("R21 asset registry persists across repository objects and blocks Commit and Push", () =>
            TestAsync(root, "registry", async (source, git) =>
            {
                //CSJ,variable=bytes,バイト列
                var bytes = "known UTF-8 material stored without its pool manifest"u8.ToArray();
                var actor = new GitRepository(source);
                await actor.RememberPublicationAssetAsync("captions.txt", Convert.ToHexString(SHA256.HashData(bytes)), CancellationToken.None);
                await File.WriteAllBytesAsync(Path.Combine(source, "other-name.txt"), bytes);
                await CommandAsync(source, "add", "--", "other-name.txt");
                //CSJ,variable=preview,プレビュー
                var preview = (await git.InspectCommitAsync()).CommitTarget!;
                var refused = await git.CommitAsync(preview, "should be blocked");
                if (refused.Failure != GitFailure.MediaPublication ||
                    File.Exists(Path.Combine(source, ".git", "refs", "heads", "main")))
                    throw new InvalidOperationException("Commit accepted the protected data or changed the branch.");
                await CommandAsync(source, "commit", "-m", "synthetic legacy input");
                return (false, null);
            }));
        await Program.RunNamedCaseAsync("R21 Push refuses pool payload even when its pool record was never committed", () =>
            TestAsync(root, "pool", async (source, _) =>
            {
                //CSJ,variable=folder,フォルダー
                var folder = "asset-" + new string('A', 16) + "-" + new string('B', 64);
                Directory.CreateDirectory(Path.Combine(source, folder));
                await CommitFileAsync(source, folder + "/sample_1234567890123456.txt", "ordinary-looking media"u8.ToArray());
                return (false, null);
            }));
        await Program.RunNamedCaseAsync("R21 Stage rejects a registered asset before writing the Git object", async () =>
        {
            //CSJ,variable=rootPath,ルートパス
            var rootPath = Directory.CreateDirectory(Path.Combine(root, "pub-no-stage")).FullName;
            await CommandAsync(rootPath, "init", "-b", "main");
            //CSJ,variable=bytes,バイト列
            var bytes = "registered material\r\nsecond line\r\n"u8.ToArray();
            //CSJ,variable=worktree,作業ツリー
            var worktree = Path.Combine(rootPath, "generic.txt");
            await File.WriteAllBytesAsync(worktree, Encoding.UTF8.GetBytes("registered material\nsecond line\n"));
            var git = new GitRepository(rootPath);
            await git.RememberPublicationAssetAsync("disclosed-original.txt", Convert.ToHexString(SHA256.HashData(bytes)), default);
            var objects = Path.Combine(rootPath, ".git", "objects");
            var before = Directory.GetFiles(objects, "*", SearchOption.AllDirectories);
            var refused = await new GitRepository(rootPath).StageAsync(["generic.txt"]);
            if (refused.Failure != GitFailure.MediaPublication || File.Exists(Path.Combine(rootPath, ".git", "index")) ||
                !before.SequenceEqual(Directory.GetFiles(objects, "*", SearchOption.AllDirectories)))
                throw new InvalidOperationException("Stage wrote a registered media blob or published the index.");
        });
        await Program.RunNamedCaseAsync("R22 Stage rejects local relink and derived metadata artifacts", () =>
            TestAsync(root, "r22-private-path", async (source, git) =>
            {
                //CSJ,variable=operation,操作
                var operation = Guid.NewGuid().ToString("N");
                //CSJ,variable=derived,派生
                var derived = ".ymmpm-derived-" + operation;
                //CSJ,variable=pendingDerived,保留派生
                var pendingDerived = ".ymmpm-derived-pending-" + operation;
                Directory.CreateDirectory(Path.Combine(source, derived));
                Directory.CreateDirectory(Path.Combine(source, pendingDerived));
                //CSJ,variable=pendingOutput,保留出力
                var pendingOutput = $".ymmpm-relink-external-{operation}.json.output.tmp";
                //CSJ,variable=path,パス
                foreach (var path in new[] { $".ymmpm-relink-{operation}.json", pendingOutput, $"{derived}/revision-synthetic.json",
                    $"{pendingDerived}/revision-synthetic.json" })
                {
                    await File.WriteAllTextAsync(Path.Combine(source, path), path == pendingOutput
                        ? """{"Items":[{"FilePath":"C:\\synthetic\\private\\clip.wav"}]}""" : "synthesized local-only metadata");
                    var refusal = await git.StageAsync([path]);
                    //CSJ,variable=publishedIndex,確定済みインデックス
                    var publishedIndex = File.Exists(Path.Combine(source, ".git", "index"));
                    if (refusal.Failure != GitFailure.MediaPublication || publishedIndex)
                        throw new InvalidOperationException($"Private path outcome ({path}): {refusal.Failure}; index={publishedIndex}; {refusal.StandardError}");
                }

                Directory.CreateDirectory(Path.Combine(source, "docs"));
                await File.WriteAllTextAsync(Path.Combine(source, "docs", "readme.txt"), "portable repository content");
                if (!(await git.StageAsync(["docs/readme.txt"])).Success ||
                    !(await git.CommitAsync("ordinary nested file")).Success)
                    throw new InvalidOperationException("An ordinary nested path could not be staged and committed.");
                return (true, null);
            }));
        await Program.RunNamedCaseAsync("R22 Commit and Push refuse renamed private recovery metadata", () =>
            TestAsync(root, "r22-private-history", async (source, git) =>
            {
                //CSJ,variable=receipt,照合記録
                const string receipt = """{"Version":6,"OperationId":"d2edaef4-61bc-47bd-9cf6-6077611a1407","OutputSha256":"fixture","SourceSha256":"fixture","Approved":[]}""";
                await File.WriteAllTextAsync(Path.Combine(source, "document.json"), receipt);
                await CommandAsync(source, "add", "--", "document.json");
                //CSJ,variable=approved,承認済み
                var approved = (await git.InspectCommitAsync()).CommitTarget!;
                var refused = await git.CommitAsync(approved, "prevent publishing user-private evidence");
                if (refused.Failure != GitFailure.MediaPublication ||
                    File.Exists(Path.Combine(source, ".git", "refs", "heads", "main")))
                    throw new InvalidOperationException("Commit published private recovery metadata.");

                // Simulate a historical commit made by another tool, then removed from the tip.
                await CommandAsync(source, "commit", "-m", "synthetic old receipt");
                await CommandAsync(source, "rm", "--", "document.json");
                await CommandAsync(source, "commit", "-m", "synthetic removal");
                return (false, null);
            }));
        await Program.RunNamedCaseAsync("R22 Push permits portable metadata and rejects local pool references", async () =>
        {
            //CSJ,variable=hash,ハッシュ
            var hash = new string('C', 64);
            await TestAsync(root, "r22-portable-pool", async (source, git) =>
            {
                //CSJ,variable=pool,プール
                var pool = $$"""{"SchemaVersion":1,"OriginalFileName":"clip.txt","DistributionFileName":"clip_HASH.txt","Sha256":"{{hash}}","OriginalReference":"media/clip.txt","RedistributionNote":"Fixture approval"}""";
                await File.WriteAllTextAsync(Path.Combine(source, "asset-pool.json"), pool);
                if (!(await git.StageAsync(["asset-pool.json"])).Success || !(await git.CommitAsync("portable asset record only")).Success)
                    throw new InvalidOperationException("Portable asset metadata could not be published.");
                return (true, null);
            });
            await TestAsync(root, "r22-private-pool", async (source, git) =>
            {
                //CSJ,variable=pool,プール
                var pool = $$"""{"SchemaVersion":1,"OriginalFileName":"clip.txt","DistributionFileName":"clip_HASH.txt","Sha256":"{{hash}}","OriginalReference":"C:\\synthetic\\local\\clip.txt"}""";
                await File.WriteAllTextAsync(Path.Combine(source, "private-pool.json"), pool);
                if ((await git.StageAsync(["private-pool.json"])).Failure != GitFailure.MediaPublication)
                    throw new InvalidOperationException("Stage accepted a non-portable path in pool metadata.");
                await CommitFileAsync(source, "private-pool.json", Encoding.UTF8.GetBytes(pool));
                return (false, null);
            });
        });
        await Program.RunNamedCaseAsync("R23 Stage refuses local transfer paths before writing a Git object", () =>
            TestAsync(root, "r23-transfer-paths", async (source, git) =>
            {
                //CSJ,variable=operation,操作
                var operation = Guid.NewGuid().ToString("N");
                //CSJ,variable=blockedPaths,停止パス一覧
                var blockedPaths = new[] { "ymmpm-transfer.json", $".ymmpm-transfer-{operation}/new.ymmp",
                    $".ymmpm-package-{operation}/copy.ymmp", $".ymmpx-{operation}/copy.ymmp",
                    "resources/ymmpm-transfer/00000001/clip.txt", "resources/ymmpm-asset-00000001-file.txt",
                    "ymmpm-missing/00000001/clip.txt" };
                var objects = Path.Combine(source, ".git", "objects");
                var before = Directory.GetFiles(objects, "*", SearchOption.AllDirectories);
                //CSJ,variable=path,パス
                foreach (var path in blockedPaths)
                {
                    //CSJ,variable=file,ファイル
                    var file = Path.Combine(source, path);
                    Directory.CreateDirectory(Path.GetDirectoryName(file)!);
                    await File.WriteAllTextAsync(file, "plain text synthetic fixture");
                    var refused = await git.StageAsync([path]);
                    if (refused.Failure != GitFailure.MediaPublication || File.Exists(Path.Combine(source, ".git", "index")) ||
                        !before.SequenceEqual(Directory.GetFiles(objects, "*", SearchOption.AllDirectories)))
                        throw new InvalidOperationException("Stage wrote an object or published an index for a local transfer.");
                }

                Directory.CreateDirectory(Path.Combine(source, "resources", "00000001"));
                await File.WriteAllTextAsync(Path.Combine(source, "resources", "00000001", "notes.txt"), "ordinary repository notes");
                if (!(await git.StageAsync(["resources/00000001/notes.txt"])).Success ||
                    !(await git.CommitAsync("ordinary resources remain supported")).Success)
                    throw new InvalidOperationException("Stage or Commit rejected ordinary resources.");
                return (true, null);
            }));
        await Program.RunNamedCaseAsync("R23 publication refuses renamed local transfer documents and unpublished PFs", async () =>
        {
            //CSJ,variable=manifest,目録
            var manifest = """{"version":1,"packageName":"fixture.ymmpx","packageSha256":"fixture","assets":[]}""";
            var bundled = """{"Timelines":[{"Items":[{"FilePath":"C:\\synthetic\\resources\\ymmpm-transfer\\0000000a\\clip.txt"}]}]}""";
            //CSJ,variable=missing,欠落
            var missing = """{"Timelines":[{"Items":[{"FilePath":"ymmpm-missing/0000000b/clip.txt"}]}]}""";
            var layers = """{"Timelines":[{"Items":[{"enableLayersFilePath":"resources/ymmpm-transfer/0000000c/layers.psd"}]}]}""";
            foreach (var (name, contents) in new[] { ("manifest", manifest), ("v2", """{"YMMProjectManagerTransferV2":{},"Timelines":[]}"""), ("bundled", bundled), ("missing", missing), ("layers", layers) })
                await TestAsync(root, "r23-transfer-" + name, async (source, git) =>
                {
                    //CSJ,variable=file,ファイル
                    var file = Path.Combine(source, "notes.json");
                    await File.WriteAllTextAsync(file, contents);
                    if ((await git.StageAsync(["notes.json"])).Failure != GitFailure.MediaPublication)
                        throw new InvalidOperationException("Stage accepted a renamed private transfer document.");
                    await CommandAsync(source, "add", "--", "notes.json");
                    //CSJ,variable=approved,承認済み
                    var approved = (await git.InspectCommitAsync()).CommitTarget!;
                    if ((await git.CommitAsync(approved, "must refuse")).Failure != GitFailure.MediaPublication ||
                        File.Exists(Path.Combine(source, ".git", "refs", "heads", "main")))
                        throw new InvalidOperationException("Commit accepted a renamed private transfer document.");
                    await CommandAsync(source, "commit", "-m", "synthetic historical input");
                    await CommandAsync(source, "rm", "--", "notes.json");
                    await CommandAsync(source, "commit", "-m", "synthetic removal");
                    return (false, null);
                });
        });
        await Program.RunNamedCaseAsync("R23 Push refuses transfer payload from old path without a sidecar", () =>
            TestAsync(root, "r23-deleted-payload", async (source, _) =>
            {
                //CSJ,variable=relative,相対
                const string relative = "resources/ymmpm-transfer/00000001/transcript.txt";
                Directory.CreateDirectory(Path.Combine(source, Path.GetDirectoryName(relative)!));
                await CommitFileAsync(source, relative, "ordinary-looking local asset"u8.ToArray());
                await CommandAsync(source, "mv", relative, "notes.txt");
                await CommandAsync(source, "commit", "-m", "synthetic rename");
                return (false, null);
            }));
        await Program.RunNamedCaseAsync("R29 Stage and Commit block restore evidence beside an ordinary PF", () =>
            TestAsync(root, "restore-local", async (source, git) =>
            {
                var operation = Guid.NewGuid().ToString("N");
                var pending = ".ymm-restore-" + new string('a', 64) + ".pending.json";
                var index = Path.Combine(source, ".git", "index");
                foreach (var path in new[] { pending, $"{pending}.{operation}.tmp", $"{operation}.restore.tmp",
                    $"{operation}_before-restore.ymmp", $".ymm-restore-recovery-{operation}/project.ymmp" })
                {
                    var local = Path.Combine(source, path);
                    Directory.CreateDirectory(Path.GetDirectoryName(local)!);
                    await File.WriteAllTextAsync(local, """{"Timelines":[]}""");
                    var before = File.Exists(index) ? await File.ReadAllBytesAsync(index) : null;
                    var refused = await git.StageAsync([path]);
                    var after = File.Exists(index) ? await File.ReadAllBytesAsync(index) : null;
                    var changed = before is null ? after is not null : after is null || !before.AsSpan().SequenceEqual(after);
                    if (refused.Failure != GitFailure.MediaPublication || changed)
                        throw new InvalidOperationException("Stage accepted local restore evidence or changed the index: " + path);
                    await CommandAsync(source, "-c", "core.longpaths=true", "add", "--", path);
                    var approved = (await git.InspectCommitAsync()).CommitTarget!;
                    if ((await git.CommitAsync(approved, "must stay local")).Failure != GitFailure.MediaPublication ||
                        File.Exists(Path.Combine(source, ".git", "refs", "heads", "main")))
                        throw new InvalidOperationException("Commit accepted local restore evidence: " + path);
                    await CommandAsync(source, "-c", "core.longpaths=true", "rm", "--cached", "--", path);
                }

                await File.WriteAllTextAsync(Path.Combine(source, "ordinary.ymmp"), """{"Timelines":[]}""");
                await File.WriteAllTextAsync(Path.Combine(source, ".ymm-restore-notes.pending.json"), """{"Revision":1}""");
                if (!(await git.StageAsync(["ordinary.ymmp", ".ymm-restore-notes.pending.json"])).Success ||
                    !(await git.CommitAsync("ordinary PF and notes")).Success)
                    throw new InvalidOperationException("Git rejected an ordinary PF or notes outside the reserved names.");
                return (true, null);
            }));
        await Program.RunNamedCaseAsync("R29 Push blocks deleted restore history and renamed recovery records", async () =>
        {
            await TestAsync(root, "restore-candidate-history", async (source, _) =>
            {
                var candidate = $"{Guid.NewGuid():N}.restore.tmp";
                await CommitFileAsync(source, candidate, """{"Timelines":[]}"""u8.ToArray());
                await CommandAsync(source, "rm", "--", candidate);
                await CommandAsync(source, "commit", "-m", "remove synthetic private backup");
                return (false, null);
            });

            await TestAsync(root, "restore-journal-history", async (source, git) =>
            {
                const string portableName = "notes.json";
                var sha = new string('B', 64);
                var id = Guid.NewGuid();
                var record = $$"""{"Version":1,"Operation":"{{id}}","TargetPath":"project.ymmp","RetainedPath":"original.ymmp","CandidatePath":"snapshot.tmp","Original":{"Volume":1,"FileId":"{{id}}","Sha256":"{{sha}}"},"Intended":{"Volume":1,"FileId":"{{Guid.NewGuid()}}","Sha256":"{{sha}}"} }""";
                await File.WriteAllTextAsync(Path.Combine(source, portableName), record);
                if ((await git.StageAsync([portableName])).Failure != GitFailure.MediaPublication)
                    throw new InvalidOperationException("Stage accepted renamed restore evidence.");
                await CommandAsync(source, "add", "--", portableName);
                var approved = (await git.InspectCommitAsync()).CommitTarget!;
                if ((await git.CommitAsync(approved, "keep the journal private")).Failure != GitFailure.MediaPublication ||
                    File.Exists(Path.Combine(source, ".git", "refs", "heads", "main")))
                    throw new InvalidOperationException("Commit accepted renamed restore evidence.");
                await CommandAsync(source, "commit", "-m", "synthetic private journal");
                await CommandAsync(source, "rm", "--", portableName);
                await CommandAsync(source, "commit", "-m", "remove synthetic private journal");
                return (false, null);
            });
        });
    }

    //CSJ,method=TestAsync,テストする（非同期）
    private static async Task TestAsync(string root, string name, Func<string, GitRepository, Task<(bool Allow, string? ForbiddenMarker)>> prepare)
    {
        //CSJ,variable=folder,フォルダー
        var folder = Path.Combine(root, "pub-" + name);
        Directory.CreateDirectory(folder);
        await CommandAsync(folder, "init", "-b", "main", "src");
        await CommandAsync(folder, "init", "--bare", "out");
        var source = Path.Combine(folder, "src");
        //CSJ,variable=remote,リモート
        var remote = Path.Combine(folder, "out");
        await CommandAsync(source, "config", "--local", "user.name", "Synthetic");
        await CommandAsync(source, "config", "--local", "user.email", "synthetic@example.invalid");
        await CommandAsync(source, "remote", "add", "origin", remote);
        var git = new GitRepository(source);
        var expected = await prepare(source, git);
        //CSJ,variable=approval,承認
        var approval = (await git.InspectRemoteAsync(GitRemoteOperation.Push)).PushTarget
            ?? throw new InvalidOperationException("Push target unavailable.");
        var result = await git.PushTrustedAsync(approval);
        //CSJ,variable=branch,ブランチ
        var branch = Path.Combine(remote, "refs", "heads", "main");
        if (expected.Allow ? !result.Success || File.ReadAllText(branch).Trim() != approval.ObjectId
            : result.Failure != GitFailure.MediaPublication || File.Exists(branch))
            throw new InvalidOperationException($"Publication expectation failed ({name}): {result.Failure}, {result.StandardError}");
        if (expected.ForbiddenMarker is { } marker && File.Exists(marker))
            throw new InvalidOperationException("Push executed a repository-controlled helper.");
    }

    private static async Task CommitFileAsync(string folder, string relative, byte[] contents)
    {
        await File.WriteAllBytesAsync(Path.Combine(folder, relative), contents);
        await CommandAsync(folder, "add", "--", relative);
        await CommandAsync(folder, "commit", "-m", "synthetic input");
    }

    private static async Task CommandAsync(string folder, params string[] arguments)
    {
        var start = new ProcessStartInfo("git")
        {
            WorkingDirectory = folder, UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true,
        };
        foreach (var variable in start.Environment.Keys.Where(key => key.StartsWith("GIT_", StringComparison.OrdinalIgnoreCase)).ToArray())
            start.Environment.Remove(variable);
        start.Environment["GIT_CONFIG_GLOBAL"] = "NUL";
        start.Environment["GIT_CONFIG_NOSYSTEM"] = "1";
        start.Environment["GIT_TERMINAL_PROMPT"] = "0";
        //CSJ,variable=argument,引数
        foreach (var argument in new[] { "-c", "core.hooksPath=/dev/null", "-c", "commit.gpgSign=false" }.Concat(arguments))
            start.ArgumentList.Add(argument);
        //CSJ,variable=process,プロセス
        using var process = Process.Start(start)!;
        var output = process.StandardOutput.ReadToEndAsync();
        //CSJ,variable=error,エラー
        var error = process.StandardError.ReadToEndAsync();
        await Task.WhenAll(process.WaitForExitAsync(), output, error);
        if (process.ExitCode != 0) throw new InvalidOperationException($"Synthetic Git fixture failed: {await error}");
    }
}
