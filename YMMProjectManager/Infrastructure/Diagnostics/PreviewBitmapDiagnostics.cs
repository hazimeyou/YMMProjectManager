using System.Diagnostics;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using YMMProjectManager.Application.Diagnostics;
using YMMProjectManager.Application.Thumbnails;
using YMMProjectManager.Infrastructure;
using YMMProjectManager.Infrastructure.Thumbnails;

namespace YMMProjectManager.Infrastructure.Diagnostics;

public sealed class PreviewBitmapDiagnostics
{
    private static readonly string[] CandidateKeywords =
    [
        "Preview",
        "ScenePreview",
        "Player",
        "Render",
        "Video",
        "Frame",
    ];

    private static readonly string[] MethodKeywords =
    [
        "GetBitmap",
        "GetImage",
        "Capture",
        "Render",
        "SaveImage",
        "Image",
        "Clipboard",
        "Bitmap",
        "Frame",
    ];

    private readonly FileLogger logger;
    private readonly YmmPreviewDiscoveryService discoveryService;
    private readonly string diagnosticsDirectory;
    private readonly string resultJsonPath;
    private readonly string captureResultJsonPath;
    private readonly string historyJsonPath;
    private readonly string windowsJsonPath;
    private readonly string visualTreeJsonPath;
    private readonly string previewCandidatesJsonPath;
    private readonly string previewMethodsJsonPath;
    private readonly string previewMethodSignaturesJsonPath;
    private readonly string captureFalseJsonPath;
    private readonly string captureTrueJsonPath;
    private readonly string comparisonJsonPath;
    private readonly string previewPngPath;
    private readonly string previewFalsePngPath;
    private readonly string previewTruePngPath;

    public PreviewBitmapDiagnostics(FileLogger logger, string? diagnosticsDirectory = null)
    {
        this.logger = logger;
        discoveryService = new YmmPreviewDiscoveryService(logger);
        this.diagnosticsDirectory = diagnosticsDirectory ?? Path.Combine(Path.GetTempPath(), "YMMProjectManager", "PreviewDiagnostics");
        resultJsonPath = Path.Combine(this.diagnosticsDirectory, "diagnostic-result.json");
        captureResultJsonPath = Path.Combine(this.diagnosticsDirectory, "capture-result.json");
        historyJsonPath = Path.Combine(this.diagnosticsDirectory, "history.json");
        windowsJsonPath = Path.Combine(this.diagnosticsDirectory, "diagnostic-windows.json");
        visualTreeJsonPath = Path.Combine(this.diagnosticsDirectory, "diagnostic-visualtree.json");
        previewCandidatesJsonPath = Path.Combine(this.diagnosticsDirectory, "preview-candidates.json");
        previewMethodsJsonPath = Path.Combine(this.diagnosticsDirectory, "preview-methods.json");
        previewMethodSignaturesJsonPath = Path.Combine(this.diagnosticsDirectory, "preview-method-signatures.json");
        captureFalseJsonPath = Path.Combine(this.diagnosticsDirectory, "capture-false.json");
        captureTrueJsonPath = Path.Combine(this.diagnosticsDirectory, "capture-true.json");
        comparisonJsonPath = Path.Combine(this.diagnosticsDirectory, "comparison.json");
        previewPngPath = Path.Combine(this.diagnosticsDirectory, "preview-test.png");
        previewFalsePngPath = Path.Combine(this.diagnosticsDirectory, "preview-false.png");
        previewTruePngPath = Path.Combine(this.diagnosticsDirectory, "preview-true.png");
    }

    public async Task<PreviewBitmapDiagnosticsResult> RunAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var dispatcher = global::System.Windows.Application.Current?.Dispatcher;
        if (dispatcher is null)
        {
            logger.Info("Preview Bitmap Diagnostics: dispatcher unavailable");
            var result = CreateFailureResult("dispatcher unavailable", TimeSpan.Zero);
            var falseCapture = CreateFailureCaptureResult("dispatcher unavailable", false, TimeSpan.Zero);
            var trueCapture = CreateFailureCaptureResult("dispatcher unavailable", true, TimeSpan.Zero);
            var comparison = CompareCaptures(falseCapture, trueCapture);
            WriteAllOutputs(result, falseCapture, trueCapture, SelectPreferredCapture(falseCapture, trueCapture, comparison), comparison);
            return result;
        }

        var watch = Stopwatch.StartNew();
        try
        {
            return await dispatcher.InvokeAsync(
                    () => RunOnUiThread(watch),
                    System.Windows.Threading.DispatcherPriority.Background,
                    cancellationToken)
                .Task
                .ConfigureAwait(true);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.Error(ex, "Preview Bitmap Diagnostics failed.");
            var result = CreateFailureResult(ex.Message, watch.Elapsed);
            var failedFalseCapture = CreateFailureCaptureResult(ex.Message, false, watch.Elapsed);
            var failedTrueCapture = CreateFailureCaptureResult(ex.Message, true, watch.Elapsed);
            var failedComparison = CompareCaptures(failedFalseCapture, failedTrueCapture);
            WriteAllOutputs(result, failedFalseCapture, failedTrueCapture, SelectPreferredCapture(failedFalseCapture, failedTrueCapture, failedComparison), failedComparison);
            return result;
        }
    }

    private PreviewBitmapDiagnosticsResult RunOnUiThread(Stopwatch watch)
    {
        Directory.CreateDirectory(diagnosticsDirectory);

        try
        {
            var discovery = discoveryService.DiscoverAsync(CancellationToken.None).GetAwaiter().GetResult();
            var windows = discovery.Windows;
            var visualTree = discovery.VisualTree;
            var candidates = discovery.Candidates.Select(info => new CandidateDiscovery(
                Type.GetType(info.FoundType ?? string.Empty, throwOnError: false) ?? typeof(object),
                null,
                info)).ToArray();
            var methodGroups = discovery.Methods;
            var methodSignatures = discovery.MethodSignatures;
            var previewViewFound = discovery.PreviewViewFound;
            var previewViewModelFound = discovery.PreviewViewModelFound;
            var scenePreviewViewModelFound = discovery.ScenePreviewViewModelFound;
            var getBitmapMethodFound = discovery.GetBitmapMethodFound;
            var invocationCandidates = discovery.GetBitmapInvocationCandidates;
            var discoverySucceeded = discovery.DiscoverySucceeded;
            var discoveryLevelReached = discovery.DiscoveryLevelReached;

            WriteJson(windowsJsonPath, windows);
            logger.Info($"Window discovery complete. count={windows.Count}");

            WriteJson(visualTreeJsonPath, visualTree);
            logger.Info($"VisualTree discovery complete. count={visualTree.Count}");

            WriteJson(previewCandidatesJsonPath, discovery.Candidates.ToArray());
            logger.Info($"Preview candidate discovery complete. count={discovery.PreviewCandidateCount}");

            WriteJson(previewMethodsJsonPath, methodGroups);
            logger.Info($"Method discovery complete. groups={methodGroups.Count}, methods={methodGroups.Sum(x => x.Methods.Count)}");

            WriteJson(previewMethodSignaturesJsonPath, methodSignatures);
            logger.Info($"Method signature discovery complete. signatures={methodSignatures.Count}");

            var result = new PreviewBitmapDiagnosticsResult
            {
                DiscoverySucceeded = discoverySucceeded,
                DiscoveryLevelReached = discoveryLevelReached,
                WindowCount = windows.Count,
                VisualTreeElementCount = visualTree.Count,
                PreviewCandidateCount = discovery.PreviewCandidateCount,
                PreviewMethodCount = methodGroups.Sum(x => x.Methods.Count),
                MethodSignatureCount = methodSignatures.Count,
                PreviewViewFound = previewViewFound,
                PreviewViewModelFound = previewViewModelFound,
                ScenePreviewViewModelFound = scenePreviewViewModelFound,
                GetBitmapMethodFound = getBitmapMethodFound,
                GetBitmapInvocationSucceeded = false,
                CaptureSucceeded = false,
                PreviewViewModelTypeName = discovery.PreviewViewModelTypeName,
                GetBitmapReturnTypeName = discovery.GetBitmapReturnTypeName,
                GetBitmapSignatureCategory = discovery.GetBitmapSignatureCategory,
                GetBitmapParameterCount = discovery.GetBitmapParameterCount,
                GetBitmapParameterTypes = discovery.GetBitmapParameterTypes,
                GetBitmapInvocationCandidates = invocationCandidates,
                NextRecommendedCall = discovery.NextRecommendedCall,
                FalseInvocationSucceeded = false,
                TrueInvocationSucceeded = false,
                FalseCaptureSucceeded = false,
                TrueCaptureSucceeded = false,
                FalseBitmapSaveSucceeded = false,
                TrueBitmapSaveSucceeded = false,
                FalseFailureKind = null,
                TrueFailureKind = null,
                FalseHasAlpha = null,
                TrueHasAlpha = null,
                FalseWidth = null,
                FalseHeight = null,
                FalsePixelFormat = null,
                FalseFileSize = null,
                FalseDurationMs = null,
                FalseSavedFilePath = null,
                TrueWidth = null,
                TrueHeight = null,
                TruePixelFormat = null,
                TrueFileSize = null,
                TrueDurationMs = null,
                TrueSavedFilePath = null,
                BitmapWidth = null,
                BitmapHeight = null,
                BitmapPixelFormat = null,
                BitmapSaveSucceeded = false,
                SavedFilePath = null,
                FailureReason = discoverySucceeded ? null : "No preview-related windows, visual tree nodes, data contexts, or methods were discovered.",
                DiscoveryFailureReason = discovery.DiscoveryFailureReason,
                SignatureFailureReason = discovery.SignatureFailureReason,
                InvocationFailureReason = discovery.InvocationFailureReason,
                BitmapSaveFailureReason = discovery.BitmapSaveFailureReason,
                OverallFailureReason = discovery.OverallFailureReason,
                DiagnosticWindowsPath = windowsJsonPath,
                DiagnosticVisualTreePath = visualTreeJsonPath,
                PreviewCandidatesPath = previewCandidatesJsonPath,
                PreviewMethodsPath = previewMethodsJsonPath,
                MethodSignaturesPath = previewMethodSignaturesJsonPath,
                CaptureResultPath = captureResultJsonPath,
                FalseCaptureResultPath = captureFalseJsonPath,
                TrueCaptureResultPath = captureTrueJsonPath,
                ComparisonPath = comparisonJsonPath,
                HistoryPath = historyJsonPath,
                Duration = watch.Elapsed,
            };

            var target = discovery.TargetInstance is not null && discovery.TargetType is not null
                ? new CandidateDiscovery(
                    discovery.TargetType,
                    discovery.TargetInstance,
                    new PreviewBitmapCandidateInfo
                    {
                        FoundType = discovery.TargetType.FullName,
                        DataContextType = discovery.PreviewViewModelTypeName,
                    })
                : null;
            var targetMethod = discovery.TargetMethod;

            var falseCapture = target is null || targetMethod is null
                ? CreateFailureCaptureResult(discovery.FailureReason ?? "GetBitmap target not resolved", false, watch.Elapsed)
                : InvokeAndCapture(target, targetMethod, false, previewFalsePngPath);

            var trueCapture = target is null || targetMethod is null
                ? CreateFailureCaptureResult(discovery.FailureReason ?? "GetBitmap target not resolved", true, watch.Elapsed)
                : InvokeAndCapture(target, targetMethod, true, previewTruePngPath);

            var comparison = CompareCaptures(falseCapture, trueCapture);
            var selectedCapture = SelectPreferredCapture(falseCapture, trueCapture, comparison);

            result = ApplyInvocationResults(result, falseCapture, trueCapture, selectedCapture, comparison);
            WriteAllOutputs(result, falseCapture, trueCapture, selectedCapture, comparison);

            logger.Info(
                $"Capture diagnostics completed. false={result.FalseCaptureSucceeded}, true={result.TrueCaptureSucceeded}, preferred={comparison.PreferredCall}, signatures={result.MethodSignatureCount}, candidates={result.PreviewCandidateCount}, methods={result.PreviewMethodCount}");

            return result;
        }
        catch (Exception ex)
        {
            logger.Error(ex, "Preview Bitmap Diagnostics failed during discovery.");
            var result = CreateFailureResult(ex.Message, watch.Elapsed);
            var failedFalseCapture = CreateFailureCaptureResult(ex.Message, false, watch.Elapsed);
            var failedTrueCapture = CreateFailureCaptureResult(ex.Message, true, watch.Elapsed);
            var failedComparison = CompareCaptures(failedFalseCapture, failedTrueCapture);
            WriteAllOutputs(result, failedFalseCapture, failedTrueCapture, SelectPreferredCapture(failedFalseCapture, failedTrueCapture, failedComparison), failedComparison);
            return result;
        }
    }

    private PreviewBitmapCaptureResult InvokeAndCapture(CandidateDiscovery target, MethodInfo targetMethod, bool alpha, string outputPath)
    {
        var invocationWatch = Stopwatch.StartNew();
        logger.Info($"GetBitmap invocation started. alpha={alpha}");

        object? bitmapObject = null;
        try
        {
            bitmapObject = targetMethod.Invoke(target.Instance, [alpha]);
        }
        catch (TargetInvocationException ex)
        {
            var inner = ex.InnerException ?? ex;
            logger.Info("GetBitmap invocation failed");
            return CreateInvocationFailureCaptureResult(targetMethod, alpha, invocationWatch.Elapsed, "InvocationException", inner.GetType().FullName, inner.Message);
        }
        catch (Exception ex)
        {
            logger.Info("GetBitmap invocation failed");
            return CreateInvocationFailureCaptureResult(targetMethod, alpha, invocationWatch.Elapsed, "InvocationException", ex.GetType().FullName, ex.Message);
        }

        if (bitmapObject is null)
        {
            logger.Info("GetBitmap invocation succeeded");
            logger.Info("Bitmap analyzed");
            return CreateInvocationFailureCaptureResult(targetMethod, alpha, invocationWatch.Elapsed, "NullReturned", null, "GetBitmap returned null");
        }

        if (!TryConvertBitmap(bitmapObject, out var bitmapSource, out var width, out var height, out var pixelFormat, out var hasAlpha, out var conversionError))
        {
            logger.Info("GetBitmap invocation succeeded");
            logger.Info("Bitmap analyzed");
            return CreateInvocationFailureCaptureResult(
                targetMethod,
                alpha,
                invocationWatch.Elapsed,
                "BitmapConversionFailed",
                null,
                conversionError ?? $"Unsupported bitmap type: {bitmapObject.GetType().FullName}",
                bitmapObject.GetType().FullName);
        }

        logger.Info("GetBitmap invocation succeeded");
        logger.Info("Bitmap analyzed");

        var saveSucceeded = false;
        long? fileSize = null;
        string? saveError = null;
        try
        {
            saveSucceeded = SavePng(bitmapSource, outputPath, out fileSize);
            if (saveSucceeded)
            {
                logger.Info("Bitmap saved");
            }
        }
        catch (Exception ex)
        {
            saveError = $"PNG save failed: {ex.GetType().FullName}: {ex.Message}";
            logger.Info("Bitmap saved");
        }

        var captureSucceeded = saveSucceeded && width > 0 && height > 0;
        return new PreviewBitmapCaptureResult
        {
            InvocationSucceeded = true,
            ReturnType = targetMethod.ReturnType.FullName,
            BitmapType = bitmapObject.GetType().FullName,
            Width = width,
            Height = height,
            PixelFormat = pixelFormat,
            SaveSucceeded = saveSucceeded,
            FileSize = fileSize,
            SavedFilePath = saveSucceeded ? outputPath : null,
            ExceptionType = null,
            ExceptionMessage = saveError,
            DurationMs = invocationWatch.Elapsed.TotalMilliseconds,
            CaptureSucceeded = captureSucceeded,
            HasAlpha = hasAlpha,
            FailureKind = saveSucceeded ? "None" : "PngSaveFailed",
        };
    }

    private static bool TryConvertBitmap(
        object bitmapObject,
        out BitmapSource bitmapSource,
        out int width,
        out int height,
        out string? pixelFormat,
        out bool hasAlpha,
        out string? error)
    {
        if (bitmapObject is BitmapSource bitmap)
        {
            bitmapSource = bitmap.CloneCurrentValue();
            if (!bitmapSource.IsFrozen && bitmapSource.CanFreeze)
            {
                bitmapSource.Freeze();
            }

            width = bitmapSource.PixelWidth;
            height = bitmapSource.PixelHeight;
            pixelFormat = bitmapSource.Format.ToString();
            hasAlpha = HasAlphaFromPixelFormat(pixelFormat);
            error = null;
            return true;
        }

        if (bitmapObject is Bitmap drawingBitmap)
        {
            try
            {
                width = drawingBitmap.Width;
                height = drawingBitmap.Height;
                pixelFormat = drawingBitmap.PixelFormat.ToString();
                hasAlpha = HasAlphaFromPixelFormat(pixelFormat);
                bitmapSource = ConvertDrawingBitmapToBitmapSource(drawingBitmap);
                error = null;
                return true;
            }
            catch (Exception ex)
            {
                bitmapSource = null!;
                width = 0;
                height = 0;
                pixelFormat = null;
                hasAlpha = false;
                error = ex.Message;
                return false;
            }
        }

        bitmapSource = null!;
        width = 0;
        height = 0;
        pixelFormat = null;
        hasAlpha = false;
        error = $"Unsupported bitmap type: {bitmapObject.GetType().FullName}";
        return false;
    }

    private static BitmapSource ConvertDrawingBitmapToBitmapSource(Bitmap drawingBitmap)
    {
        using var memory = new MemoryStream();
        drawingBitmap.Save(memory, System.Drawing.Imaging.ImageFormat.Bmp);
        memory.Position = 0;

        var decoder = BitmapDecoder.Create(memory, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
        var frame = decoder.Frames[0];
        var source = BitmapFrame.Create(frame);
        if (!source.IsFrozen && source.CanFreeze)
        {
            source.Freeze();
        }

        return source;
    }

    private static bool SavePng(BitmapSource bitmapSource, string outputPath, out long? fileSize)
    {
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmapSource));

        using var stream = File.Open(outputPath, FileMode.Create, FileAccess.Write, FileShare.None);
        encoder.Save(stream);
        fileSize = new FileInfo(outputPath).Length;
        return true;
    }

    private static int DetermineDiscoveryLevel(
        int windowCount,
        int visualTreeCount,
        int candidateCount,
        bool previewTargetFound,
        bool getBitmapFound,
        bool methodSignaturesFound,
        bool invocationCandidatesFound,
        bool autoInvocationPerformed)
    {
        if (windowCount == 0)
        {
            return 0;
        }

        if (visualTreeCount == 0)
        {
            return 1;
        }

        if (candidateCount == 0)
        {
            return 2;
        }

        if (!previewTargetFound)
        {
            return 3;
        }

        if (!getBitmapFound)
        {
            return 4;
        }

        if (!methodSignaturesFound)
        {
            return 5;
        }

        if (!invocationCandidatesFound)
        {
            return 6;
        }

        return autoInvocationPerformed ? 8 : 7;
    }

    private static PreviewBitmapDiagnosticsResult ApplyInvocationResults(
        PreviewBitmapDiagnosticsResult result,
        PreviewBitmapCaptureResult falseCapture,
        PreviewBitmapCaptureResult trueCapture,
        PreviewBitmapCaptureResult preferredCapture,
        PreviewBitmapComparisonResult comparison)
    {
        return new PreviewBitmapDiagnosticsResult
        {
            DiscoverySucceeded = result.DiscoverySucceeded,
            DiscoveryLevelReached = result.DiscoveryLevelReached,
            WindowCount = result.WindowCount,
            VisualTreeElementCount = result.VisualTreeElementCount,
            PreviewCandidateCount = result.PreviewCandidateCount,
            PreviewMethodCount = result.PreviewMethodCount,
            MethodSignatureCount = result.MethodSignatureCount,
            PreviewViewFound = result.PreviewViewFound,
            PreviewViewModelFound = result.PreviewViewModelFound,
            ScenePreviewViewModelFound = result.ScenePreviewViewModelFound,
            GetBitmapMethodFound = result.GetBitmapMethodFound,
            GetBitmapSignatureCategory = result.GetBitmapSignatureCategory,
            GetBitmapParameterCount = result.GetBitmapParameterCount,
            GetBitmapParameterTypes = result.GetBitmapParameterTypes,
            GetBitmapInvocationCandidates = result.GetBitmapInvocationCandidates,
            NextRecommendedCall = comparison.PreferredCall ?? result.NextRecommendedCall,
            FalseInvocationSucceeded = falseCapture.InvocationSucceeded,
            TrueInvocationSucceeded = trueCapture.InvocationSucceeded,
            FalseCaptureSucceeded = falseCapture.CaptureSucceeded,
            TrueCaptureSucceeded = trueCapture.CaptureSucceeded,
            FalseBitmapSaveSucceeded = falseCapture.SaveSucceeded,
            TrueBitmapSaveSucceeded = trueCapture.SaveSucceeded,
            FalseFailureKind = falseCapture.FailureKind,
            TrueFailureKind = trueCapture.FailureKind,
            FalseHasAlpha = falseCapture.HasAlpha,
            TrueHasAlpha = trueCapture.HasAlpha,
            FalseWidth = falseCapture.Width,
            FalseHeight = falseCapture.Height,
            FalsePixelFormat = falseCapture.PixelFormat,
            FalseFileSize = falseCapture.FileSize,
            FalseDurationMs = falseCapture.DurationMs,
            FalseSavedFilePath = falseCapture.SavedFilePath,
            TrueWidth = trueCapture.Width,
            TrueHeight = trueCapture.Height,
            TruePixelFormat = trueCapture.PixelFormat,
            TrueFileSize = trueCapture.FileSize,
            TrueDurationMs = trueCapture.DurationMs,
            TrueSavedFilePath = trueCapture.SavedFilePath,
            DiscoveryFailureReason = result.DiscoveryFailureReason,
            SignatureFailureReason = result.SignatureFailureReason,
            InvocationFailureReason = comparison.Reason,
            BitmapSaveFailureReason = preferredCapture.SaveSucceeded ? null : preferredCapture.ExceptionMessage,
            CaptureFailureReason = preferredCapture.CaptureSucceeded ? null : preferredCapture.ExceptionMessage ?? comparison.Reason,
            CaptureFailureKind = preferredCapture.FailureKind,
            OverallFailureReason = preferredCapture.CaptureSucceeded ? null : comparison.Reason ?? result.FailureReason,
            GetBitmapInvocationSucceeded = falseCapture.InvocationSucceeded || trueCapture.InvocationSucceeded,
            CaptureSucceeded = preferredCapture.CaptureSucceeded,
            PreviewViewModelTypeName = result.PreviewViewModelTypeName,
            GetBitmapReturnTypeName = result.GetBitmapReturnTypeName,
            BitmapWidth = preferredCapture.Width,
            BitmapHeight = preferredCapture.Height,
            BitmapPixelFormat = preferredCapture.PixelFormat,
            BitmapSaveSucceeded = preferredCapture.SaveSucceeded,
            SavedFilePath = preferredCapture.SavedFilePath,
            FailureReason = preferredCapture.CaptureSucceeded ? null : comparison.Reason ?? result.FailureReason,
            DiagnosticWindowsPath = result.DiagnosticWindowsPath,
            DiagnosticVisualTreePath = result.DiagnosticVisualTreePath,
            PreviewCandidatesPath = result.PreviewCandidatesPath,
            PreviewMethodsPath = result.PreviewMethodsPath,
            MethodSignaturesPath = result.MethodSignaturesPath,
            CaptureResultPath = result.CaptureResultPath,
            FalseCaptureResultPath = result.FalseCaptureResultPath,
            TrueCaptureResultPath = result.TrueCaptureResultPath,
            ComparisonPath = result.ComparisonPath,
            HistoryPath = result.HistoryPath,
            Duration = result.Duration,
        };
    }

    private static CandidateDiscovery? ResolveInvocationTarget(IReadOnlyList<CandidateDiscovery> candidates)
    {
        var ordered = candidates
            .Where(candidate => candidate.Instance is not null)
            .OrderByDescending(candidate => ContainsKeyword(candidate.Info.FoundType, "PreviewViewModel") || ContainsKeyword(candidate.Info.DataContextType, "PreviewViewModel"))
            .ThenByDescending(candidate => ContainsKeyword(candidate.Info.FoundType, "ScenePreviewViewModel") || ContainsKeyword(candidate.Info.DataContextType, "ScenePreviewViewModel"))
            .ThenByDescending(candidate => IsPreviewViewType(candidate.Info.FoundType) || IsPreviewViewType(candidate.Info.ControlType))
            .ToArray();

        foreach (var candidate in ordered)
        {
            var method = candidate.Type
                .GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
                .Any(methodInfo => methodInfo.Name == "GetBitmap");
            if (method)
            {
                return candidate;
            }
        }

        return null;
    }

    private static MethodInfo? ResolveGetBitmapMethod(Type? type)
    {
        if (type is null)
        {
            return null;
        }

        return type.GetMethod(
            "GetBitmap",
            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance,
            binder: null,
            types: [typeof(bool)],
            modifiers: null);
    }

    private static IReadOnlyList<PreviewBitmapWindowInfo> CollectWindows()
    {
        var windows = global::System.Windows.Application.Current?.Windows.OfType<Window>() ?? [];
        return windows.Select(window => new PreviewBitmapWindowInfo
        {
            Type = window.GetType().FullName,
            Name = window.Name,
            Title = window.Title,
            DataContextType = GetDataContext(window)?.GetType().FullName,
            IsActive = window.IsActive,
            Visibility = window.Visibility.ToString(),
        }).ToArray();
    }

    private static IReadOnlyList<PreviewBitmapVisualTreeInfo> CollectVisualTree()
    {
        var collected = new List<PreviewBitmapVisualTreeInfo>();
        var currentWindows = global::System.Windows.Application.Current?.Windows.OfType<Window>().ToArray() ?? [];
        foreach (var window in currentWindows)
        {
            TraverseVisualTree(window, collected, window.GetType().FullName, 0, null);
        }

        return collected;
    }

    private static void TraverseVisualTree(
        DependencyObject element,
        ICollection<PreviewBitmapVisualTreeInfo> collected,
        string? windowType,
        int depth,
        string? parentType)
    {
        collected.Add(new PreviewBitmapVisualTreeInfo
        {
            WindowType = windowType,
            ControlType = element.GetType().FullName,
            ControlName = GetElementName(element),
            DataContextType = GetDataContext(element)?.GetType().FullName,
            Depth = depth,
            ParentType = parentType,
        });

        var childCount = SafeGetChildrenCount(element);
        for (var i = 0; i < childCount; i++)
        {
            var child = SafeGetChild(element, i);
            if (child is null)
            {
                continue;
            }

            TraverseVisualTree(child, collected, windowType, depth + 1, element.GetType().FullName);
        }
    }

    private static int SafeGetChildrenCount(DependencyObject element)
    {
        try
        {
            return VisualTreeHelper.GetChildrenCount(element);
        }
        catch
        {
            return 0;
        }
    }

    private static DependencyObject? SafeGetChild(DependencyObject element, int index)
    {
        try
        {
            return VisualTreeHelper.GetChild(element, index);
        }
        catch
        {
            return null;
        }
    }

    private static string? GetElementName(DependencyObject element)
    {
        if (element is FrameworkElement frameworkElement)
        {
            return string.IsNullOrWhiteSpace(frameworkElement.Name) ? null : frameworkElement.Name;
        }

        if (element is FrameworkContentElement contentElement)
        {
            return string.IsNullOrWhiteSpace(contentElement.Name) ? null : contentElement.Name;
        }

        return null;
    }

    private static object? GetDataContext(DependencyObject element)
    {
        if (element is FrameworkElement frameworkElement)
        {
            return frameworkElement.DataContext;
        }

        if (element is FrameworkContentElement contentElement)
        {
            return contentElement.DataContext;
        }

        return null;
    }

    private static IReadOnlyList<CandidateDiscovery> DiscoverCandidates()
    {
        var discoveries = new List<CandidateDiscovery>();

        foreach (var window in global::System.Windows.Application.Current?.Windows.OfType<Window>() ?? [])
        {
            DiscoverFromInstance(
                discoveries,
                window,
                window.GetType(),
                "WindowType",
                window.GetType().FullName,
                window.Name,
                window.GetType().FullName,
                null,
                0);

            var dataContext = GetDataContext(window);
            if (dataContext is not null)
            {
                DiscoverFromInstance(
                    discoveries,
                    dataContext,
                    dataContext.GetType(),
                    "DataContextType",
                    window.GetType().FullName,
                    window.Name,
                    window.GetType().FullName,
                    dataContext.GetType().FullName,
                    0);
            }

            DiscoverTreeCandidates(window, discoveries, 0, window.GetType().FullName);
        }

        return discoveries;
    }

    private static void DiscoverTreeCandidates(
        DependencyObject element,
        ICollection<CandidateDiscovery> discoveries,
        int depth,
        string? parentType)
    {
        var elementType = element.GetType();
        var controlName = GetElementName(element);
        var dataContext = GetDataContext(element);
        var dataContextType = dataContext?.GetType();

        DiscoverFromInstance(
            discoveries,
            element,
            elementType,
            "ControlType",
            parentType,
            controlName,
            elementType.FullName,
            dataContextType?.FullName,
            depth);

        if (dataContextType is not null)
        {
            DiscoverFromInstance(
                discoveries,
                dataContext,
                dataContextType,
                "DataContextType",
                elementType.FullName,
                controlName,
                elementType.FullName,
                dataContextType.FullName,
                depth);
        }

        var childCount = SafeGetChildrenCount(element);
        for (var i = 0; i < childCount; i++)
        {
            var child = SafeGetChild(element, i);
            if (child is null)
            {
                continue;
            }

            DiscoverTreeCandidates(child, discoveries, depth + 1, elementType.FullName);
        }
    }

    private static void DiscoverFromInstance(
        ICollection<CandidateDiscovery> discoveries,
        object? instance,
        Type type,
        string sourceKind,
        string? parent,
        string? controlName,
        string? controlType,
        string? dataContextType,
        int? depth)
    {
        var fullName = type.FullName ?? type.Name;
        if (!ContainsKeyword(fullName) && !ContainsKeyword(dataContextType))
        {
            return;
        }

        discoveries.Add(new CandidateDiscovery(
            Type: type,
            Instance: instance,
            Info: new PreviewBitmapCandidateInfo
            {
                FoundType = fullName,
                Assembly = type.Assembly.FullName,
                Parent = parent,
                DataContext = dataContextType,
                SourceKind = sourceKind,
                ControlType = controlType,
                ControlName = controlName,
                DataContextType = dataContextType,
                Depth = depth,
            }));
    }

    private static IReadOnlyList<PreviewBitmapMethodGroupInfo> DiscoverMethods(IReadOnlyList<CandidateDiscovery> candidates)
    {
        var groups = new List<PreviewBitmapMethodGroupInfo>();
        var seenTypes = new HashSet<string>(StringComparer.Ordinal);
        foreach (var candidate in candidates)
        {
            var type = candidate.Type;
            var key = type.AssemblyQualifiedName ?? type.FullName ?? type.Name;
            if (!seenTypes.Add(key))
            {
                continue;
            }

            var methods = type
                .GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
                .Where(method => !method.IsSpecialName)
                .Select(method => new PreviewBitmapMethodInfo
                {
                    Name = method.Name,
                    ReturnType = method.ReturnType.FullName ?? method.ReturnType.Name,
                    ParameterCount = method.GetParameters().Length,
                    ParameterTypes = method.GetParameters()
                        .Select(parameter => parameter.ParameterType.FullName ?? parameter.ParameterType.Name)
                        .ToArray(),
                    IsPublic = method.IsPublic,
                    IsPrivate = method.IsPrivate,
                    IsFamily = method.IsFamily,
                    IsAssembly = method.IsAssembly,
                    IsStatic = method.IsStatic,
                    MatchKeyword = GetMatchedKeyword(method.Name),
                })
                .OrderByDescending(method => method.MatchKeyword is not null)
                .ThenBy(method => method.Name, StringComparer.OrdinalIgnoreCase)
                .ToArray();

            groups.Add(new PreviewBitmapMethodGroupInfo
            {
                Type = type.FullName ?? type.Name,
                Assembly = type.Assembly.FullName,
                DataContextType = candidate.Info.DataContextType,
                Methods = methods,
            });
        }

        return groups;
    }

    private static IReadOnlyList<PreviewBitmapMethodSignatureInfo> DiscoverMethodSignatures(IReadOnlyList<CandidateDiscovery> candidates)
    {
        var signatures = new List<PreviewBitmapMethodSignatureInfo>();
        var seenTypes = new HashSet<string>(StringComparer.Ordinal);

        foreach (var candidate in candidates)
        {
            var type = candidate.Type;
            var key = type.AssemblyQualifiedName ?? type.FullName ?? type.Name;
            if (!seenTypes.Add(key))
            {
                continue;
            }

            var methods = type
                .GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static)
                .Where(method => !method.IsSpecialName && IsSignatureTargetMethod(method.Name))
                .Select(method =>
                {
                    var parameters = method.GetParameters()
                        .Select(parameter => new PreviewBitmapMethodSignatureParameterInfo
                        {
                            Name = parameter.Name,
                            Type = parameter.ParameterType.FullName ?? parameter.ParameterType.Name,
                            HasDefaultValue = parameter.HasDefaultValue,
                            DefaultValue = parameter.HasDefaultValue ? FormatDefaultValue(parameter.DefaultValue) : null,
                        })
                        .ToArray();

                    return new PreviewBitmapMethodSignatureInfo
                    {
                        MethodName = method.Name,
                        DeclaringType = method.DeclaringType?.FullName ?? type.FullName ?? type.Name,
                        ReturnType = method.ReturnType.FullName ?? method.ReturnType.Name,
                        ParameterCount = parameters.Length,
                        Parameters = parameters,
                        IsPublic = method.IsPublic,
                        IsStatic = method.IsStatic,
                        MatchKeyword = GetMatchedKeyword(method.Name),
                        Category = method.Name.Equals("GetBitmap", StringComparison.OrdinalIgnoreCase)
                            ? ClassifyGetBitmapSignature(parameters)
                            : null,
                        InvocationCandidates = method.Name.Equals("GetBitmap", StringComparison.OrdinalIgnoreCase)
                            ? GenerateInvocationCandidates(parameters)
                            : [],
                    };
                })
                .OrderByDescending(method => method.MatchKeyword is not null)
                .ThenBy(method => method.MethodName, StringComparer.OrdinalIgnoreCase)
                .ThenBy(method => method.DeclaringType, StringComparer.OrdinalIgnoreCase)
                .ToArray();

            signatures.AddRange(methods);
        }

        return signatures;
    }

    private PreviewBitmapDiagnosticsResult CreateFailureResult(string failureReason, TimeSpan duration)
        => new()
        {
            DiscoverySucceeded = false,
            DiscoveryLevelReached = 0,
            WindowCount = 0,
            VisualTreeElementCount = 0,
            PreviewCandidateCount = 0,
            PreviewMethodCount = 0,
            MethodSignatureCount = 0,
            PreviewViewFound = false,
            PreviewViewModelFound = false,
            ScenePreviewViewModelFound = false,
            GetBitmapMethodFound = false,
            GetBitmapSignatureCategory = null,
            GetBitmapParameterCount = null,
            GetBitmapParameterTypes = [],
            GetBitmapInvocationCandidates = [],
            NextRecommendedCall = null,
            FalseInvocationSucceeded = false,
            TrueInvocationSucceeded = false,
            FalseCaptureSucceeded = false,
            TrueCaptureSucceeded = false,
            FalseBitmapSaveSucceeded = false,
            TrueBitmapSaveSucceeded = false,
            FalseFailureKind = null,
            TrueFailureKind = null,
            FalseHasAlpha = null,
            TrueHasAlpha = null,
            FalseWidth = null,
            FalseHeight = null,
            FalsePixelFormat = null,
            FalseFileSize = null,
            FalseDurationMs = null,
            FalseSavedFilePath = null,
            TrueWidth = null,
            TrueHeight = null,
            TruePixelFormat = null,
            TrueFileSize = null,
            TrueDurationMs = null,
            TrueSavedFilePath = null,
            DiscoveryFailureReason = failureReason,
            SignatureFailureReason = null,
            InvocationFailureReason = null,
            BitmapSaveFailureReason = null,
            CaptureFailureReason = failureReason,
            CaptureFailureKind = "DiscoveryException",
            OverallFailureReason = failureReason,
            GetBitmapInvocationSucceeded = false,
            CaptureSucceeded = false,
            PreviewViewModelTypeName = null,
            GetBitmapReturnTypeName = null,
            BitmapWidth = null,
            BitmapHeight = null,
            BitmapPixelFormat = null,
            BitmapSaveSucceeded = false,
            SavedFilePath = null,
            FailureReason = failureReason,
            DiagnosticWindowsPath = windowsJsonPath,
            DiagnosticVisualTreePath = visualTreeJsonPath,
            PreviewCandidatesPath = previewCandidatesJsonPath,
            PreviewMethodsPath = previewMethodsJsonPath,
            MethodSignaturesPath = previewMethodSignaturesJsonPath,
            CaptureResultPath = captureResultJsonPath,
            FalseCaptureResultPath = captureFalseJsonPath,
            TrueCaptureResultPath = captureTrueJsonPath,
            ComparisonPath = comparisonJsonPath,
            HistoryPath = historyJsonPath,
            Duration = duration,
        };

    private void WriteAllOutputs(
        PreviewBitmapDiagnosticsResult result,
        PreviewBitmapCaptureResult falseCapture,
        PreviewBitmapCaptureResult trueCapture,
        PreviewBitmapCaptureResult preferredCapture,
        PreviewBitmapComparisonResult comparison)
    {
        try
        {
            Directory.CreateDirectory(diagnosticsDirectory);
            WriteJson(windowsJsonPath, CollectWindows());
            WriteJson(visualTreeJsonPath, CollectVisualTree());
            WriteJson(previewCandidatesJsonPath, DiscoverCandidates().Select(x => x.Info).ToArray());
            WriteJson(previewMethodsJsonPath, DiscoverMethods(DiscoverCandidates()));
            WriteJson(previewMethodSignaturesJsonPath, DiscoverMethodSignatures(DiscoverCandidates()));
            WriteJson(resultJsonPath, result);
            WriteJson(captureFalseJsonPath, falseCapture);
            WriteJson(captureTrueJsonPath, trueCapture);
            WriteJson(captureResultJsonPath, preferredCapture);
            WriteJson(comparisonJsonPath, comparison);
            EnsurePreferredPreviewCopy(preferredCapture);
            AppendHistory(result, falseCapture, trueCapture, comparison);
        }
        catch (Exception ex)
        {
            logger.Error(ex, "Failed to save preview diagnostics outputs.");
        }
    }

    private void AppendHistory(
        PreviewBitmapDiagnosticsResult result,
        PreviewBitmapCaptureResult falseCapture,
        PreviewBitmapCaptureResult trueCapture,
        PreviewBitmapComparisonResult comparison)
    {
        try
        {
            List<PreviewBitmapHistoryEntry> history;
            if (File.Exists(historyJsonPath))
            {
                var existing = File.ReadAllText(historyJsonPath);
                history = JsonSerializer.Deserialize<List<PreviewBitmapHistoryEntry>>(existing) ?? [];
            }
            else
            {
                history = [];
            }

            history.Add(new PreviewBitmapHistoryEntry
            {
                Timestamp = DateTimeOffset.Now,
                FalseCaptureSucceeded = falseCapture.CaptureSucceeded,
                TrueCaptureSucceeded = trueCapture.CaptureSucceeded,
                FalseInvocationSucceeded = falseCapture.InvocationSucceeded,
                TrueInvocationSucceeded = trueCapture.InvocationSucceeded,
                FalseBitmapSaveSucceeded = falseCapture.SaveSucceeded,
                TrueBitmapSaveSucceeded = trueCapture.SaveSucceeded,
                PreferredCall = comparison.PreferredCall,
                Reason = comparison.Reason,
            });

            WriteJson(historyJsonPath, history);
        }
        catch (Exception ex)
        {
            logger.Error(ex, "Failed to update preview diagnostics history.");
        }
    }

    private void WriteJson<T>(string path, T value)
    {
        try
        {
            Directory.CreateDirectory(diagnosticsDirectory);
            var json = JsonSerializer.Serialize(value, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(path, json);
        }
        catch (Exception ex)
        {
            logger.Error(ex, $"Failed to save diagnostics output. Path={path}");
        }
    }

    private static bool ContainsKeyword(string? value)
        => !string.IsNullOrWhiteSpace(value) && CandidateKeywords.Any(keyword => value.Contains(keyword, StringComparison.OrdinalIgnoreCase));

    private static bool ContainsKeyword(string? value, string keyword)
        => !string.IsNullOrWhiteSpace(value) && value.Contains(keyword, StringComparison.OrdinalIgnoreCase);

    private static string? GetMatchedKeyword(string methodName)
        => MethodKeywords.FirstOrDefault(keyword => methodName.Contains(keyword, StringComparison.OrdinalIgnoreCase));

    private static bool IsSignatureTargetMethod(string methodName)
        => MethodKeywords.Any(keyword => methodName.Contains(keyword, StringComparison.OrdinalIgnoreCase));

    private static string? FormatDefaultValue(object? value)
    {
        if (value is null)
        {
            return null;
        }

        return value switch
        {
            string text => text,
            char ch => ch.ToString(),
            bool boolValue => boolValue ? "true" : "false",
            Enum enumValue => enumValue.ToString(),
            IFormattable formattable => formattable.ToString(null, CultureInfo.InvariantCulture),
            _ => value.ToString(),
        };
    }

    private static string ClassifyGetBitmapSignature(IReadOnlyList<PreviewBitmapMethodSignatureParameterInfo> parameters)
    {
        if (parameters.Count == 0)
        {
            return "NoParameters";
        }

        if (parameters.All(parameter => parameter.HasDefaultValue))
        {
            return "OptionalParametersOnly";
        }

        if (parameters.Any(parameter => !parameter.HasDefaultValue))
        {
            return "RequiredParameters";
        }

        return "Unknown";
    }

    private static IReadOnlyList<string> GenerateInvocationCandidates(IReadOnlyList<PreviewBitmapMethodSignatureParameterInfo> parameters)
    {
        var candidates = new List<string>();
        var parameterCount = parameters.Count;

        if (parameterCount == 0)
        {
            candidates.Add("GetBitmap()");
            return candidates;
        }

        if (parameterCount == 1 && string.Equals(parameters[0].Type, typeof(bool).FullName, StringComparison.Ordinal))
        {
            candidates.Add("GetBitmap(false)");
            candidates.Add("GetBitmap(true)");
            return candidates;
        }

        var defaultInvocation = $"GetBitmap({string.Join(", ", parameters.Select(BuildDefaultArgument))})";
        candidates.Add(defaultInvocation);

        if (parameters.All(parameter => parameter.HasDefaultValue))
        {
            candidates.Insert(0, "GetBitmap()");
        }

        if (LooksLikeWidthHeight(parameters))
        {
            candidates.Add("GetBitmap(320, 180)");
            candidates.Add("GetBitmap(640, 360)");
        }
        else if (parameterCount == 1 && IsNumericType(parameters[0].Type))
        {
            candidates.Add("GetBitmap(320)");
            candidates.Add("GetBitmap(640)");
        }

        return candidates.Distinct(StringComparer.Ordinal).ToArray();
    }

    private static string BuildDefaultArgument(PreviewBitmapMethodSignatureParameterInfo parameter)
    {
        if (parameter.HasDefaultValue)
        {
            return parameter.DefaultValue is null ? "default" : FormatInvocationLiteral(parameter.DefaultValue);
        }

        return "default";
    }

    private static string FormatInvocationLiteral(string value)
    {
        if (int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out _))
        {
            return value;
        }

        if (double.TryParse(value, NumberStyles.Float | NumberStyles.AllowThousands, CultureInfo.InvariantCulture, out _))
        {
            return value;
        }

        if (bool.TryParse(value, out var boolValue))
        {
            return boolValue ? "true" : "false";
        }

        if (string.Equals(value, "null", StringComparison.OrdinalIgnoreCase))
        {
            return "null";
        }

        return $"\"{value.Replace("\"", "\\\"")}\"";
    }

    private static bool LooksLikeWidthHeight(IReadOnlyList<PreviewBitmapMethodSignatureParameterInfo> parameters)
    {
        if (parameters.Count != 2)
        {
            return false;
        }

        var first = parameters[0];
        var second = parameters[1];
        return IsNumericType(first.Type) && IsNumericType(second.Type)
            && LooksLikeWidth(first.Name, first.Type)
            && LooksLikeHeight(second.Name, second.Type);
    }

    private static bool LooksLikeWidth(string? name, string? type)
        => ContainsKeyword(name, "width") || string.Equals(name, "w", StringComparison.OrdinalIgnoreCase) || IsNumericType(type);

    private static bool LooksLikeHeight(string? name, string? type)
        => ContainsKeyword(name, "height") || string.Equals(name, "h", StringComparison.OrdinalIgnoreCase) || IsNumericType(type);

    private static bool IsNumericType(string? type)
        => type is not null && (
            type == typeof(int).FullName ||
            type == typeof(long).FullName ||
            type == typeof(short).FullName ||
            type == typeof(uint).FullName ||
            type == typeof(ulong).FullName ||
            type == typeof(ushort).FullName ||
            type == typeof(byte).FullName ||
            type == typeof(float).FullName ||
            type == typeof(double).FullName ||
            type == typeof(decimal).FullName);

    private static PreviewBitmapMethodSignatureInfo? ResolveGetBitmapSignature(IReadOnlyList<PreviewBitmapMethodSignatureInfo> signatures)
    {
        return signatures
            .Where(signature => string.Equals(signature.MethodName, "GetBitmap", StringComparison.OrdinalIgnoreCase))
            .OrderBy(signature => signature.ParameterCount)
            .ThenBy(signature => signature.Category == "NoParameters" ? 0 : signature.Category == "OptionalParametersOnly" ? 1 : signature.Category == "RequiredParameters" ? 2 : 3)
            .FirstOrDefault();
    }

    private static bool IsPreviewViewType(string? value)
        => !string.IsNullOrWhiteSpace(value)
            && value.Contains("PreviewView", StringComparison.OrdinalIgnoreCase)
            && !value.Contains("PreviewViewModel", StringComparison.OrdinalIgnoreCase);

    private static bool HasAlphaFromPixelFormat(string? pixelFormat)
        => !string.IsNullOrWhiteSpace(pixelFormat) && (
            pixelFormat.Contains("Argb", StringComparison.OrdinalIgnoreCase) ||
            pixelFormat.Contains("PArgb", StringComparison.OrdinalIgnoreCase) ||
            pixelFormat.Contains("Bgra", StringComparison.OrdinalIgnoreCase) ||
            pixelFormat.Contains("Pbgra", StringComparison.OrdinalIgnoreCase) ||
            pixelFormat.Contains("Alpha", StringComparison.OrdinalIgnoreCase));

    private static PreviewBitmapCaptureResult CreateInvocationFailureCaptureResult(
        MethodInfo targetMethod,
        bool alpha,
        TimeSpan duration,
        string failureKind,
        string? exceptionType,
        string? exceptionMessage,
        string? bitmapType = null)
        => new()
        {
            InvocationSucceeded = false,
            ReturnType = targetMethod.ReturnType.FullName,
            BitmapType = bitmapType,
            Width = null,
            Height = null,
            PixelFormat = null,
            SaveSucceeded = false,
            FileSize = null,
            SavedFilePath = null,
            ExceptionType = exceptionType,
            ExceptionMessage = exceptionMessage,
            DurationMs = duration.TotalMilliseconds,
            CaptureSucceeded = false,
            HasAlpha = alpha,
            FailureKind = failureKind,
        };

    private static PreviewBitmapCaptureResult CreateFailureCaptureResult(string reason, bool alpha, TimeSpan duration)
        => new()
        {
            InvocationSucceeded = false,
            ReturnType = null,
            BitmapType = null,
            Width = null,
            Height = null,
            PixelFormat = null,
            SaveSucceeded = false,
            FileSize = null,
            SavedFilePath = null,
            ExceptionType = null,
            ExceptionMessage = reason,
            DurationMs = duration.TotalMilliseconds,
            CaptureSucceeded = false,
            HasAlpha = alpha,
            FailureKind = "InvocationNotAttempted",
        };

    private static PreviewBitmapComparisonResult CompareCaptures(PreviewBitmapCaptureResult falseCapture, PreviewBitmapCaptureResult trueCapture)
    {
        var falseSucceeded = IsCaptureSuccessful(falseCapture);
        var trueSucceeded = IsCaptureSuccessful(trueCapture);

        var preferredCall = (string?)null;
        var reason = (string?)null;

        if (falseSucceeded && trueSucceeded)
        {
            var falseArea = (falseCapture.Width ?? 0) * (falseCapture.Height ?? 0);
            var trueArea = (trueCapture.Width ?? 0) * (trueCapture.Height ?? 0);

            if (falseCapture.Width == trueCapture.Width && falseCapture.Height == trueCapture.Height)
            {
                var falseSize = falseCapture.FileSize ?? long.MaxValue;
                var trueSize = trueCapture.FileSize ?? long.MaxValue;
                if (falseSize < trueSize)
                {
                    preferredCall = "GetBitmap(false)";
                    reason = "Smaller image size and identical dimensions.";
                }
                else if (trueSize < falseSize)
                {
                    preferredCall = "GetBitmap(true)";
                    reason = "Smaller image size and identical dimensions.";
                }
                else if ((falseCapture.DurationMs, trueCapture.DurationMs) is (var fd, var td) && fd <= td)
                {
                    preferredCall = "GetBitmap(false)";
                    reason = "Same size and identical dimensions, faster invocation.";
                }
                else
                {
                    preferredCall = "GetBitmap(true)";
                    reason = "Same size and identical dimensions, faster invocation.";
                }
            }
            else if (falseArea >= trueArea)
            {
                preferredCall = "GetBitmap(false)";
                reason = "Larger resolved bitmap area.";
            }
            else
            {
                preferredCall = "GetBitmap(true)";
                reason = "Larger resolved bitmap area.";
            }
        }
        else if (falseSucceeded)
        {
            preferredCall = "GetBitmap(false)";
            reason = "Only GetBitmap(false) produced a valid bitmap.";
        }
        else if (trueSucceeded)
        {
            preferredCall = "GetBitmap(true)";
            reason = "Only GetBitmap(true) produced a valid bitmap.";
        }
        else
        {
            reason = $"False={falseCapture.FailureKind ?? "Unknown"}, True={trueCapture.FailureKind ?? "Unknown"}";
        }

        return new PreviewBitmapComparisonResult
        {
            FalseSucceeded = falseSucceeded,
            TrueSucceeded = trueSucceeded,
            FalseInvocationSucceeded = falseCapture.InvocationSucceeded,
            TrueInvocationSucceeded = trueCapture.InvocationSucceeded,
            FalseCaptureSucceeded = falseCapture.CaptureSucceeded,
            TrueCaptureSucceeded = trueCapture.CaptureSucceeded,
            FalseWidth = falseCapture.Width,
            FalseHeight = falseCapture.Height,
            FalsePixelFormat = falseCapture.PixelFormat,
            FalseHasAlpha = falseCapture.HasAlpha,
            FalseFileSize = falseCapture.FileSize,
            FalseDurationMs = falseCapture.DurationMs,
            FalseFailureKind = falseCapture.FailureKind,
            TrueWidth = trueCapture.Width,
            TrueHeight = trueCapture.Height,
            TruePixelFormat = trueCapture.PixelFormat,
            TrueHasAlpha = trueCapture.HasAlpha,
            TrueFileSize = trueCapture.FileSize,
            TrueDurationMs = trueCapture.DurationMs,
            TrueFailureKind = trueCapture.FailureKind,
            PreferredCall = preferredCall,
            Reason = reason,
        };
    }

    private static PreviewBitmapCaptureResult SelectPreferredCapture(
        PreviewBitmapCaptureResult falseCapture,
        PreviewBitmapCaptureResult trueCapture,
        PreviewBitmapComparisonResult comparison)
    {
        return comparison.PreferredCall switch
        {
            "GetBitmap(false)" => falseCapture,
            "GetBitmap(true)" => trueCapture,
            _ => falseCapture.CaptureSucceeded ? falseCapture : trueCapture,
        };
    }

    private void EnsurePreferredPreviewCopy(PreviewBitmapCaptureResult preferredCapture)
    {
        var sourcePath = preferredCapture.SavedFilePath;
        if (string.IsNullOrWhiteSpace(sourcePath) || !File.Exists(sourcePath))
        {
            return;
        }

        try
        {
            File.Copy(sourcePath, previewPngPath, overwrite: true);
        }
        catch (Exception ex)
        {
            logger.Error(ex, "Failed to copy preferred preview image.");
        }
    }

    private static bool IsCaptureSuccessful(PreviewBitmapCaptureResult captureResult)
        => captureResult.InvocationSucceeded &&
           captureResult.CaptureSucceeded &&
           captureResult.Width.GetValueOrDefault() > 0 &&
           captureResult.Height.GetValueOrDefault() > 0 &&
           captureResult.SaveSucceeded;

    private sealed record CandidateDiscovery(Type Type, object? Instance, PreviewBitmapCandidateInfo Info);
}
