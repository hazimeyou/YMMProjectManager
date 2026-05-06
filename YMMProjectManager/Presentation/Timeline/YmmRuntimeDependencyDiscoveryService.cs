namespace YMMProjectManager.Presentation.Timeline;

public sealed class YmmRuntimeDependencyDiscoveryService
{
    private readonly YmmRuntimeDependencyDiscoveryOptions options;

    public YmmRuntimeDependencyDiscoveryService()
        : this(new YmmRuntimeDependencyDiscoveryOptions())
    {
    }

    public YmmRuntimeDependencyDiscoveryService(YmmRuntimeDependencyDiscoveryOptions options)
    {
        this.options = options;
    }

    public IReadOnlyList<YmmRuntimeDependencyCandidate> DiscoverCandidates(
        IEnumerable<Assembly> assemblies,
        string dependencyName,
        Type? dependencyType)
    {
        if (dependencyType is null)
        {
            return [];
        }

        var candidates = new List<YmmRuntimeDependencyCandidate>();

        foreach (var assembly in assemblies)
        {
            Type[] types;
            try
            {
                types = assembly.GetTypes();
            }
            catch (ReflectionTypeLoadException ex)
            {
                types = ex.Types.Where(x => x is not null).Cast<Type>().ToArray();
            }
            catch
            {
                continue;
            }

            foreach (var owner in types)
            {
                if (ShouldExcludeOwnerType(owner))
                {
                    continue;
                }

                DiscoverPropertyCandidates(owner, dependencyName, dependencyType, candidates);
                DiscoverFieldCandidates(owner, dependencyName, dependencyType, candidates);
                DiscoverMethodCandidates(owner, dependencyName, dependencyType, candidates);
            }
        }

        DiscoverLiveInstanceCandidates(dependencyName, dependencyType, candidates);

        return candidates;
    }

    private static void DiscoverPropertyCandidates(
        Type owner,
        string dependencyName,
        Type dependencyType,
        ICollection<YmmRuntimeDependencyCandidate> candidates)
    {
        foreach (var propertyInfo in owner.GetProperties(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static))
        {
            if (!dependencyType.IsAssignableFrom(propertyInfo.PropertyType))
            {
                continue;
            }

            var candidate = CreateBaseCandidate(dependencyName, dependencyType, owner, "Property", propertyInfo.Name, IsStatic(propertyInfo));
            var getter = propertyInfo.GetGetMethod(true);
            if (candidate.IsStatic && getter is not null)
            {
                candidate.CanReadExistingInstance = true;
                TryReadStaticValue(() => getter.Invoke(null, null), candidate);
            }

            candidates.Add(candidate);
        }
    }

    private static void DiscoverFieldCandidates(
        Type owner,
        string dependencyName,
        Type dependencyType,
        ICollection<YmmRuntimeDependencyCandidate> candidates)
    {
        foreach (var fieldInfo in owner.GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static))
        {
            if (!dependencyType.IsAssignableFrom(fieldInfo.FieldType))
            {
                continue;
            }

            var candidate = CreateBaseCandidate(dependencyName, dependencyType, owner, "Field", fieldInfo.Name, fieldInfo.IsStatic);
            if (fieldInfo.IsStatic)
            {
                candidate.CanReadExistingInstance = true;
                TryReadStaticValue(() => fieldInfo.GetValue(null), candidate);
            }

            candidates.Add(candidate);
        }
    }

    private static void DiscoverMethodCandidates(
        Type owner,
        string dependencyName,
        Type dependencyType,
        ICollection<YmmRuntimeDependencyCandidate> candidates)
    {
        foreach (var methodInfo in owner.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static))
        {
            if (!dependencyType.IsAssignableFrom(methodInfo.ReturnType))
            {
                continue;
            }

            // Do not invoke methods here. We only record route candidates.
            var candidate = CreateBaseCandidate(dependencyName, dependencyType, owner, "Method", methodInfo.Name, methodInfo.IsStatic);
            candidate.CanReadExistingInstance = false;
            candidates.Add(candidate);
        }
    }

    private static YmmRuntimeDependencyCandidate CreateBaseCandidate(
        string dependencyName,
        Type dependencyType,
        Type owner,
        string memberKind,
        string memberName,
        bool isStatic)
    {
        return new YmmRuntimeDependencyCandidate
        {
            DependencyName = dependencyName,
            TargetTypeName = dependencyType.FullName ?? dependencyType.Name,
            OwnerTypeName = owner.FullName ?? owner.Name,
            MemberKind = memberKind,
            MemberName = memberName,
            IsStatic = isStatic,
        };
    }

    private static bool IsStatic(PropertyInfo propertyInfo)
    {
        var getter = propertyInfo.GetGetMethod(true);
        var setter = propertyInfo.GetSetMethod(true);
        return (getter?.IsStatic ?? false) || (setter?.IsStatic ?? false);
    }

    private static void TryReadStaticValue(Func<object?> valueFactory, YmmRuntimeDependencyCandidate candidate)
    {
        try
        {
            candidate.ExistingInstanceFound = valueFactory() is not null;
        }
        catch (Exception ex)
        {
            candidate.AccessError = $"{ex.GetType().Name}: {ex.Message}";
        }
    }

    private void DiscoverLiveInstanceCandidates(
        string dependencyName,
        Type dependencyType,
        ICollection<YmmRuntimeDependencyCandidate> candidates)
    {
        var app = System.Windows.Application.Current;
        if (app is null)
        {
            return;
        }

        var maxDepth = Math.Max(1, options.MaxDepth);
        var maxNodes = Math.Max(100, options.MaxNodes);
        var queue = new Queue<(object Node, string Path, int Depth)>();
        var seen = new HashSet<object>(ReferenceEqualityComparer.Instance);

        foreach (var window in app.Windows.OfType<System.Windows.Window>())
        {
            Enqueue(window, $"Window[{window.GetType().FullName}]");
            if (window.DataContext is not null)
            {
                Enqueue(window.DataContext, $"Window[{window.GetType().FullName}].DataContext");
            }
        }

        var processed = 0;
        while (queue.Count > 0 && processed < maxNodes)
        {
            var (node, path, depth) = queue.Dequeue();
            processed++;

            if (dependencyType.IsInstanceOfType(node))
            {
                candidates.Add(new YmmRuntimeDependencyCandidate
                {
                    DependencyName = dependencyName,
                    TargetTypeName = dependencyType.FullName ?? dependencyType.Name,
                    OwnerTypeName = node.GetType().FullName ?? node.GetType().Name,
                    MemberKind = "Object",
                    MemberName = "(self)",
                    IsStatic = false,
                    CanReadExistingInstance = true,
                    ExistingInstanceFound = true,
                    RoutePath = path,
                    Depth = depth,
                });
            }

            if (depth >= maxDepth)
            {
                continue;
            }

            var nodeType = node.GetType();
            if (ShouldExcludeOwnerType(nodeType))
            {
                continue;
            }

            foreach (var property in nodeType.GetProperties(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance))
            {
                if (property.GetIndexParameters().Length > 0)
                {
                    continue;
                }

                object? value;
                try
                {
                    value = property.GetValue(node);
                }
                catch
                {
                    continue;
                }

                if (value is null)
                {
                    continue;
                }

                if (dependencyType.IsInstanceOfType(value))
                {
                    candidates.Add(new YmmRuntimeDependencyCandidate
                    {
                        DependencyName = dependencyName,
                        TargetTypeName = dependencyType.FullName ?? dependencyType.Name,
                        OwnerTypeName = nodeType.FullName ?? nodeType.Name,
                        MemberKind = "Property",
                        MemberName = property.Name,
                        IsStatic = false,
                        CanReadExistingInstance = true,
                        ExistingInstanceFound = true,
                        RoutePath = $"{path}.{property.Name}",
                        Depth = depth + 1,
                    });
                }

                if (ShouldTraverse(value))
                {
                    Enqueue(value, $"{path}.{property.Name}", depth + 1);
                }
            }

            foreach (var field in nodeType.GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance))
            {
                object? value;
                try
                {
                    value = field.GetValue(node);
                }
                catch
                {
                    continue;
                }

                if (value is null)
                {
                    continue;
                }

                if (dependencyType.IsInstanceOfType(value))
                {
                    candidates.Add(new YmmRuntimeDependencyCandidate
                    {
                        DependencyName = dependencyName,
                        TargetTypeName = dependencyType.FullName ?? dependencyType.Name,
                        OwnerTypeName = nodeType.FullName ?? nodeType.Name,
                        MemberKind = "Field",
                        MemberName = field.Name,
                        IsStatic = false,
                        CanReadExistingInstance = true,
                        ExistingInstanceFound = true,
                        RoutePath = $"{path}.{field.Name}",
                        Depth = depth + 1,
                    });
                }

                if (ShouldTraverse(value))
                {
                    Enqueue(value, $"{path}.{field.Name}", depth + 1);
                }
            }
        }

        void Enqueue(object value, string route, int depth = 0)
        {
            if (!seen.Add(value))
            {
                return;
            }

            queue.Enqueue((value, route, depth));
        }
    }

    private static bool ShouldTraverse(object value)
    {
        var type = value.GetType();
        if (type.IsPrimitive || type.IsEnum)
        {
            return false;
        }

        if (value is string)
        {
            return false;
        }

        if (typeof(System.Collections.IEnumerable).IsAssignableFrom(type) && type != typeof(System.Windows.WindowCollection))
        {
            return false;
        }

        return true;
    }

    private bool ShouldExcludeOwnerType(Type type)
    {
        var fullName = type.FullName ?? type.Name;
        foreach (var prefix in options.ExcludedOwnerTypePrefixes)
        {
            if (fullName.StartsWith(prefix, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }
}
