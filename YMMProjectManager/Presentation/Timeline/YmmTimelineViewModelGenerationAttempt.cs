namespace YMMProjectManager.Presentation.Timeline;

public sealed class YmmTimelineViewModelGenerationAttempt
{
    public async Task<YmmTimelineGenerationAttemptResult> TryGenerateAndDisposeAsync(
        Type targetType,
        YmmTimelineConstructorBindingResult bindingResult,
        bool disposeImmediatelyAfterGeneration)
    {
        var result = new YmmTimelineGenerationAttemptResult
        {
            Attempted = true,
            TargetTypeName = targetType.FullName ?? targetType.Name,
            ConstructorSignature = bindingResult.ConstructorSignature,
        };

        var generationSw = Stopwatch.StartNew();
        object? instance = null;
        try
        {
            var constructor = ResolveConstructor(targetType, bindingResult.ConstructorSignature);
            if (constructor is null)
            {
                result.Succeeded = false;
                result.FailureReason = "Constructor not found from binding signature.";
                return result;
            }

            var args = BuildArguments(constructor.GetParameters());
            instance = constructor.Invoke(args);
            result.Succeeded = true;
        }
        catch (Exception ex)
        {
            var core = ex is TargetInvocationException tie && tie.InnerException is not null ? tie.InnerException : ex;
            result.Succeeded = false;
            result.FailureReason = "Generation attempt failed.";
            result.ExceptionType = core.GetType().FullName;
            result.ExceptionMessage = core.Message;
        }
        finally
        {
            generationSw.Stop();
            result.GenerationAttemptMs = generationSw.ElapsedMilliseconds;
        }

        if (!disposeImmediatelyAfterGeneration || instance is null)
        {
            return result;
        }

        result.DisposeAttempted = true;
        var disposeSw = Stopwatch.StartNew();
        var disposeResult = await YmmTimelineInstanceDisposer.DisposeAsync(instance).ConfigureAwait(true);
        disposeSw.Stop();
        result.DisposeMs = disposeSw.ElapsedMilliseconds;
        result.DisposeSucceeded = disposeResult.Succeeded;
        result.DisposeFailureReason = disposeResult.FailureReason;
        return result;
    }

    private static ConstructorInfo? ResolveConstructor(Type targetType, string signature)
    {
        foreach (var constructor in targetType.GetConstructors(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
        {
            var formatted = FormatConstructor(constructor);
            if (string.Equals(formatted, signature, StringComparison.Ordinal))
            {
                return constructor;
            }
        }

        return null;
    }

    private static object?[] BuildArguments(IReadOnlyList<ParameterInfo> parameters)
    {
        var args = new object?[parameters.Count];
        for (var i = 0; i < parameters.Count; i++)
        {
            args[i] = ResolveArgument(parameters[i]);
        }

        return args;
    }

    private static object? ResolveArgument(ParameterInfo parameterInfo)
    {
        if (parameterInfo.IsOptional)
        {
            return parameterInfo.DefaultValue;
        }

        var parameterType = parameterInfo.ParameterType;
        if (parameterType == typeof(CancellationToken))
        {
            return CancellationToken.None;
        }

        if (parameterType.FullName?.Contains("Dispatcher", StringComparison.Ordinal) == true)
        {
            return System.Windows.Application.Current?.Dispatcher;
        }

        if (parameterType == typeof(SynchronizationContext))
        {
            return SynchronizationContext.Current;
        }

        // In preview15 we intentionally avoid constructing YMM runtime dependencies.
        return null;
    }

    private static string FormatConstructor(ConstructorInfo constructorInfo)
    {
        var access = constructorInfo.IsPublic ? "public" :
            constructorInfo.IsFamily ? "protected" :
            constructorInfo.IsPrivate ? "private" : "internal";
        var args = string.Join(", ", constructorInfo.GetParameters()
            .Select(p => $"{p.ParameterType.Name} {p.Name}"));
        return $"{access} .ctor({args})";
    }
}
