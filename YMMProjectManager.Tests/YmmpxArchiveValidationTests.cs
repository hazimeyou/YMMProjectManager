using System.IO;
using System.IO.Compression;
using YMMProjectManager.Infrastructure.Packaging;

//CSJ,class=YmmpxArchiveValidationTests,YMMPXアーカイブ妥当性検証テスト
internal static class YmmpxArchiveValidationTests
{
    //CSJ,method=RunAsync,実行する（非同期）
    public static async Task RunAsync(string workRoot)
    {
        static MemoryStream Archive(params string[] names)
        {
            //CSJ,variable=stream,ストリーム
            var stream = new MemoryStream();
            using (var zip = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
                //CSJ,variable=name,名前
                foreach (var name in names)
                    //CSJ,variable=writer,書き込み処理
                    using (var writer = new StreamWriter(zip.CreateEntry(name).Open())) writer.Write("synthetic");
            stream.Position = 0;
            return stream;
        }
        static void Rejected(params string[] names)
        {
            //CSJ,variable=stream,ストリーム
            using var stream = Archive(names);
            try { YmmpxLibBridge.ValidateArchiveForExtraction(stream, CancellationToken.None); throw new Exception("unsafe archive accepted"); }
            catch (InvalidDataException) { }
        }
        //CSJ,variable=safe,安全
        using (var safe = Archive("resources/a.txt", "project.ymmp")) YmmpxLibBridge.ValidateArchiveForExtraction(safe, CancellationToken.None);
        Rejected("../outside.txt"); Rejected("/absolute.txt"); Rejected("C:/outside.txt");
        Rejected("resources/Folder/a.txt", "resources/folder/b.txt");
        Rejected("resources/Folder/a.txt", "resources/folder/nested/b.txt");
        Rejected("folder/a.txt", "FOLDER/A.TXT"); Rejected("file", "file/child"); Rejected("NUL.txt"); Rejected("file.txt:stream");
        using (var oversized = Archive("file.txt"))
        {
            try { YmmpxLibBridge.ValidateArchiveForExtraction(oversized, CancellationToken.None, maxTotalBytes: 2); throw new Exception("oversize archive accepted"); }
            catch (InvalidDataException) { }
        }
        //CSJ,variable=cancelled,キャンセル済み
        using (var cancelled = new CancellationTokenSource())
        //CSJ,variable=archive,アーカイブ
        using (var archive = Archive("file.txt"))
        {
            cancelled.Cancel();
            try { YmmpxLibBridge.ValidateArchiveForExtraction(archive, cancelled.Token); throw new Exception("cancellation ignored"); }
            catch (OperationCanceledException) { }
        }
        //CSJ,variable=cancelled,キャンセル済み
        using (var cancelled = new CancellationTokenSource())
        //CSJ,variable=emptyArchive,空アーカイブ
        using (var emptyArchive = Archive())
        {
            cancelled.Cancel();
            try { YmmpxLibBridge.ValidateArchiveForExtraction(emptyArchive, cancelled.Token); throw new Exception("empty archive cancellation ignored"); }
            catch (OperationCanceledException) { }
        }
        //CSJ,variable=root,ルート
        var root = Path.Combine(workRoot, "ymmpx-archive-preflight"); Directory.CreateDirectory(root);
        //CSJ,variable=path,パス
        var path = Path.Combine(root, "unsafe.ymmpx");
        //CSJ,variable=bytes,バイト列
        using (var bytes = Archive("../outside.txt")) await File.WriteAllBytesAsync(path, bytes.ToArray());
        var bridge = new YmmpxLibBridge(new YMMProjectManager.Infrastructure.FileLogger(Path.Combine(root, "test.log")), typeof(YmmpxLib.YmmpxPackageService).Assembly);
        var before = YmmpxLib.YmmpxPackageService.ExtractionCalls;
        var result = await bridge.ExtractPackageAsync(path, Path.Combine(root, "rejected-extraction"), CancellationToken.None);
        if (result.Success || YmmpxLib.YmmpxPackageService.ExtractionCalls != before) throw new Exception("unsafe archive reached extractor");
        //CSJ,variable=bytes,バイト列
        using (var bytes = Archive("project.ymmp")) await File.WriteAllBytesAsync(path, bytes.ToArray());
        //CSJ,variable=destination,格納先
        var destination = Path.Combine(root, "restored");
        //CSJ,variable=unsupported,未対応
        var unsupported = await bridge.ExtractPackageAsync(path, destination, CancellationToken.None);
        if (unsupported.Success || Directory.Exists(destination)) throw new Exception("unverified format was published");
        WriteCurrentPackage(path, """{"Items":[{"FilePath":"./resources/clip.txt"},{"FilePath":"C:\\fixture\\missing.txt"}],"Custom":42}""");
        //CSJ,variable=originalHash,元のハッシュ
        var originalHash = await ReadHashAsync(path);
        var recovered = await bridge.ExtractPackageAsync(path, destination, CancellationToken.None);
        if (!recovered.Success || recovered.ReplacedPathCount != 1 || recovered.ExtractedResourceCount != 1)
            throw new Exception("managed extraction failed: " + recovered.ErrorMessage);
        //CSJ,variable=project,プロジェクト
        using var project = System.Text.Json.JsonDocument.Parse(await File.ReadAllTextAsync(recovered.RestoredProjectPath!));
        var items = project.RootElement.GetProperty("Items");
        //CSJ,variable=resource,リソース
        var resource = items[0].GetProperty("FilePath").GetString();
        if (resource != Path.Combine(destination, "resources", "clip.txt") || await File.ReadAllTextAsync(resource) != "synthetic asset" ||
            items[1].GetProperty("FilePath").GetString() != @"C:\fixture\missing.txt" ||
            project.RootElement.GetProperty("Custom").GetInt32() != 42) throw new Exception("reference or project field mismatch");
        if ((await bridge.ExtractPackageAsync(path, destination, CancellationToken.None)).Success ||
            YmmpxLib.YmmpxPackageService.ExtractionCalls != before)
            throw new Exception("managed extraction overwrote existing output or invoked external extractor");
        //CSJ,variable=cancelledOutput,キャンセル済み出力
        var cancelledOutput = Path.Combine(root, "cancelled");
        using (var cancelDuringCopy = new CancellationTokenSource())
        {
            try
            {
                await bridge.ExtractPackageAsync(path, cancelledOutput, cancelDuringCopy.Token, new InlineProgress(() => cancelDuringCopy.Cancel()));
                throw new Exception("mid-extraction cancellation was ignored");
            }
            catch (OperationCanceledException) { }
        }
        if (Directory.Exists(cancelledOutput) || Directory.EnumerateDirectories(root, ".ymmpx-*").Any())
            throw new Exception("cancelled extraction leaked output");
        var raced = Path.Combine(root, "raced");
        var race = await bridge.ExtractPackageAsync(path, raced, CancellationToken.None, new InlineProgress(() =>
        {
            Directory.CreateDirectory(raced);
            File.WriteAllText(Path.Combine(raced, "sentinel.txt"), "existing");
        }));
        if (race.Success || await File.ReadAllTextAsync(Path.Combine(raced, "sentinel.txt")) != "existing" ||
            Directory.EnumerateDirectories(root, ".ymmpx-*").Any())
            throw new Exception("destination race did not preserve existing output");
        //CSJ,variable=failed,失敗
        var failed = Path.Combine(root, "failed-copy");
        //CSJ,variable=failure,失敗
        var failure = await bridge.ExtractPackageAsync(path, failed, CancellationToken.None,
            new InlineProgress(() => throw new IOException("synthetic interruption after staging write")));
        if (failure.Success || Directory.Exists(failed) || Directory.EnumerateDirectories(root, ".ymmpx-*").Any() ||
            await ReadHashAsync(path) != originalHash)
            throw new Exception("interrupted extraction left output or modified the archive");

        //CSJ,variable=missingInternal,欠落内部
        var missingInternal = Path.Combine(root, "missing-internal.ymmpx");
        WriteCurrentPackage(missingInternal, """{"Items":[{"FilePath":".\\resources\\missing.txt"}]}""");
        //CSJ,variable=missingOutput,欠落出力
        var missingOutput = Path.Combine(root, "missing-internal-output");
        if ((await bridge.ExtractPackageAsync(missingInternal, missingOutput, CancellationToken.None)).Success ||
            Directory.Exists(missingOutput) || Directory.EnumerateDirectories(root, ".ymmpx-*").Any())
            throw new Exception("missing internal resource was accepted");
        await VerifyReparseParentRejectedAsync(bridge, path, root);
        await Program.RunNamedCaseAsync("Interrupted nested extraction preserves unowned staging data",
            () => VerifyNestedCleanupAsync(bridge, root));
        Console.WriteLine("YMMPX archive safety preflight checks passed.");
    }

    //CSJ,method=VerifyNestedCleanupAsync,入れ子後片付けを検証する（非同期）
    private static async Task VerifyNestedCleanupAsync(YmmpxLibBridge bridge, string root)
    {
        //CSJ,variable=archive,アーカイブ
        var archive = Path.Combine(root, "nested.ymmpx");
        WriteCurrentPackage(archive, """{"Items":[{"FilePath":"resources/nested/clip.txt"}]}""", "resources/nested/clip.txt");
        //CSJ,variable=archiveHash,アーカイブハッシュ
        var archiveHash = await ReadHashAsync(archive);
        //CSJ,variable=destination,格納先
        var destination = Path.Combine(root, "nested-destination");
        string? sentinel = null;
        var writes = 0;
        //CSJ,variable=extraction,抽出
        var extraction = await bridge.ExtractPackageAsync(archive, destination, CancellationToken.None, new InlineProgress(() =>
        {
            if (++writes != 4) return;
            //CSJ,variable=staging,準備領域
            var staging = Directory.EnumerateDirectories(root, ".ymmpx-*").Single();
            sentinel = Path.Combine(staging, "resources", "nested", "unowned-note.txt");
            File.WriteAllText(sentinel, "data from a separate operation");
            throw new IOException("synthetic failure after nested resource write");
        }));
        if (extraction.Success || Directory.Exists(destination) || sentinel is null ||
            await File.ReadAllTextAsync(sentinel) != "data from a separate operation" || await ReadHashAsync(archive) != archiveHash)
            throw new Exception("nested extraction did not preserve unrelated data or source archive");
        //CSJ,variable=ownedRoot,管理対象のルート
        var ownedRoot = Directory.EnumerateDirectories(root, ".ymmpx-*").Single();
        if (!Directory.EnumerateFiles(ownedRoot, "*", SearchOption.AllDirectories).SequenceEqual(new[] { sentinel }))
            throw new Exception("owned staged output files were not cleaned");
        if (extraction.ErrorMessage?.Contains(ownedRoot, StringComparison.Ordinal) != true)
            throw new Exception("retained extraction work directory was not reported");
    }

    //CSJ,method=VerifyReparseParentRejectedAsync,再解析親拒否を検証する（非同期）
    private static async Task VerifyReparseParentRejectedAsync(YmmpxLibBridge bridge, string package, string root)
    {
        var target = Path.Combine(root, "reparse-target");
        Directory.CreateDirectory(target);
        var parent = Path.Combine(root, "reparse-parent");
        try { Directory.CreateSymbolicLink(parent, target); }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException { HResult: unchecked((int)0x80070522) })
        {
            Program.ReportConditionalSkip("YMMPX reparse-parent fixture requires permission to create a symbolic link");
            return;
        }
        try
        {
            //CSJ,variable=outcome,処理結果
            var outcome = await bridge.ExtractPackageAsync(package, Path.Combine(parent, "restored"), CancellationToken.None);
            if (outcome.Success || Directory.EnumerateFileSystemEntries(target).Any())
                throw new Exception("extraction wrote through a reparse parent");
        }
        finally { Directory.Delete(parent); }
    }

    //CSJ,method=ReadHashAsync,ハッシュを読み取る（非同期）
    private static async Task<string> ReadHashAsync(string path)
    {
        //CSJ,variable=stream,ストリーム
        await using var stream = File.OpenRead(path);
        return Convert.ToHexString(await System.Security.Cryptography.SHA256.HashDataAsync(stream));
    }

    //CSJ,method=WriteCurrentPackage,現在のパッケージを書き込む
    private static void WriteCurrentPackage(string path, string projectJson, string resourceName = "resources/clip.txt")
    {
        //CSJ,variable=stream,ストリーム
        using var stream = new FileStream(path, FileMode.Create, FileAccess.Write);
        using var zip = new ZipArchive(stream, ZipArchiveMode.Create);
        Write("_ymmpx_project_path.txt", "project.ymmp");
        Write("links.json", System.Text.Json.JsonSerializer.Serialize(new Dictionary<string, string> { [resourceName] = resourceName }));
        Write("project.ymmp", projectJson);
        Write(resourceName, "synthetic asset");

        //CSJ,method=Write,書き込む
        void Write(string name, string content)
        {
            //CSJ,variable=writer,書き込み処理
            using var writer = new StreamWriter(zip.CreateEntry(name).Open());
            writer.Write(content);
        }
    }

    //CSJ,class=InlineProgress,インライン進捗
    private sealed class InlineProgress(Action action) : IProgress<double>
    {
        public void Report(double value) => action();
    }
}
