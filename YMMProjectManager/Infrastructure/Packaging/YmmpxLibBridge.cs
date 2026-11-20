using System.IO;
using System.Reflection;
using System.Text;
using System.Text.Json.Nodes;
using System.IO.Compression;
using System.Text.Json;
using YMMProjectManager.Domain.Packaging;

namespace YMMProjectManager.Infrastructure.Packaging;

public sealed class YmmpxLibBridge
{
    private readonly FileLogger logger;
    private readonly string? explicitAssemblyPath;
    private readonly string? baseDirectory;
    private Assembly? cachedAssembly;
    private string? cachedAssemblyPath;
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
        cachedAssembly = assembly;
        cachedAssemblyPath = assembly.Location;
    }

    public IReadOnlyList<string> SearchedPaths => searchedPaths;

    public string MissingLibraryMessage =>
        "YmmpxLib が見つかりません。\nYmmpxLibPlugin を YMM4 の user/plugin に導入してください。";

    public bool TryEnsureAvailable(out string? resolvedAssemblyPath)
    {
        var assembly = ResolveAssembly(out resolvedAssemblyPath);
        return assembly is not null;
    }

    public async Task<YmmpxPackageResult> CreatePackageAsync(
        string ymmpPath,
        string outputYmmpxPath,
        CancellationToken token,
        IProgress<double>? progress = null)
    {
        token.ThrowIfCancellationRequested();
        var assembly = ResolveAssembly(out var resolvedAssemblyPath);
        if (assembly is null)
        {
            LogMissingYmmpxLib("同梱");
            return new YmmpxPackageResult
            {
                Success = false,
                ErrorMessage = MissingLibraryMessage,
            };
        }

        OwnedStagingDirectory? staging = null;
        string? temporaryPath = null;
        (ulong Volume, Guid FileId)? temporaryIdentity = null;
        FileStream? validatedPackage = null;
        try
        {
            token.ThrowIfCancellationRequested();
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
            try
            {
                using var reader = new StreamReader(sourceGuard, Encoding.UTF8, detectEncodingFromByteOrderMarks: true, leaveOpen: true);
                var sourceText = await reader.ReadToEndAsync(token).ConfigureAwait(false);
                using var source = JsonDocument.Parse(sourceText);
                if (source.RootElement.ValueKind != JsonValueKind.Object) return InvalidProject();
            }
            catch (JsonException) { return InvalidProject(); }
            staging = OwnedStagingDirectory.Reserve(Path.GetDirectoryName(destination)!, ".ymmpm-package-");
            temporaryPath = PackagingOutputHelper.CreateTemporaryPackagePath(Path.Combine(staging.Path, Path.GetFileName(destination)));
            using (var reserved = new FileStream(temporaryPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                temporaryIdentity = ProjectFileEvidence.ReadPhysicalIdentity(reserved);
            var scan = ScanProjectForLogging(ymmpPath);
            token.ThrowIfCancellationRequested();
            logger.Info(
                $"YmmpxLib 同梱開始。対象 ymmp={ymmpPath}, 出力 ymmpx={outputYmmpxPath}, 探索パス={string.Join(" | ", searchedPaths)}");

            var serviceType = assembly.GetType("YmmpxLib.YmmpxPackageService")
                ?? throw new TypeLoadException("YmmpxPackageService が見つかりません。");
            var optionsType = assembly.GetType("YmmpxLib.YmmpxPackagingOptions")
                ?? throw new TypeLoadException("YmmpxPackagingOptions が見つかりません。");
            var progressType = assembly.GetType("YmmpxLib.YmmpxPackagingProgress")
                ?? throw new TypeLoadException("YmmpxPackagingProgress が見つかりません。");

            var options = Activator.CreateInstance(optionsType)
                ?? throw new InvalidOperationException("YmmpxPackagingOptions を生成できません。");
            optionsType.GetProperty("IncludeProjectUiSettings")?.SetValue(options, true);

            object? reporter = null;
            if (progress is not null)
            {
                var progressImplType = typeof(ObjectProgress<>).MakeGenericType(progressType);
                reporter = Activator.CreateInstance(progressImplType, new Action<object?>(value =>
                {
                    if (value is null)
                    {
                        return;
                    }

                    var t = value.GetType();
                    var percentage = ConvertToDouble(t.GetProperty("Percentage")?.GetValue(value));
                    progress.Report(percentage / 100d);
                }));
            }

            var createMethod = serviceType.GetMethods(BindingFlags.Public | BindingFlags.Static)
                .FirstOrDefault(m => m.Name == "CreatePackageAsync" && m.GetParameters().Length >= 5)
                ?? throw new MissingMethodException("CreatePackageAsync が見つかりません。");

            var args = createMethod.GetParameters().Length >= 6
                ? new object?[] { ymmpPath, temporaryPath, new HashSet<string>(StringComparer.OrdinalIgnoreCase), options, reporter, token }
                : new object?[] { ymmpPath, temporaryPath, new HashSet<string>(StringComparer.OrdinalIgnoreCase), options, reporter };

            var taskObj = createMethod.Invoke(null, args) ?? throw new InvalidOperationException("CreatePackageAsync の戻り値が null です。");
            if (taskObj is not Task task)
            {
                throw new InvalidOperationException("CreatePackageAsync が Task を返しません。");
            }

            await task.ConfigureAwait(false);
            var result = ExtractTaskResult(taskObj);
            var packagedOutputPath = GetStringProperty(result, "OutputPath") ?? temporaryPath;
            if (!string.Equals(Path.GetFullPath(packagedOutputPath), temporaryPath, StringComparison.OrdinalIgnoreCase) ||
                !File.Exists(temporaryPath) || new FileInfo(temporaryPath).Length == 0)
                throw new IOException("一時出力が不正です。");
            validatedPackage = new FileStream(YmmpxPackageReader.OpenPackageForPublication(temporaryPath), FileAccess.Read);
            await YmmpxPackageReader.ValidateAsync(validatedPackage, token, Path.GetFileName(ymmpPath)).ConfigureAwait(false);
            var resourceCount = GetIntProperty(result, "ResourceCount");
            var fileMap = GetDictionaryProperty(result, "FileMap");
            var links = scan.Links.Select(link => new YmmpxPackageLink
            {
                OriginalPath = link.OriginalPath,
                ResolvedPath = link.ResolvedPath,
                BundlePath = fileMap.TryGetValue(link.BundleFileName, out var bundlePath) ? bundlePath : link.BundlePath,
                Exists = link.Exists,
                Status = link.Exists ? "packaged" : "missing",
            }).ToArray();
            token.ThrowIfCancellationRequested();
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

        static YmmpxPackageResult InvalidProject() => new()
        {
            Success = false,
            ErrorMessage = "有効な .ymmp プロジェクトを指定してください。プロジェクトの JSON とファイルサイズを確認してください。",
        };
    }

    private void TryDeleteOwnedPackage(string path, (ulong Volume, Guid FileId) identity, FileStream? validatedPackage)
    {
        try
        {
            using var reopened = validatedPackage is null
                ? new FileStream(YmmpxPackageReader.OpenPackageForPublication(path), FileAccess.Read)
                : null;
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

    public async Task<YmmpxExtractResult> ExtractPackageAsync(
        string ymmpxPath,
        string outputDirectory,
        CancellationToken token,
        IProgress<double>? progress = null)
    {
        token.ThrowIfCancellationRequested();
        try
        {
            return await Task.Run(() => YmmpxPackageReader.ExtractAsync(ymmpxPath, outputDirectory, token, progress), token).ConfigureAwait(false);
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
                    : "展開に失敗しました。現行の YMMPX 形式と未使用の展開先を確認してください。",
            };
        }
    }

    internal static void ValidateArchiveForExtraction(Stream stream, CancellationToken token, long maxTotalBytes = 100L * 1024 * 1024 * 1024, int maxEntries = 100000)
    {
        token.ThrowIfCancellationRequested();
        using var archive = new ZipArchive(stream, ZipArchiveMode.Read, leaveOpen: true);
        ValidateArchiveForExtraction(archive, token, maxTotalBytes, maxEntries);
    }

    internal static void ValidateArchiveForExtraction(ZipArchive archive, CancellationToken token,
        long maxTotalBytes = 100L * 1024 * 1024 * 1024, int maxEntries = 100000)
    {
        token.ThrowIfCancellationRequested();
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var files = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        long total = 0;
        if (archive.Entries.Count > maxEntries) throw new InvalidDataException("パッケージ内の項目数が多すぎます。");
        foreach (var entry in archive.Entries)
        {
            token.ThrowIfCancellationRequested();
            var name = entry.FullName.Replace('\\', '/');
            if (string.IsNullOrEmpty(name) || name.StartsWith('/') || name.Contains(':'))
                throw new InvalidDataException("パッケージに不正なパスがあります。");
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
        foreach (var path in paths)
        {
            token.ThrowIfCancellationRequested();
            if (!files.Contains(path)) RecordDirectory(path);
            var slash = path.IndexOf('/');
            while (slash >= 0)
            {
                var directory = path[..slash];
                if (files.Contains(directory)) throw new InvalidDataException("ファイルとディレクトリが競合しています。");
                RecordDirectory(directory);
                slash = path.IndexOf('/', slash + 1);
            }
        }

        void RecordDirectory(string directory)
        {
            if (directorySpellings.TryGetValue(directory, out var previous) &&
                !string.Equals(previous, directory, StringComparison.Ordinal))
                throw new InvalidDataException("大文字・小文字だけが異なるフォルダー名が混在しています。");
            directorySpellings.TryAdd(directory, directory);
        }
    }

    private Assembly? ResolveAssembly(out string? resolvedAssemblyPath)
    {
        if (cachedAssembly is not null)
        {
            resolvedAssemblyPath = cachedAssemblyPath;
            return cachedAssembly;
        }

        if (!YmmpxLibResolver.TryResolveAssembly(out var assembly, out var assemblyPath, out var paths, baseDirectory, explicitAssemblyPath))
        {
            searchedPaths = paths;
            resolvedAssemblyPath = null;
            return null;
        }

        cachedAssembly = assembly;
        cachedAssemblyPath = assemblyPath;
        searchedPaths = paths;
        resolvedAssemblyPath = assemblyPath;
        return assembly;
    }

    private void LogMissingYmmpxLib(string operation)
    {
        logger.Error(
            new FileNotFoundException("YmmpxLib.dll が見つかりません。"),
            $"{operation}に必要な YmmpxLib が見つかりません。探索パス={string.Join(" | ", searchedPaths)}");
        logger.Flush();
    }

    private static int GetIntProperty(object obj, string propertyName)
    {
        try
        {
            var value = obj.GetType().GetProperty(propertyName)?.GetValue(obj);
            return value is null ? 0 : Convert.ToInt32(value);
        }
        catch
        {
            return 0;
        }
    }

    private static string? GetStringProperty(object obj, string propertyName)
    {
        try
        {
            return obj.GetType().GetProperty(propertyName)?.GetValue(obj)?.ToString();
        }
        catch
        {
            return null;
        }
    }

    private static Dictionary<string, string> GetDictionaryProperty(object obj, string propertyName)
    {
        try
        {
            var value = obj.GetType().GetProperty(propertyName)?.GetValue(obj);
            if (value is IReadOnlyDictionary<string, string> typed)
            {
                return typed.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.OrdinalIgnoreCase);
            }

            if (value is IDictionary<string, string> dict)
            {
                return dict.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.OrdinalIgnoreCase);
            }

            return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        }
        catch
        {
            return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        }
    }

    private static object ExtractTaskResult(object taskObj)
    {
        var taskType = taskObj.GetType();
        var resultProperty = taskType.GetProperty("Result");
        return resultProperty?.GetValue(taskObj)
            ?? throw new InvalidOperationException("Task.Result が取得できません。");
    }

    private static double ConvertToDouble(object? value)
    {
        try
        {
            return value is null ? 0d : Convert.ToDouble(value);
        }
        catch
        {
            return 0d;
        }
    }

    private sealed class ObjectProgress<T> : IProgress<T>
    {
        private readonly Action<object?> report;

        public ObjectProgress(Action<object?> report)
        {
            this.report = report;
        }

        public void Report(T value)
        {
            report(value);
        }
    }

    private sealed class ProjectScanResult
    {
        public int DetectedCount { get; init; }
        public int MissingCount { get; init; }
        public int DuplicateCount { get; init; }
        public List<ProjectLink> Links { get; init; } = [];
    }

    private sealed class ProjectLink
    {
        public string OriginalPath { get; init; } = string.Empty;
        public string ResolvedPath { get; init; } = string.Empty;
        public string BundlePath { get; init; } = string.Empty;
        public string BundleFileName => Path.GetFileName(BundlePath);
        public bool Exists { get; init; }
    }

    private ProjectScanResult ScanProjectForLogging(string ymmpPath)
    {
        if (!File.Exists(ymmpPath))
        {
            return new ProjectScanResult();
        }

        var projectDir = Path.GetDirectoryName(Path.GetFullPath(ymmpPath)) ?? string.Empty;
        var rawPaths = PackagingDetector.GetProjectFilePaths(ymmpPath);
        var uniqueResolved = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var links = new List<ProjectLink>();
        var missingCount = 0;
        var duplicateCount = 0;
        var packagedIndex = 0;

        foreach (var rawPath in rawPaths)
        {
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
        var invalid = Path.GetInvalidFileNameChars();
        var builder = new StringBuilder(fileName.Length);
        foreach (var ch in fileName)
        {
            builder.Append(invalid.Contains(ch) ? '_' : ch);
        }

        var sanitized = builder.ToString().Trim().TrimEnd('.');
        return string.IsNullOrWhiteSpace(sanitized) ? "file" : sanitized;
    }
}
