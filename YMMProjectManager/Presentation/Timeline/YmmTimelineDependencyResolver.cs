namespace YMMProjectManager.Presentation.Timeline;

public sealed class YmmTimelineDependencyResolver
{
    private readonly NullabilityInfoContext nullabilityInfoContext = new();

    public YmmTimelineConstructorParameterResult Evaluate(
        ParameterInfo parameterInfo,
        YmmTimelineReflectionResult? reflectionResult = null)
    {
        var parameterType = parameterInfo.ParameterType;
        var parameterName = parameterInfo.Name ?? "(unknown)";
        var nullable = IsNullable(parameterInfo);

        var result = new YmmTimelineConstructorParameterResult
        {
            ParameterName = parameterName,
            ParameterTypeName = parameterType.FullName ?? parameterType.Name,
            IsOptional = parameterInfo.IsOptional,
            IsNullable = nullable,
        };

        var parameterTypeName = result.ParameterTypeName;
        var isRequiredYmmRuntimeDependency = ContainsAny(
            parameterTypeName,
            "YukkuriMovieMaker.Project.Scene",
            "YukkuriMovieMaker.UndoRedo.UndoRedoManager",
            "YukkuriMovieMaker.Project.AsyncAwaitStatus");

        if (isRequiredYmmRuntimeDependency)
        {
            result.IsRequiredYmmRuntimeDependency = true;
            if (IsRequiredRuntimeDependencyResolved(parameterTypeName, reflectionResult))
            {
                result.CanResolve = true;
                result.ResolutionSource = "Resolved live runtime instance candidate";
            }
            else
            {
                result.CanResolve = false;
                result.FailureReason = "RequiredYmmRuntimeDependency is unresolved in isolated host context.";
            }
            return result;
        }

        if (parameterInfo.IsOptional)
        {
            result.CanResolve = true;
            result.ResolutionSource = "Optional parameter";
            return result;
        }

        if (nullable)
        {
            result.CanResolve = true;
            result.ResolutionSource = "Nullable parameter (can pass null)";
            return result;
        }

        if (parameterTypeName.Contains("Dispatcher", StringComparison.Ordinal))
        {
            result.CanResolve = true;
            result.ResolutionSource = "WPF Dispatcher";
            return result;
        }

        if (parameterTypeName.Contains("SynchronizationContext", StringComparison.Ordinal))
        {
            result.CanResolve = true;
            result.ResolutionSource = "SynchronizationContext.Current";
            return result;
        }

        if (parameterType == typeof(CancellationToken))
        {
            result.CanResolve = true;
            result.ResolutionSource = "CancellationToken.None";
            return result;
        }

        if (parameterTypeName.Contains("IServiceProvider", StringComparison.Ordinal))
        {
            result.CanResolve = false;
            result.FailureReason = "IServiceProvider source is not defined in isolated host context.";
            return result;
        }

        if (ContainsAny(parameterTypeName, "UndoRedoManager", "AsyncAwaitStatus", "TimelineToolInfo", "Scene", "Timeline"))
        {
            result.CanResolve = false;
            result.FailureReason = "YMM runtime dependency required.";
            return result;
        }

        result.CanResolve = false;
        result.FailureReason = "Unknown required dependency in isolated host context.";
        return result;
    }

    private bool IsNullable(ParameterInfo parameterInfo)
    {
        if (!parameterInfo.ParameterType.IsValueType || Nullable.GetUnderlyingType(parameterInfo.ParameterType) is not null)
        {
            return true;
        }

        try
        {
            var nullability = nullabilityInfoContext.Create(parameterInfo);
            return nullability.ReadState == NullabilityState.Nullable;
        }
        catch
        {
            return false;
        }
    }

    private static bool ContainsAny(string value, params string[] needles)
    {
        foreach (var needle in needles)
        {
            if (value.Contains(needle, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsRequiredRuntimeDependencyResolved(
        string parameterTypeName,
        YmmTimelineReflectionResult? reflectionResult)
    {
        if (reflectionResult is null)
        {
            return false;
        }

        if (parameterTypeName.Contains("YukkuriMovieMaker.Project.Scene", StringComparison.Ordinal))
        {
            return reflectionResult.SceneDiscovery.Resolved;
        }

        if (parameterTypeName.Contains("YukkuriMovieMaker.UndoRedo.UndoRedoManager", StringComparison.Ordinal))
        {
            return reflectionResult.UndoRedoManagerDiscovery.Resolved;
        }

        if (parameterTypeName.Contains("YukkuriMovieMaker.Project.AsyncAwaitStatus", StringComparison.Ordinal))
        {
            return reflectionResult.AsyncAwaitStatusDiscovery.Resolved;
        }

        return false;
    }
}
