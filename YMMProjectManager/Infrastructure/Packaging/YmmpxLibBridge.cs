using System.IO;
using System.Reflection;
using System.Text;
using System.Text.Json.Nodes;
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

        try
        {
            var scan = ScanProjectForLogging(ymmpPath);
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
                ? new object?[] { ymmpPath, outputYmmpxPath, new HashSet<string>(StringComparer.OrdinalIgnoreCase), options, reporter, token }
                : new object?[] { ymmpPath, outputYmmpxPath, new HashSet<string>(StringComparer.OrdinalIgnoreCase), options, reporter };

            var taskObj = createMethod.Invoke(null, args) ?? throw new InvalidOperationException("CreatePackageAsync の戻り値が null です。");
            if (taskObj is not Task task)
            {
                throw new InvalidOperationException("CreatePackageAsync が Task を返しません。");
            }

            await task.ConfigureAwait(false);
            var result = ExtractTaskResult(taskObj);
            var packagedOutputPath = GetStringProperty(result, "OutputPath") ?? outputYmmpxPath;
            var resourceCount = GetIntProperty(result, "ResourceCount");
            var fileMap = GetDictionaryProperty(result, "FileMap");

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
                Links = scan.Links.Select(link => new YmmpxPackageLink
                {
                    OriginalPath = link.OriginalPath,
                    ResolvedPath = link.ResolvedPath,
                    BundlePath = fileMap.TryGetValue(link.BundleFileName, out var bundlePath) ? bundlePath : link.BundlePath,
                    Exists = link.Exists,
                    Status = link.Exists ? "packaged" : "missing",
                }).ToArray(),
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
    }

    public async Task<YmmpxExtractResult> ExtractPackageAsync(
        string ymmpxPath,
        string outputDirectory,
        CancellationToken token,
        IProgress<double>? progress = null)
    {
        var assembly = ResolveAssembly(out var resolvedAssemblyPath);
        if (assembly is null)
        {
            LogMissingYmmpxLib("展開");
            return new YmmpxExtractResult
            {
                Success = false,
                ErrorMessage = MissingLibraryMessage,
            };
        }

        try
        {
            logger.Info(
                $"YmmpxLib 展開開始。対象 ymmpx={ymmpxPath}, 展開先={outputDirectory}, 探索パス={string.Join(" | ", searchedPaths)}");

            var serviceType = assembly.GetType("YmmpxLib.YmmpxPackageService")
                ?? throw new TypeLoadException("YmmpxPackageService が見つかりません。");
            var method = serviceType.GetMethod("GetAvailableDirectoryPath", BindingFlags.Public | BindingFlags.Static)
                ?? throw new MissingMethodException("GetAvailableDirectoryPath が見つかりません。");
            var unpackMethod = serviceType.GetMethod("ExtractAndRestoreProject", BindingFlags.Public | BindingFlags.Static)
                ?? throw new MissingMethodException("ExtractAndRestoreProject が見つかりません。");

            var desiredDirectory = Path.Combine(outputDirectory, Path.GetFileNameWithoutExtension(ymmpxPath));
            var finalDirectory = method.Invoke(null, [desiredDirectory])?.ToString() ?? desiredDirectory;

            var unpackResult = unpackMethod.Invoke(null, [ymmpxPath, finalDirectory]) ?? throw new InvalidOperationException("展開結果が null です。");
            var projectFilePath = GetStringProperty(unpackResult, "ProjectFilePath")
                ?? throw new InvalidOperationException("ProjectFilePath が取得できません。");
            var replacedPathCount = GetIntProperty(unpackResult, "ReplacedPathCount");
            var linkMap = GetDictionaryProperty(unpackResult, "LinkMap");

            if (progress is not null)
            {
                progress.Report(1d);
            }

            logger.Info(
                $"YmmpxLib 展開成功。対象 ymmpx={ymmpxPath}, 展開先={finalDirectory}, 復元 ymmp={projectFilePath}, 置換 FilePath={replacedPathCount}, 解決={resolvedAssemblyPath}");
            logger.Flush();

            return new YmmpxExtractResult
            {
                Success = true,
                RestoredProjectPath = projectFilePath,
                OutputDirectory = finalDirectory,
                ReplacedPathCount = replacedPathCount,
                ExtractedResourceCount = linkMap.Count,
                Links = linkMap.Select(item => new YmmpxPackageLink
                {
                    OriginalPath = item.Key,
                    ResolvedPath = item.Value,
                    BundlePath = string.Empty,
                    Exists = true,
                    Status = "restored",
                }).ToArray(),
            };
        }
        catch (OperationCanceledException)
        {
            logger.Info($"YmmpxLib 展開キャンセル。対象 ymmpx={ymmpxPath}");
            logger.Flush();
            throw;
        }
        catch (Exception ex)
        {
            logger.Error(ex, $"YmmpxLib 展開失敗。対象 ymmpx={ymmpxPath}, 展開先={outputDirectory}, 探索パス={string.Join(" | ", searchedPaths)}");
            logger.Flush();
            return new YmmpxExtractResult
            {
                Success = false,
                ErrorMessage = "YmmpxLib を使った展開に失敗しました。ログを確認してください。",
            };
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
