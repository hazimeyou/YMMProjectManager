namespace YMMProjectManager.Presentation.Timeline;

public sealed class YmmTimelineViewModelGenerationAttempt
{
    public async Task<YmmTimelineGenerationAttemptResult> TryGenerateAndDisposeAsync(
        Type targetType,
        YmmTimelineConstructorBindingResult bindingResult,
        bool disposeImmediatelyAfterGeneration,
        IReadOnlyDictionary<string, object?>? runtimeDependencyInstances = null)
    {
        var result = new YmmTimelineGenerationAttemptResult
        {
            Attempted = true,
            TargetTypeName = targetType.FullName ?? targetType.Name,
            ConstructorSignature = bindingResult.ConstructorSignature,
        };

        var generationSw = Stopwatch.StartNew();
        object? instance = null;
        WeakReference? weakReference = null;
        try
        {
            var constructor = ResolveConstructor(targetType, bindingResult.ConstructorSignature);
            if (constructor is null)
            {
                result.Succeeded = false;
                result.FailureReason = "Constructor not found from binding signature.";
                return result;
            }

            var constructorParameters = constructor.GetParameters();
            result.ConstructorParameters = constructorParameters
                .Select(x => $"{x.ParameterType.FullName ?? x.ParameterType.Name} {x.Name}")
                .ToArray();

            var build = BuildArguments(constructorParameters, runtimeDependencyInstances);
            var args = build.Args;
            result.NullInjectedParameters = build.NullInjectedParameters;
            instance = constructor.Invoke(args);
            weakReference = new WeakReference(instance);
            result.Succeeded = true;
        }
        catch (Exception ex)
        {
            var core = ex is TargetInvocationException tie && tie.InnerException is not null ? tie.InnerException : ex;
            result.Succeeded = false;
            result.FailureReason = "Generation attempt failed.";
            result.ExceptionType = core.GetType().FullName;
            result.ExceptionMessage = core.Message;
            result.ExceptionStackTrace = core.StackTrace;
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
        VerifyGcReachability(result, weakReference);
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

    private static (object?[] Args, IReadOnlyList<string> NullInjectedParameters) BuildArguments(
        IReadOnlyList<ParameterInfo> parameters,
        IReadOnlyDictionary<string, object?>? runtimeDependencyInstances)
    {
        var args = new object?[parameters.Count];
        var nullInjected = new List<string>();
        for (var i = 0; i < parameters.Count; i++)
        {
            var value = ResolveArgument(parameters[i], runtimeDependencyInstances);
            args[i] = value;
            if (value is null)
            {
                nullInjected.Add($"{parameters[i].Name}:{parameters[i].ParameterType.FullName ?? parameters[i].ParameterType.Name}");
            }
        }

        return (args, nullInjected);
    }

    private static object? ResolveArgument(
        ParameterInfo parameterInfo,
        IReadOnlyDictionary<string, object?>? runtimeDependencyInstances)
    {
        if (parameterInfo.IsOptional)
        {
            return parameterInfo.DefaultValue;
        }

        var parameterType = parameterInfo.ParameterType;
        var parameterTypeName = parameterType.FullName ?? parameterType.Name;
        if (runtimeDependencyInstances is not null &&
            runtimeDependencyInstances.TryGetValue(parameterTypeName, out var runtimeInstance) &&
            runtimeInstance is not null)
        {
            return runtimeInstance;
        }

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

    private static void VerifyGcReachability(YmmTimelineGenerationAttemptResult result, WeakReference? weakReference)
    {
        if (weakReference is null)
        {
            return;
        }

        result.GcVerificationAttempted = true;
        try
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
            result.WeakReferenceAliveAfterGc = weakReference.IsAlive;
            result.FinalizationNote = weakReference.IsAlive
                ? "WeakReference is still alive after forced GC. This may be normal depending on runtime references."
                : "WeakReference is not alive after forced GC.";
        }
        catch (Exception ex)
        {
            result.FinalizationNote = $"GC verification failed: {ex.GetType().Name}: {ex.Message}";
        }
    }
}
