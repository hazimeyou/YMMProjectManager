namespace YMMProjectManager.Presentation.Timeline;

public sealed class YmmTimelineDependencyResolver
{
    private readonly NullabilityInfoContext nullabilityInfoContext = new();

    public YmmTimelineConstructorParameterResult Evaluate(ParameterInfo parameterInfo)
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

        var parameterTypeName = result.ParameterTypeName;
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
}
