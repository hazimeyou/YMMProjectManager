namespace YMMProjectManager.Presentation.Timeline;

public sealed class YmmTimelineConstructorBinder
{
    private readonly YmmTimelineDependencyResolver dependencyResolver = new();

    public IReadOnlyList<YmmTimelineConstructorBindingResult> DryRunForType(Type? targetType, ICollection<YmmTimelineReflectionLog>? logs = null)
    {
        if (targetType is null)
        {
            return [];
        }

        var results = new List<YmmTimelineConstructorBindingResult>();
        var constructors = targetType.GetConstructors(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        foreach (var constructor in constructors)
        {
            var parameters = constructor.GetParameters()
                .Select(dependencyResolver.Evaluate)
                .ToList();

            var unresolvedRequired = parameters.Where(x => !x.IsOptional && !x.CanResolve).ToList();
            var allRequiredResolvable = unresolvedRequired.Count == 0;
            var result = new YmmTimelineConstructorBindingResult
            {
                TargetTypeName = targetType.FullName ?? targetType.Name,
                ConstructorSignature = FormatConstructor(constructor),
                Parameters = parameters,
                AllRequiredParametersResolvable = allRequiredResolvable,
                CanAttemptGeneration = allRequiredResolvable,
                Notes = allRequiredResolvable
                    ? "All required parameters appear resolvable in dry-run."
                    : string.Join(" / ", unresolvedRequired.Select(x => $"{x.ParameterName}: {x.FailureReason}")),
            };
            results.Add(result);
            logs?.Add(new YmmTimelineReflectionLog
            {
                Category = "Bind",
                Message = $"{result.ConstructorSignature} => resolvable={result.CanAttemptGeneration}",
            });
        }

        return results;
    }

    public YmmTimelineGenerationReadiness BuildReadiness(
        YmmTimelineReflectionResult reflection,
        IReadOnlyList<YmmTimelineConstructorBindingResult> viewBindings,
        IReadOnlyList<YmmTimelineConstructorBindingResult> viewModelBindings)
    {
        var blocking = new List<string>();
        var warnings = new List<string>();
        var score = 0;

        var viewTypeFound = reflection.TimelineViewFound;
        var vmTypeFound = reflection.TimelineViewModelFound;
        var viewBindable = viewBindings.Any(x => x.CanAttemptGeneration);
        var vmBindable = viewModelBindings.Any(x => x.CanAttemptGeneration);

        if (viewTypeFound) score += 15; else blocking.Add("TimelineView 型が見つかりません。");
        if (vmTypeFound) score += 15; else blocking.Add("TimelineViewModel 型が見つかりません。");
        if (vmBindable) score += 20; else blocking.Add("TimelineViewModel constructor の必須引数を解決できません。");
        if (viewBindable) score += 20; else warnings.Add("TimelineView constructor の必須引数を解決できません。");
        if (reflection.UndoRedoManagerFound) score += 10; else warnings.Add("UndoRedoManager 型が未検出です。");
        if (reflection.AsyncAwaitStatusFound) score += 10; else warnings.Add("AsyncAwaitStatus 型が未検出です。");
        if (reflection.SetTimelineToolInfoFound) score += 10; else warnings.Add("SetTimelineToolInfo 入口が未検出です。");

        if (viewBindings.Count == 0)
        {
            warnings.Add("TimelineView constructor 候補がありません。");
        }

        if (viewModelBindings.Count == 0)
        {
            blocking.Add("TimelineViewModel constructor 候補がありません。");
        }

        score -= Math.Min(20, blocking.Count * 5);
        score = Math.Max(0, Math.Min(100, score));

        return new YmmTimelineGenerationReadiness
        {
            TimelineViewTypeFound = viewTypeFound,
            TimelineViewModelTypeFound = vmTypeFound,
            TimelineViewConstructorBindable = viewBindable,
            TimelineViewModelConstructorBindable = vmBindable,
            CanAttemptViewModelGeneration = vmTypeFound && vmBindable,
            CanAttemptViewGeneration = viewTypeFound && viewBindable,
            Score = score,
            BlockingReasons = blocking,
            Warnings = warnings,
        };
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
