namespace YMMProjectManager.Presentation.Timeline;

public sealed class YmmTimelineReflectionProbe
{
    public YmmTimelineReflectionResult Probe(ICollection<YmmTimelineReflectionLog>? logs = null)
    {
        var sw = Stopwatch.StartNew();
        var notes = new List<string>();
        var missing = new List<string>();
        var constructorSignatures = new List<string>();
        var foundAssemblies = new List<string>();
        var typeFoundCount = 0;

        void Log(string category, string message)
        {
            logs?.Add(new YmmTimelineReflectionLog { Category = category, Message = message });
        }

        var assemblies = AppDomain.CurrentDomain.GetAssemblies();
        foreach (var assembly in assemblies.OrderBy(a => a.GetName().Name, StringComparer.OrdinalIgnoreCase))
        {
            var name = assembly.GetName().Name ?? "(unknown)";
            foundAssemblies.Add(name);
        }

        Log("Probe", $"Assembly count: {assemblies.Length}");

        var timelineViewType = ResolveType(assemblies, "YukkuriMovieMaker.Views.TimelineView");
        if (timelineViewType is not null)
        {
            typeFoundCount++;
            Log("Type", $"Found TimelineView: {timelineViewType.FullName}");
        }
        else
        {
            missing.Add("TimelineView");
            Log("Missing", "TimelineView type not found.");
        }

        var timelineViewModelType = ResolveType(assemblies, "YukkuriMovieMaker.ViewModels.TimelineViewModel");
        if (timelineViewModelType is not null)
        {
            typeFoundCount++;
            Log("Type", $"Found TimelineViewModel: {timelineViewModelType.FullName}");
            foreach (var ctor in timelineViewModelType.GetConstructors(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
            {
                var signature = FormatConstructor(ctor);
                constructorSignatures.Add(signature);
                Log("Ctor", signature);
            }
        }
        else
        {
            missing.Add("TimelineViewModel");
            Log("Missing", "TimelineViewModel type not found.");
        }

        var undoRedoManagerType = ResolveType(assemblies, "YukkuriMovieMaker.Commons.UndoRedoManager");
        if (undoRedoManagerType is not null)
        {
            typeFoundCount++;
            Log("Type", $"Found UndoRedoManager: {undoRedoManagerType.FullName}");
        }
        else
        {
            missing.Add("UndoRedoManager");
            Log("Missing", "UndoRedoManager type not found.");
        }

        var asyncAwaitStatusType = ResolveType(assemblies, "YukkuriMovieMaker.Commons.AsyncAwaitStatus");
        if (asyncAwaitStatusType is not null)
        {
            typeFoundCount++;
            Log("Type", $"Found AsyncAwaitStatus: {asyncAwaitStatusType.FullName}");
        }
        else
        {
            missing.Add("AsyncAwaitStatus");
            Log("Missing", "AsyncAwaitStatus type not found.");
        }

        var setTimelineToolInfoOwner = FindSetTimelineToolInfoOwner(assemblies);
        var setTimelineToolInfoFound = setTimelineToolInfoOwner is not null;
        if (setTimelineToolInfoFound)
        {
            typeFoundCount++;
            Log("Method", $"Found SetTimelineToolInfo owner: {setTimelineToolInfoOwner!.FullName}");
        }
        else
        {
            missing.Add("SetTimelineToolInfo");
            Log("Missing", "SetTimelineToolInfo method not found.");
        }

        var canAttemptExperimentalHost =
            timelineViewType is not null &&
            timelineViewModelType is not null &&
            constructorSignatures.Count > 0;

        if (!canAttemptExperimentalHost)
        {
            notes.Add("Required reflection prerequisites are not fully available.");
        }
        else
        {
            notes.Add("Core reflection prerequisites found. Experimental host attempt is possible.");
        }

        sw.Stop();
        Log("Probe", $"Probe completed in {sw.ElapsedMilliseconds} ms");

        return new YmmTimelineReflectionResult
        {
            TimelineViewFound = timelineViewType is not null,
            TimelineViewModelFound = timelineViewModelType is not null,
            UndoRedoManagerFound = undoRedoManagerType is not null,
            AsyncAwaitStatusFound = asyncAwaitStatusType is not null,
            SetTimelineToolInfoFound = setTimelineToolInfoFound,
            TimelineViewTypeName = timelineViewType?.FullName,
            TimelineViewModelTypeName = timelineViewModelType?.FullName,
            SetTimelineToolInfoOwnerTypeName = setTimelineToolInfoOwner?.FullName,
            ConstructorSignatures = constructorSignatures,
            MissingDependencies = missing,
            Notes = notes,
            FoundAssemblies = foundAssemblies,
            AssemblyCount = assemblies.Length,
            TypeFoundCount = typeFoundCount,
            ProbeMs = sw.ElapsedMilliseconds,
            CanAttemptExperimentalHost = canAttemptExperimentalHost,
        };
    }

    private static Type? ResolveType(IEnumerable<Assembly> assemblies, string fullName)
    {
        foreach (var assembly in assemblies)
        {
            var type = assembly.GetType(fullName, false, false);
            if (type is not null)
            {
                return type;
            }
        }

        return null;
    }

    private static Type? FindSetTimelineToolInfoOwner(IEnumerable<Assembly> assemblies)
    {
        foreach (var assembly in assemblies)
        {
            Type[] types;
            try
            {
                types = assembly.GetTypes();
            }
            catch (ReflectionTypeLoadException ex)
            {
                types = ex.Types.Where(t => t is not null).Cast<Type>().ToArray();
            }
            catch
            {
                continue;
            }

            foreach (var type in types)
            {
                var method = type.GetMethod("SetTimelineToolInfo",
                    BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static);
                if (method is not null)
                {
                    return type;
                }
            }
        }

        return null;
    }

    private static string FormatConstructor(ConstructorInfo constructorInfo)
    {
        var access = constructorInfo.IsPublic ? "public" :
            constructorInfo.IsFamily ? "protected" :
            constructorInfo.IsPrivate ? "private" : "internal";
        var args = string.Join(", ", constructorInfo.GetParameters().Select(p => $"{p.ParameterType.Name} {p.Name}"));
        return $"{access} .ctor({args})";
    }
}
