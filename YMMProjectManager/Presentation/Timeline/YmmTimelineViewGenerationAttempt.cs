namespace YMMProjectManager.Presentation.Timeline;

public sealed class YmmTimelineViewGenerationAttempt
{
    public async Task<YmmTimelineViewGenerationAttemptResult> TryGenerateAndDisposeAsync(
        Type targetType,
        YmmTimelineConstructorBindingResult bindingResult,
        IReadOnlyDictionary<string, object?> runtimeDependencyInstances,
        PureTimelineExperimentalOptions options)
    {
        var result = new YmmTimelineViewGenerationAttemptResult
        {
            Attempted = true,
            TargetTypeName = targetType.FullName ?? targetType.Name,
            ConstructorSignature = bindingResult.ConstructorSignature,
            VisualAttachForbidden = options.ForbidVisualTreeAttach,
            VisualAttachAttempted = false,
        };

        object? instance = null;
        var sw = Stopwatch.StartNew();
        try
        {
            var constructor = targetType.GetConstructors(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                .FirstOrDefault(x => FormatConstructor(x) == bindingResult.ConstructorSignature);
            if (constructor is null)
            {
                result.Succeeded = false;
                result.FailureReason = "Constructor not found.";
                return result;
            }

            var args = constructor.GetParameters().Select(p =>
            {
                var tn = p.ParameterType.FullName ?? p.ParameterType.Name;
                if (runtimeDependencyInstances.TryGetValue(tn, out var dep) && dep is not null) return dep;
                if (p.IsOptional) return p.DefaultValue;
                if (p.ParameterType == typeof(CancellationToken)) return CancellationToken.None;
                if (p.ParameterType == typeof(SynchronizationContext)) return SynchronizationContext.Current;
                if (tn.Contains("Dispatcher", StringComparison.Ordinal)) return System.Windows.Application.Current?.Dispatcher;
                return null;
            }).ToArray();

            if (args.Any(x => x is null))
            {
                result.Succeeded = false;
                result.FailureReason = "Required constructor arguments are unresolved.";
                return result;
            }

            instance = constructor.Invoke(args);
            result.Succeeded = true;
        }
        catch (Exception ex)
        {
            var core = ex is TargetInvocationException tie && tie.InnerException is not null ? tie.InnerException : ex;
            result.Succeeded = false;
            result.FailureReason = "TimelineView generation failed.";
            result.ExceptionType = core.GetType().FullName;
            result.ExceptionMessage = core.Message;
        }
        finally
        {
            sw.Stop();
            result.GenerationAttemptMs = sw.ElapsedMilliseconds;
        }

        if (!options.DisposeImmediatelyAfterGeneration || instance is null)
        {
            return result;
        }

        result.DisposeAttempted = true;
        var dsw = Stopwatch.StartNew();
        var disposeResult = await YmmTimelineInstanceDisposer.DisposeAsync(instance).ConfigureAwait(true);
        dsw.Stop();
        result.DisposeMs = dsw.ElapsedMilliseconds;
        result.DisposeSucceeded = disposeResult.Succeeded;
        result.DisposeFailureReason = disposeResult.FailureReason;
        return result;
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
