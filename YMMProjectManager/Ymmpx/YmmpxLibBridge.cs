using System.IO;
using System.Reflection;
using System.Text;
using System.IO.Compression;
using System.Text.Json;
using YMMProjectManager.Domain.Packaging;

namespace YMMProjectManager.Infrastructure.Packaging;

//CSJ,class=YmmpxLibBridge,YMMPXライブラリ連携
public sealed class YmmpxLibBridge
{
    //CSJ,field=logger,ロガー
    private readonly FileLogger logger;
    private readonly string? explicitAssemblyPath;
    //CSJ,field=baseDirectory,基点ディレクトリ
    private readonly string? baseDirectory;
    private readonly Assembly? fixtureAssembly;
    private readonly object bindingGate = new();
    private YmmpxLibApi? boundApi;
    private IReadOnlyList<string> searchedPaths = [];

    public YmmpxLibBridge(FileLogger logger, string? explicitAssemblyPath = null, string? baseDirectory = null)
    {
        this.logger = logger;
        this.explicitAssemblyPath = explicitAssemblyPath;
        this.baseDirectory = baseDirectory;
    }

    internal YmmpxLibBridge(FileLogger logger, Assembly assembly)
    {
        this.logger = logger;
        fixtureAssembly = assembly;
    }

    //CSJ,property=SearchedPaths,検索済みのパス一覧
    public IReadOnlyList<string> SearchedPaths => searchedPaths;

    internal YmmpxLibStatus LastStatus { get; private set; }

    public string MissingLibraryMessage => LastStatus switch
    {
        YmmpxLibStatus.Unsupported => "導入済みの YmmpxLib は非対応です。対応する YmmpxLibPlugin v1.1.0 を確認してください。",
        YmmpxLibStatus.Invalid => "YmmpxLib.dll の正当性を確認できません。公式の YmmpxLibPlugin v1.1.0 を確認してください。",
        YmmpxLibStatus.LoadFailed => "YmmpxLib.dll を読み込めません。ファイルのアクセス権と導入先を確認してください。",
        _ => "YmmpxLib が見つかりません。\nYmmpxLibPlugin を YMM4 の user/plugin に導入してください。",
    };

    public bool TryEnsureAvailable(out string? resolvedAssemblyPath) =>
        ResolveApi(out resolvedAssemblyPath) is not null;

    //CSJ,method=CreatePackageAsync,パッケージを作成する（非同期）
    public Task<YmmpxPackageResult> CreatePackageAsync(
        //CSJ,parameter=ymmpPath,YMMPパス
        string ymmpPath,
        //CSJ,parameter=outputYmmpxPath,出力YMMPXパス
        string outputYmmpxPath,
        CancellationToken token,
        //CSJ,parameter=progress,進捗
        IProgress<double>? progress = null) => CreatePackageAsync(ymmpPath, outputYmmpxPath, new HashSet<string>(StringComparer.OrdinalIgnoreCase), token, progress);

    internal async Task<YmmpxPackageResult> CreatePackageAsync(string ymmpPath, string outputYmmpxPath,
        ISet<string> excludedFiles, CancellationToken token, IProgress<double>? progress)
    {
        token.ThrowIfCancellationRequested();
        var api = ResolveApi(out var resolvedAssemblyPath);
        if (api is null)
        {
            logger.Info($"YmmpxLib を利用できません。状態={LastStatus}");
            logger.Flush();
            return new YmmpxPackageResult
            {
                Success = false,
                ErrorMessage = MissingLibraryMessage,
            };
        }

        //CSJ,variable=staging,準備領域
        OwnedStagingDirectory? staging = null;
        //CSJ,variable=temporaryPath,一時パス
        string? temporaryPath = null;
        //CSJ,variable=temporaryIdentity,一時識別情報
        (ulong Volume, Guid FileId)? temporaryIdentity = null;
        FileStream? validatedPackage = null;
        try
        {
            token.ThrowIfCancellationRequested();
            //CSJ,variable=destination,格納先
            var destination = Path.GetFullPath(outputYmmpxPath);
            if (string.Equals(destination, Path.GetFullPath(ymmpPath), StringComparison.OrdinalIgnoreCase))
                throw new IOException("プロジェクトと同じパスには出力できません。");
            if (File.Exists(destination) || Directory.Exists(destination))
                return new YmmpxPackageResult { Success = false, ErrorMessage = "出力先は既に存在します。既存データを保護するため、別の名前で保存してください。" };
            // Keep the source readable but deny cooperating writers and replacement while packaging.
            await using var sourceGuard = new FileStream(ymmpPath, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, FileOptions.Asynchronous);
            if (!Path.GetExtension(ymmpPath).Equals(".ymmp", StringComparison.OrdinalIgnoreCase) ||
                sourceGuard.Length > YmmpxPackageReader.MaxProjectBytes)
                return InvalidProject();
            //CSJ,variable=sourceSnapshot,元スナップショット
            JsonDocument sourceSnapshot;
            try
            {
                //CSJ,variable=reader,読み取り元
                using var reader = new StreamReader(sourceGuard, Encoding.UTF8, detectEncodingFromByteOrderMarks: true, leaveOpen: true);
                sourceSnapshot = JsonDocument.Parse(await reader.ReadToEndAsync(token).ConfigureAwait(false));
            }
            catch (JsonException) { return InvalidProject(); }
            using var source = sourceSnapshot;
            if (source.RootElement.ValueKind != JsonValueKind.Object) return InvalidProject();
            staging = OwnedStagingDirectory.Reserve(Path.GetDirectoryName(destination)!, ".ymmpm-package-");
            temporaryPath = PackagingOutputHelper.CreateTemporaryPackagePath(Path.Combine(staging.Path, Path.GetFileName(destination)));
            //CSJ,variable=reserved,予約済み
            using (var reserved = new FileStream(temporaryPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                temporaryIdentity = ProjectFileEvidence.ReadPhysicalIdentity(reserved);
            //CSJ,variable=scan,走査
            var scan = ScanProjectForLogging(ymmpPath);
            token.ThrowIfCancellationRequested();
            logger.Info(
                $"YmmpxLib 同梱開始。対象 ymmp={ymmpPath}, 出力 ymmpx={outputYmmpxPath}, 探索パス={string.Join(" | ", searchedPaths)}");

            var result = await api.CreatePackageAsync(ymmpPath, temporaryPath, excludedFiles, progress, token).ConfigureAwait(false);
            var packagedOutputPath = result.OutputPath;
            if (!string.Equals(Path.GetFullPath(packagedOutputPath), temporaryPath, StringComparison.OrdinalIgnoreCase) ||
                !File.Exists(temporaryPath) || new FileInfo(temporaryPath).Length == 0)
                throw new IOException("一時出力が不正です。");
            validatedPackage = new FileStream(YmmpxPackageReader.OpenPackageForPublication(temporaryPath), FileAccess.Read);
            await YmmpxPackageReader.ValidateAsync(validatedPackage, ymmpPath, source.RootElement, token).ConfigureAwait(false);
            var resourceCount = result.ResourceCount;
            var fileMap = new Dictionary<string, string>(result.FileMap, StringComparer.OrdinalIgnoreCase);
            //CSJ,variable=links,リンク一覧
            var links = scan.Links.Select(link => new YmmpxPackageLink
            {
                OriginalPath = link.OriginalPath,
                ResolvedPath = link.ResolvedPath,
                BundlePath = fileMap.TryGetValue(link.BundleFileName, out var bundlePath) ? bundlePath : link.BundlePath,
                Exists = link.Exists,
                Status = link.Exists ? "packaged" : "missing",
            }).ToArray();
            token.ThrowIfCancellationRequested();
            var publishedIdentity = ProjectFileEvidence.ReadPhysicalIdentity(validatedPackage);
            try { YmmpxPackageReader.RenameOpenedPath(validatedPackage.SafeFileHandle, destination); }
            catch (IOException) when (File.Exists(destination) || Directory.Exists(destination))
            {
                return new YmmpxPackageResult { Success = false, ErrorMessage = "出力中に同じ名前のファイルが作成されました。別の名前で保存してください。" };
            }
            temporaryIdentity = null;
            packagedOutputPath = destination;

            logger.Info(
                $"YmmpxLib 同梱成功。対象 ymmp={ymmpPath}, 出力 ymmpx={packagedOutputPath}, 検出={scan.DetectedCount}, 同梱成功={resourceCount}, 不足={scan.MissingCount}, 重複={scan.DuplicateCount}, 解決={resolvedAssemblyPath}");
            logger.Flush();

            return new YmmpxPackageResult
            {
                Success = true,
                OutputPath = packagedOutputPath,
                PublishedIdentity = publishedIdentity,
                DetectedMaterialCount = scan.DetectedCount,
                PackagedMaterialCount = resourceCount,
                MissingMaterialCount = scan.MissingCount,
                DuplicateMaterialCount = scan.DuplicateCount,
                Links = links,
            };
        }
        catch (OperationCanceledException)
        {
            logger.Info($"YmmpxLib 同梱キャンセル。対象 ymmp={ymmpPath}");
            logger.Flush();
            throw;
        }
        catch (Exception ex)
        {
            logger.Error(ex, $"YmmpxLib 同梱失敗。対象 ymmp={ymmpPath}, 出力 ymmpx={outputYmmpxPath}, 探索パス={string.Join(" | ", searchedPaths)}");
            logger.Flush();
            return new YmmpxPackageResult
            {
                Success = false,
                ErrorMessage = "YmmpxLib を使った同梱に失敗しました。ログを確認してください。",
            };
        }
        finally
        {
            // The library can replace its output during creation. On failure, neither the
            // path nor a library result proves that a file with a different identity is ours.
            if (temporaryIdentity is { } owned && temporaryPath is not null)
                TryDeleteOwnedPackage(temporaryPath, owned, validatedPackage);
            validatedPackage?.Dispose();
            if (staging is not null)
            {
                try
                {
                    if (!staging.DeleteIfEmpty()) logger.Info($"同梱の作業フォルダーを保持しました。path={staging.Path}");
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    logger.Info($"同梱の作業フォルダーを後始末できませんでした。path={staging.Path}, error={ex.GetType().Name}");
                }
                finally { staging.Dispose(); }
            }
        }

        //CSJ,method=InvalidProject,無効プロジェクト
        static YmmpxPackageResult InvalidProject() => new()
        {
            Success = false,
            ErrorMessage = "有効な .ymmp プロジェクトを指定してください。プロジェクトの JSON とファイルサイズを確認してください。",
        };
    }

    //CSJ,method=TryDeleteOwnedPackage,管理対象のパッケージの削除を試みる
    private void TryDeleteOwnedPackage(string path, (ulong Volume, Guid FileId) identity, FileStream? validatedPackage)
    {
        try
        {
            using var reopened = validatedPackage is null
                ? new FileStream(YmmpxPackageReader.OpenPackageForPublication(path), FileAccess.Read)
                : null;
            //CSJ,variable=package,パッケージ
            var package = validatedPackage ?? reopened!;
            if (ProjectFileEvidence.ReadPhysicalIdentity(package) == identity &&
                YmmpxPackageReader.TryDeleteOpenedPath(package.SafeFileHandle)) return;
            logger.Info($"同梱の一時出力の所有を確認できないか削除できないため保持しました。path={path}");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logger.Info($"同梱の一時出力を後始末できませんでした。path={path}, error={ex.GetType().Name}");
        }
    }

    //CSJ,method=ExtractPackageAsync,パッケージを抽出する（非同期）
    public async Task<YmmpxExtractResult> ExtractPackageAsync(
        //CSJ,parameter=ymmpxPath,YMMPXパス
        string ymmpxPath,
        //CSJ,parameter=outputDirectory,出力ディレクトリ
        string outputDirectory,
        CancellationToken token,
        //CSJ,parameter=progress,進捗
        IProgress<double>? progress = null,
        //CSJ,parameter=requireTransfer,要求受け渡し
        bool requireTransfer = false)
    {
        token.ThrowIfCancellationRequested();
        try
        {
            return await Task.Run(() => YmmpxPackageReader.ExtractAsync(ymmpxPath, outputDirectory, token, progress, requireTransfer), token).ConfigureAwait(false);
        }
        catch (OperationCanceledException ex)
        {
            logger.Info($"YmmpxLib 展開キャンセル。対象 ymmpx={ymmpxPath}");
            if (ex.Data[YmmpxPackageReader.RetainedStagingPath] is string retained)
                logger.Info($"YMMPX 展開の作業フォルダーが保持されました。path={retained}");
            logger.Flush();
            throw;
        }
        catch (Exception ex)
        {
            logger.Error(ex, $"YmmpxLib 展開失敗。対象 ymmpx={ymmpxPath}, 展開先={outputDirectory}, 探索パス={string.Join(" | ", searchedPaths)}");
            logger.Flush();
            var retained = ex.Data[YmmpxPackageReader.RetainedStagingPath] as string;
            return new YmmpxExtractResult
            {
                Success = false,
                ErrorMessage = retained is not null
                    ? $"展開に失敗しました。公開していない作業フォルダーが保持されています。\n{retained}"
                    : ex is InvalidDataException
                        ? ex.Message
                        : "展開に失敗しました。現行の YMMPX 形式と未使用の展開先を確認してください。",
            };
        }
    }

    internal static void ValidateArchiveForExtraction(Stream stream, CancellationToken token, long maxTotalBytes = 100L * 1024 * 1024 * 1024, int maxEntries = 100000)
    {
        token.ThrowIfCancellationRequested();
        //CSJ,variable=archive,アーカイブ
        using var archive = new ZipArchive(stream, ZipArchiveMode.Read, leaveOpen: true);
        ValidateArchiveForExtraction(archive, token, maxTotalBytes, maxEntries);
    }

    internal static void ValidateArchiveForExtraction(ZipArchive archive, CancellationToken token,
        //CSJ,parameter=maxTotalBytes,最大合計バイト列
        long maxTotalBytes = 100L * 1024 * 1024 * 1024, int maxEntries = 100000)
    {
        token.ThrowIfCancellationRequested();
        //CSJ,variable=paths,パス一覧
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        //CSJ,variable=files,ファイル一覧
        var files = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        //CSJ,variable=total,合計
        long total = 0;
        if (archive.Entries.Count > maxEntries) throw new InvalidDataException("パッケージ内の項目数が多すぎます。");
        foreach (var entry in archive.Entries)
        {
            token.ThrowIfCancellationRequested();
            //CSJ,variable=name,名前
            var name = entry.FullName.Replace('\\', '/');
            if (string.IsNullOrEmpty(name) || name.StartsWith('/') || name.Contains(':'))
                throw new InvalidDataException("パッケージに不正なパスがあります。");
            //CSJ,variable=parts,構成要素
            var parts = name.TrimEnd('/').Split('/');
            if (parts.Length > 32 || name.Length > 2048 || parts.Any(part => part.Length is 0 or > 255 || part is "." or ".." || part.EndsWith(' ') || part.EndsWith('.') || part.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0))
                throw new InvalidDataException("パッケージに不正なパスがあります。");
            foreach (var part in parts)
            {
                var stem = part.Split('.')[0].TrimEnd(' ');
                if (stem.Equals("CON", StringComparison.OrdinalIgnoreCase) || stem.Equals("PRN", StringComparison.OrdinalIgnoreCase) ||
                    stem.Equals("AUX", StringComparison.OrdinalIgnoreCase) || stem.Equals("NUL", StringComparison.OrdinalIgnoreCase) ||
                    stem.Equals("CONIN$", StringComparison.OrdinalIgnoreCase) || stem.Equals("CONOUT$", StringComparison.OrdinalIgnoreCase) ||
                    (stem.Length == 4 && (stem.StartsWith("COM", StringComparison.OrdinalIgnoreCase) || stem.StartsWith("LPT", StringComparison.OrdinalIgnoreCase)) && "0123456789¹²³".Contains(stem[3])))
                    throw new InvalidDataException("パッケージに予約済みの名前があります。");
            }
            if (!paths.Add(string.Join('/', parts))) throw new InvalidDataException("パッケージに重複したパスがあります。");
            if (!name.EndsWith('/')) files.Add(string.Join('/', parts));
            if (((entry.ExternalAttributes >> 16) & 0xF000) is not (0 or 0x8000 or 0x4000) ||
                (entry.ExternalAttributes & (int)FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException("リンク項目や特殊ファイルは展開できません。");
            if (entry.Length > maxTotalBytes - total) throw new InvalidDataException("パッケージの展開サイズが上限を超えます。");
            total += entry.Length;
        }
        var directorySpellings = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        //CSJ,variable=path,パス
        foreach (var path in paths)
        {
            token.ThrowIfCancellationRequested();
            if (!files.Contains(path)) RecordDirectory(path);
            var slash = path.IndexOf('/');
            while (slash >= 0)
            {
                //CSJ,variable=directory,ディレクトリ
                var directory = path[..slash];
                if (files.Contains(directory)) throw new InvalidDataException("ファイルとディレクトリが競合しています。");
                RecordDirectory(directory);
                slash = path.IndexOf('/', slash + 1);
            }
        }

        //CSJ,method=RecordDirectory,ディレクトリを記録する
        void RecordDirectory(string directory)
        {
            if (directorySpellings.TryGetValue(directory, out var previous) &&
                !string.Equals(previous, directory, StringComparison.Ordinal))
                throw new InvalidDataException("大文字・小文字だけが異なるフォルダー名が混在しています。");
            directorySpellings.TryAdd(directory, directory);
        }
    }

    private YmmpxLibApi? ResolveApi(out string? resolvedAssemblyPath)
    {
        lock (bindingGate)
        {
            Assembly? assembly;
            if (fixtureAssembly is not null)
            {
                assembly = fixtureAssembly;
                resolvedAssemblyPath = fixtureAssembly.Location;
                LastStatus = YmmpxLibStatus.Ready;
            }
            else
            {
                LastStatus = YmmpxLibResolver.Resolve(out assembly, out resolvedAssemblyPath, out searchedPaths,
                    baseDirectory, explicitAssemblyPath);
            }

            if (assembly is null) return null;
            try
            {
                return boundApi ??= new YmmpxLibApi(assembly);
            }
            catch (Exception ex) when (ex is TypeLoadException or ReflectionTypeLoadException or MissingMemberException or AmbiguousMatchException or
                ArgumentException or InvalidOperationException)
            {
                LastStatus = YmmpxLibStatus.Unsupported;
                resolvedAssemblyPath = null;
                return null;
            }
            catch (Exception ex) when (ex is IOException or BadImageFormatException or NotSupportedException)
            {
                LastStatus = YmmpxLibStatus.LoadFailed;
                resolvedAssemblyPath = null;
                return null;
            }
        }
    }

    //CSJ,class=ProjectScanResult,プロジェクト走査結果
    private sealed class ProjectScanResult
    {
        //CSJ,property=DetectedCount,検出数
        public int DetectedCount { get; init; }
        //CSJ,property=MissingCount,欠落件数
        public int MissingCount { get; init; }
        //CSJ,property=DuplicateCount,重複件数
        public int DuplicateCount { get; init; }
        //CSJ,property=Links,リンク一覧
        public List<ProjectLink> Links { get; init; } = [];
    }

    //CSJ,class=ProjectLink,プロジェクトリンク
    private sealed class ProjectLink
    {
        //CSJ,property=OriginalPath,元のパス
        public string OriginalPath { get; init; } = string.Empty;
        //CSJ,property=ResolvedPath,解決済みのパス
        public string ResolvedPath { get; init; } = string.Empty;
        //CSJ,property=BundlePath,一括パッケージのパス
        public string BundlePath { get; init; } = string.Empty;
        //CSJ,property=BundleFileName,一括パッケージファイル名
        public string BundleFileName => Path.GetFileName(BundlePath);
        //CSJ,property=Exists,存在するか判定する
        public bool Exists { get; init; }
    }

    private ProjectScanResult ScanProjectForLogging(string ymmpPath)
    {
        if (!File.Exists(ymmpPath))
        {
            return new ProjectScanResult();
        }

        var projectDir = Path.GetDirectoryName(Path.GetFullPath(ymmpPath)) ?? string.Empty;
        //CSJ,variable=rawPaths,生データパス一覧
        var rawPaths = PackagingDetector.GetProjectFilePaths(ymmpPath);
        var uniqueResolved = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        //CSJ,variable=links,リンク一覧
        var links = new List<ProjectLink>();
        //CSJ,variable=missingCount,欠落件数
        var missingCount = 0;
        //CSJ,variable=duplicateCount,重複件数
        var duplicateCount = 0;
        var packagedIndex = 0;

        //CSJ,variable=rawPath,生データパス
        foreach (var rawPath in rawPaths)
        {
            //CSJ,variable=resolved,解決済み
            var resolved = PackagingDetector.ResolveMaterialPath(rawPath, projectDir);
            var exists = !string.IsNullOrWhiteSpace(resolved) && File.Exists(resolved);
            if (!exists)
            {
                missingCount++;
                links.Add(new ProjectLink
                {
                    OriginalPath = rawPath,
                    ResolvedPath = resolved ?? string.Empty,
                    BundlePath = string.Empty,
                    Exists = false,
                });
                continue;
            }

            if (!uniqueResolved.Add(resolved!))
            {
                duplicateCount++;
                links.Add(new ProjectLink
                {
                    OriginalPath = rawPath,
                    ResolvedPath = resolved!,
                    BundlePath = string.Empty,
                    Exists = true,
                });
                continue;
            }

            packagedIndex++;
            //CSJ,variable=bundlePath,一括パッケージのパス
            var bundlePath = $"resources/{packagedIndex:D6}_{SanitizeFileName(Path.GetFileName(resolved!))}";
            links.Add(new ProjectLink
            {
                OriginalPath = rawPath,
                ResolvedPath = resolved!,
                BundlePath = bundlePath,
                Exists = true,
            });
        }

        return new ProjectScanResult
        {
            DetectedCount = rawPaths.Count,
            MissingCount = missingCount,
            DuplicateCount = duplicateCount,
            Links = links,
        };
    }

    private static string SanitizeFileName(string fileName)
    {
        //CSJ,variable=invalid,無効
        var invalid = Path.GetInvalidFileNameChars();
        //CSJ,variable=builder,ビルダー
        var builder = new StringBuilder(fileName.Length);
        foreach (var ch in fileName)
        {
            builder.Append(invalid.Contains(ch) ? '_' : ch);
        }

        var sanitized = builder.ToString().Trim().TrimEnd('.');
        return string.IsNullOrWhiteSpace(sanitized) ? "file" : sanitized;
    }
}
