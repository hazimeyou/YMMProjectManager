using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Windows;
using System.Windows.Media;
using YMMProjectManager.Application.Diagnostics;
using YMMProjectManager.Application.Thumbnails;
using YMMProjectManager.Infrastructure;

namespace YMMProjectManager.Infrastructure.Thumbnails;

public sealed class YmmPreviewDiscoveryService
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
    private readonly YmmPreviewDiscoveryOptions options;
    private readonly string failureDirectory;

    public YmmPreviewDiscoveryService(FileLogger logger, YmmPreviewDiscoveryOptions? options = null, string? failureDirectory = null)
    {
        this.logger = logger;
        this.options = options ?? new YmmPreviewDiscoveryOptions();
        this.failureDirectory = failureDirectory ?? Path.Combine(Path.GetTempPath(), "YMMProjectManager", "thumbnail-fast-generation");
    }

    public async Task<YmmPreviewDiscoveryResult> DiscoverAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var dispatcher = System.Windows.Application.Current?.Dispatcher;
        if (dispatcher is null)
        {
            var failed = CreateFailureResult("dispatcher unavailable", "DispatcherUnavailable", TimeSpan.Zero);
            WriteFailureReport(failed);
            return failed;
        }

        YmmPreviewDiscoveryResult? lastResult = null;
        for (var attempt = 1; attempt <= Math.Max(1, options.DiscoveryRetryCount); attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            lastResult = dispatcher.CheckAccess()
                ? DiscoverOnUiThread()
                : await dispatcher.InvokeAsync(
                        DiscoverOnUiThread,
                        System.Windows.Threading.DispatcherPriority.Background,
                        cancellationToken)
                    .Task
                    .ConfigureAwait(true);

            if (lastResult.DiscoverySucceeded || lastResult.PreviewViewModelFound || attempt >= Math.Max(1, options.DiscoveryRetryCount))
            {
                if (!lastResult.DiscoverySucceeded)
                {
                    WriteFailureReport(lastResult);
                }

                return lastResult;
            }

            logger.Info(
                $"Preview discovery retry scheduled. attempt={attempt}, failureStage={lastResult.FailureStage}, reason={lastResult.FailureReason}");
            await Task.Delay(Math.Max(0, options.DiscoveryRetryDelayMs), cancellationToken).ConfigureAwait(true);
        }

        lastResult ??= CreateFailureResult("discovery not attempted", "DiscoveryNotAttempted", TimeSpan.Zero);
        WriteFailureReport(lastResult);
        return lastResult;
    }

    private YmmPreviewDiscoveryResult DiscoverOnUiThread()
    {
        var watch = Stopwatch.StartNew();
        try
        {
            var windows = CollectWindows();
            logger.Info($"Window discovery complete. count={windows.Count}");

            var visualTree = CollectVisualTree();
            logger.Info($"VisualTree discovery complete. count={visualTree.Count}");

            var candidates = DiscoverCandidates();
            logger.Info($"Preview candidate discovery complete. count={candidates.Count}");

            var methods = DiscoverMethods(candidates);
            logger.Info($"Method discovery complete. groups={methods.Count}, methods={methods.Sum(x => x.Methods.Count)}");

            var signatures = DiscoverMethodSignatures(candidates);
            logger.Info($"Method signature discovery complete. signatures={signatures.Count}");

            var previewViewCandidate = candidates.FirstOrDefault(x => IsPreviewViewType(x.Info.FoundType) || IsPreviewViewType(x.Info.ControlType));
            var previewViewFound = previewViewCandidate is not null;
            var previewViewDataContextType = previewViewCandidate?.Info.DataContextType;

            var previewViewModelCandidate = candidates.FirstOrDefault(x => ContainsKeyword(x.Info.FoundType, "PreviewViewModel") || ContainsKeyword(x.Info.DataContextType, "PreviewViewModel"));
            var scenePreviewViewModelCandidate = candidates.FirstOrDefault(x => ContainsKeyword(x.Info.FoundType, "ScenePreviewViewModel") || ContainsKeyword(x.Info.DataContextType, "ScenePreviewViewModel"));
            var previewViewModelFound = previewViewModelCandidate is not null;
            var scenePreviewViewModelFound = scenePreviewViewModelCandidate is not null;

            var getBitmapMethodFound = methods.SelectMany(x => x.Methods).Any(x => string.Equals(x.MatchKeyword, "GetBitmap", StringComparison.OrdinalIgnoreCase));
            var getBitmapSignature = ResolveGetBitmapSignature(signatures);
            var invocationCandidates = getBitmapSignature?.InvocationCandidates ?? [];
            var previewViewModelTypeName = previewViewModelCandidate?.Info.FoundType
                ?? previewViewModelCandidate?.Info.DataContextType
                ?? scenePreviewViewModelCandidate?.Info.FoundType
                ?? scenePreviewViewModelCandidate?.Info.DataContextType;
            var targetCandidate = ResolveInvocationTarget(candidates);
            var targetMethod = ResolveGetBitmapMethod(targetCandidate?.Type);
            var targetArguments = ResolveInvocationArguments(targetMethod);
            var discoverySucceeded = targetCandidate is not null && targetMethod is not null;

            var discoveryLevelReached = DetermineDiscoveryLevel(
                windows.Count,
                visualTree.Count,
                candidates.Count,
                previewViewFound || previewViewModelFound || scenePreviewViewModelFound,
                getBitmapMethodFound,
                signatures.Count > 0,
                invocationCandidates.Count > 0,
                true);

            var result = new YmmPreviewDiscoveryResult
            {
                DiscoverySucceeded = discoverySucceeded,
                FailureStage = discoverySucceeded ? null : (previewViewModelFound ? "GetBitmapNotFound" : "PreviewViewModelNotFound"),
                FailureReason = discoverySucceeded ? null : (previewViewModelFound ? "GetBitmap not found" : "PreviewViewModel not found"),
                DiscoveryLevelReached = discoveryLevelReached,
                WindowCount = windows.Count,
                VisualTreeElementCount = visualTree.Count,
                PreviewCandidateCount = candidates.Count,
                PreviewMethodCount = methods.Sum(x => x.Methods.Count),
                MethodSignatureCount = signatures.Count,
                PreviewViewFound = previewViewFound,
                PreviewViewDataContextType = previewViewDataContextType,
                PreviewViewModelFound = previewViewModelFound,
                ScenePreviewViewModelFound = scenePreviewViewModelFound,
                GetBitmapMethodFound = getBitmapMethodFound,
                PreviewViewModelTypeName = previewViewModelTypeName,
                GetBitmapReturnTypeName = getBitmapSignature?.ReturnType,
                GetBitmapSignatureCategory = getBitmapSignature?.Category,
                GetBitmapParameterCount = getBitmapSignature?.ParameterCount,
                GetBitmapParameterTypes = getBitmapSignature?.Parameters.Select(parameter => parameter.Type ?? string.Empty).ToArray() ?? [],
                GetBitmapInvocationCandidates = invocationCandidates,
                NextRecommendedCall = invocationCandidates.FirstOrDefault(),
                Windows = windows,
                VisualTree = visualTree,
                Candidates = candidates.Select(x => x.Info).ToArray(),
                Methods = methods,
                MethodSignatures = signatures,
                CandidateDataContextTypes = candidates
                    .Select(x => x.Info.DataContextType)
                    .Where(value => !string.IsNullOrWhiteSpace(value))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .Take(options.MaxCandidateLogCount)
                    .Select(value => value!)
                    .ToArray(),
                CandidatePropertyNames = CollectCandidatePropertyNames(candidates, options.MaxCandidateLogCount),
                CandidateMethodNames = CollectCandidateMethodNames(candidates, options.MaxCandidateLogCount),
                TargetInstance = targetCandidate?.Instance,
                TargetType = targetCandidate?.Type,
                TargetMethod = targetMethod,
                TargetArguments = targetArguments,
                DiscoveryFailureReason = discoverySucceeded ? null : (previewViewModelFound ? "GetBitmap not found" : "PreviewViewModel not found"),
                SignatureFailureReason = discoverySucceeded ? null : (targetMethod is null && targetCandidate is not null ? "GetBitmap not found" : null),
                InvocationFailureReason = null,
                BitmapSaveFailureReason = null,
                OverallFailureReason = discoverySucceeded ? null : (previewViewModelFound ? "GetBitmap not found" : "PreviewViewModel not found"),
            };

            watch.Stop();
            return result;
        }
        catch (Exception ex)
        {
            watch.Stop();
            logger.Error(ex, "Preview discovery failed during UI traversal.");
            return CreateFailureResult(ex.Message, "DiscoveryException", watch.Elapsed);
        }
    }

    private static IReadOnlyList<PreviewBitmapWindowInfo> CollectWindows()
    {
        var windows = new List<PreviewBitmapWindowInfo>();
        foreach (var window in System.Windows.Application.Current?.Windows.OfType<Window>() ?? [])
        {
            windows.Add(new PreviewBitmapWindowInfo
            {
                Type = window.GetType().FullName,
                Name = window.Name,
                Title = window.Title,
                DataContextType = window.DataContext?.GetType().FullName,
                IsActive = window.IsActive,
                Visibility = window.Visibility.ToString(),
            });
        }

        return windows;
    }

    private static IReadOnlyList<PreviewBitmapVisualTreeInfo> CollectVisualTree()
    {
        var entries = new List<PreviewBitmapVisualTreeInfo>();
        foreach (var window in System.Windows.Application.Current?.Windows.OfType<Window>() ?? [])
        {
            if (window.Content is DependencyObject root)
            {
                CollectVisualTree(root, entries, window.GetType().FullName, 0);
            }
        }

        return entries;
    }

    private static void CollectVisualTree(DependencyObject element, ICollection<PreviewBitmapVisualTreeInfo> entries, string? windowType, int depth)
    {
        var elementType = element.GetType();
        entries.Add(new PreviewBitmapVisualTreeInfo
        {
            WindowType = windowType,
            ControlType = elementType.FullName,
            ControlName = GetElementName(element),
            DataContextType = GetDataContext(element)?.GetType().FullName,
            Depth = depth,
            ParentType = GetParentType(element),
        });

        var childCount = VisualTreeHelper.GetChildrenCount(element);
        for (var i = 0; i < childCount; i++)
        {
            if (VisualTreeHelper.GetChild(element, i) is DependencyObject child)
            {
                CollectVisualTree(child, entries, windowType, depth + 1);
            }
        }
    }

    private static IReadOnlyList<CandidateDiscovery> DiscoverCandidates()
    {
        var discoveries = new List<CandidateDiscovery>();
        foreach (var window in System.Windows.Application.Current?.Windows.OfType<Window>() ?? [])
        {
            DiscoverFromInstance(discoveries, window, window.GetType(), "WindowType", window.GetType().FullName, window.Name, window.GetType().FullName, null, 0);

            var dataContext = GetDataContext(window);
            if (dataContext is not null)
            {
                DiscoverFromInstance(discoveries, dataContext, dataContext.GetType(), "DataContextType", window.GetType().FullName, window.Name, window.GetType().FullName, dataContext.GetType().FullName, 0);
            }

            if (window.Content is DependencyObject root)
            {
                DiscoverTreeCandidates(root, discoveries, 0, window.GetType().FullName);
            }
        }

        return discoveries;
    }

    private static void DiscoverTreeCandidates(DependencyObject element, ICollection<CandidateDiscovery> discoveries, int depth, string? parentType)
    {
        var elementType = element.GetType();
        var controlName = GetElementName(element);
        var dataContext = GetDataContext(element);
        var dataContextType = dataContext?.GetType();

        DiscoverFromInstance(discoveries, element, elementType, "ControlType", parentType, controlName, elementType.FullName, dataContextType?.FullName, depth);

        if (dataContextType is not null)
        {
            DiscoverFromInstance(discoveries, dataContext, dataContextType, "DataContextType", elementType.FullName, controlName, elementType.FullName, dataContextType.FullName, depth);
        }

        var childCount = VisualTreeHelper.GetChildrenCount(element);
        for (var i = 0; i < childCount; i++)
        {
            if (VisualTreeHelper.GetChild(element, i) is DependencyObject child)
            {
                DiscoverTreeCandidates(child, discoveries, depth + 1, elementType.FullName);
            }
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
                    ParameterTypes = method.GetParameters().Select(parameter => parameter.ParameterType.FullName ?? parameter.ParameterType.Name).ToArray(),
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

    private static CandidateDiscovery? ResolveInvocationTarget(IReadOnlyList<CandidateDiscovery> candidates)
    {
        return candidates
            .Where(candidate => IsPreviewViewModelType(candidate.Info.FoundType) || IsPreviewViewModelType(candidate.Info.DataContextType) || IsScenePreviewViewModelType(candidate.Info.FoundType) || IsScenePreviewViewModelType(candidate.Info.DataContextType))
            .OrderByDescending(candidate => IsPreviewViewModelType(candidate.Info.FoundType) || IsPreviewViewModelType(candidate.Info.DataContextType))
            .ThenByDescending(candidate => IsScenePreviewViewModelType(candidate.Info.FoundType) || IsScenePreviewViewModelType(candidate.Info.DataContextType))
            .ThenByDescending(candidate => candidate.Type.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
                .Any(methodInfo => string.Equals(methodInfo.Name, "GetBitmap", StringComparison.OrdinalIgnoreCase)))
            .FirstOrDefault();
    }

    private static MethodInfo? ResolveGetBitmapMethod(Type? type)
    {
        if (type is null)
        {
            return null;
        }

        var methods = type.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
            .Where(method => string.Equals(method.Name, "GetBitmap", StringComparison.OrdinalIgnoreCase))
            .ToArray();

        if (methods.Length == 0)
        {
            return null;
        }

        return methods
            .OrderByDescending(method => method.GetParameters().Length == 1 && method.GetParameters()[0].ParameterType == typeof(bool))
            .ThenBy(method => method.GetParameters().Length)
            .FirstOrDefault();
    }

    private static object?[] ResolveInvocationArguments(MethodInfo? method)
    {
        if (method is null)
        {
            return [];
        }

        var parameters = method.GetParameters();
        if (parameters.Length == 0)
        {
            return [];
        }

        if (parameters.Length == 1 && parameters[0].ParameterType == typeof(bool))
        {
            return [true];
        }

        if (parameters.All(parameter => parameter.HasDefaultValue))
        {
            return parameters.Select(parameter => parameter.HasDefaultValue ? parameter.DefaultValue : null).ToArray();
        }

        if (LooksLikeWidthHeight(parameters.Select(parameter => new PreviewBitmapMethodSignatureParameterInfo
        {
            Name = parameter.Name,
            Type = parameter.ParameterType.FullName ?? parameter.ParameterType.Name,
            HasDefaultValue = parameter.HasDefaultValue,
            DefaultValue = parameter.HasDefaultValue ? FormatDefaultValue(parameter.DefaultValue) : null,
        }).ToArray()))
        {
            return [320, 180];
        }

        return parameters.Select(parameter => parameter.HasDefaultValue ? parameter.DefaultValue : null).ToArray();
    }

    private static IReadOnlyList<string> CollectCandidatePropertyNames(IReadOnlyList<CandidateDiscovery> candidates, int maxCount)
    {
        return candidates
            .SelectMany(candidate => candidate.Type.GetProperties(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static))
            .Select(property => $"{property.DeclaringType?.FullName ?? property.ReflectedType?.FullName ?? property.Name}.{property.Name}")
            .Distinct(StringComparer.Ordinal)
            .Take(Math.Max(0, maxCount))
            .ToArray();
    }

    private static IReadOnlyList<string> CollectCandidateMethodNames(IReadOnlyList<CandidateDiscovery> candidates, int maxCount)
    {
        return candidates
            .SelectMany(candidate => candidate.Type.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static))
            .Where(method => !method.IsSpecialName)
            .Select(method => $"{method.DeclaringType?.FullName ?? method.ReflectedType?.FullName ?? method.Name}.{method.Name}")
            .Distinct(StringComparer.Ordinal)
            .Take(Math.Max(0, maxCount))
            .ToArray();
    }

    private static PreviewBitmapMethodSignatureInfo? ResolveGetBitmapSignature(IReadOnlyList<PreviewBitmapMethodSignatureInfo> signatures)
    {
        return signatures
            .Where(signature => string.Equals(signature.MethodName, "GetBitmap", StringComparison.OrdinalIgnoreCase))
            .OrderBy(signature => signature.ParameterCount)
            .ThenBy(signature => signature.Category == "NoParameters" ? 0 : signature.Category == "OptionalParametersOnly" ? 1 : signature.Category == "RequiredParameters" ? 2 : 3)
            .FirstOrDefault();
    }

    private static int DetermineDiscoveryLevel(
        int windowCount,
        int controlCount,
        int candidateCount,
        bool previewCandidateFound,
        bool getBitmapMethodFound,
        bool signatureFound,
        bool invocationCandidateFound,
        bool invocationAttempted)
    {
        var level = 0;
        if (windowCount > 0) level = 1;
        if (controlCount > 0) level = 2;
        if (candidateCount > 0) level = 3;
        if (previewCandidateFound) level = 4;
        if (getBitmapMethodFound) level = 5;
        if (signatureFound) level = 6;
        if (invocationCandidateFound) level = 7;
        if (invocationAttempted) level = 8;
        return level;
    }

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
            candidates.Add("GetBitmap(true)");
            candidates.Add("GetBitmap(false)");
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
        return IsNumericType(first.Type)
            && IsNumericType(second.Type)
            && LooksLikeWidth(first.Name, first.Type)
            && LooksLikeHeight(second.Name, second.Type);
    }

    private static bool LooksLikeWidth(string? name, string? type)
        => !string.IsNullOrWhiteSpace(name) && (name.Contains("width", StringComparison.OrdinalIgnoreCase) || string.Equals(name, "w", StringComparison.OrdinalIgnoreCase) || IsNumericType(type));

    private static bool LooksLikeHeight(string? name, string? type)
        => !string.IsNullOrWhiteSpace(name) && (name.Contains("height", StringComparison.OrdinalIgnoreCase) || string.Equals(name, "h", StringComparison.OrdinalIgnoreCase) || IsNumericType(type));

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

    private static bool IsPreviewViewType(string? value)
        => !string.IsNullOrWhiteSpace(value)
            && value.Contains("PreviewView", StringComparison.OrdinalIgnoreCase)
            && !value.Contains("PreviewViewModel", StringComparison.OrdinalIgnoreCase);

    private static bool IsPreviewViewModelType(string? value)
        => !string.IsNullOrWhiteSpace(value) && value.Contains("PreviewViewModel", StringComparison.OrdinalIgnoreCase);

    private static bool IsScenePreviewViewModelType(string? value)
        => !string.IsNullOrWhiteSpace(value) && value.Contains("ScenePreviewViewModel", StringComparison.OrdinalIgnoreCase);

    private static bool ContainsKeyword(string? value)
        => !string.IsNullOrWhiteSpace(value) && CandidateKeywords.Any(keyword => value.Contains(keyword, StringComparison.OrdinalIgnoreCase));

    private static bool ContainsKeyword(string? value, string keyword)
        => !string.IsNullOrWhiteSpace(value) && value.Contains(keyword, StringComparison.OrdinalIgnoreCase);

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

    private static string? GetElementName(DependencyObject element)
    {
        if (element is FrameworkElement frameworkElement)
        {
            return frameworkElement.Name;
        }

        if (element is FrameworkContentElement contentElement)
        {
            return contentElement.Name;
        }

        return null;
    }

    private static string? GetParentType(DependencyObject element)
    {
        var parent = VisualTreeHelper.GetParent(element);
        return parent?.GetType().FullName;
    }

    private YmmPreviewDiscoveryResult CreateFailureResult(string failureReason, string failureStage, TimeSpan duration)
    {
        var result = new YmmPreviewDiscoveryResult
        {
            DiscoverySucceeded = false,
            FailureReason = failureReason,
            FailureStage = failureStage,
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
            PreviewViewModelTypeName = null,
            GetBitmapReturnTypeName = null,
            GetBitmapSignatureCategory = null,
            GetBitmapParameterCount = null,
            GetBitmapParameterTypes = [],
            GetBitmapInvocationCandidates = [],
            NextRecommendedCall = null,
            Windows = [],
            VisualTree = [],
            Candidates = [],
            Methods = [],
            MethodSignatures = [],
            CandidateDataContextTypes = [],
            CandidatePropertyNames = [],
            CandidateMethodNames = [],
            DiscoveryFailureReason = failureReason,
            SignatureFailureReason = null,
            InvocationFailureReason = null,
            BitmapSaveFailureReason = null,
            OverallFailureReason = failureReason,
        };

        return result;
    }

    private void WriteFailureReport(YmmPreviewDiscoveryResult result)
    {
        try
        {
            Directory.CreateDirectory(failureDirectory);
            var fileName = $"discovery-failure-{DateTimeOffset.Now:yyyyMMdd-HHmmss}.json";
            var path = Path.Combine(failureDirectory, fileName);
            var report = new YmmPreviewDiscoveryFailureReport
            {
                Timestamp = DateTimeOffset.Now,
                FailureReason = result.FailureReason ?? result.OverallFailureReason,
                FailureStage = result.FailureStage,
                WindowCount = result.WindowCount,
                VisualTreeElementCount = result.VisualTreeElementCount,
                PreviewViewFound = result.PreviewViewFound,
                PreviewViewModelFound = result.PreviewViewModelFound,
                GetBitmapMethodFound = result.GetBitmapMethodFound,
                DiscoveryLevelReached = result.DiscoveryLevelReached,
                CandidateTypes = result.CandidateDataContextTypes,
                CandidateMethods = result.CandidateMethodNames,
                CandidateDataContexts = result.CandidateDataContextTypes,
            };

            File.WriteAllText(path, JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
            logger.Info($"Discovery failure report saved. path={path}");
        }
        catch (Exception ex)
        {
            logger.Error(ex, "Failed to save discovery failure report.");
        }
    }

    private sealed record CandidateDiscovery(Type Type, object? Instance, PreviewBitmapCandidateInfo Info);
}
