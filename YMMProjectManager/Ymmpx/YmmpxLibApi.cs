using System.Reflection;
using System.Runtime.ExceptionServices;

namespace YMMProjectManager.Infrastructure.Packaging;

// Binds the v1.1 packaging contract once. Safe extraction stays with YmmpxPackageReader.
internal sealed class YmmpxLibApi
{
    private readonly ConstructorInfo optionsConstructor;
    private readonly PropertyInfo includeUiSettings;
    private readonly ConstructorInfo progressConstructor;
    private readonly PropertyInfo percentage;
    private readonly MethodInfo createPackage;
    private readonly PropertyInfo taskResult;
    private readonly PropertyInfo outputPath;
    private readonly PropertyInfo resourceCount;
    private readonly PropertyInfo fileMap;

    internal sealed record PackageResult(string OutputPath, int ResourceCount, IReadOnlyDictionary<string, string> FileMap);

    internal YmmpxLibApi(Assembly assembly)
    {
        var service = RequireType(assembly, "YmmpxPackageService");
        var options = RequireType(assembly, "YmmpxPackagingOptions");
        var progress = RequireType(assembly, "YmmpxPackagingProgress");
        var result = RequireType(assembly, "YmmpxPackagingResult");

        optionsConstructor = options.GetConstructor(Type.EmptyTypes)
            ?? throw new MissingMethodException(options.FullName, ".ctor");
        includeUiSettings = RequireProperty(options, "IncludeProjectUiSettings", typeof(bool), requireSetter: true);
        percentage = RequireProperty(progress, "Percentage", typeof(double));
        progressConstructor = typeof(ObjectProgress<>).MakeGenericType(progress).GetConstructor([typeof(Action<object>)])
            ?? throw new MissingMethodException(typeof(ObjectProgress<>).FullName, ".ctor");

        Type[] signature = [typeof(string), typeof(string), typeof(ISet<string>), options,
            typeof(IProgress<>).MakeGenericType(progress), typeof(CancellationToken)];
        var taskType = typeof(Task<>).MakeGenericType(result);
        createPackage = service.GetMethods(BindingFlags.Public | BindingFlags.Static | BindingFlags.DeclaredOnly)
            .SingleOrDefault(method => method.Name == "CreatePackageAsync" && !method.IsGenericMethod &&
                method.ReturnType == taskType && method.GetParameters().Select(p => p.ParameterType).SequenceEqual(signature))
            ?? throw new MissingMethodException(service.FullName, "CreatePackageAsync");
        taskResult = RequireProperty(taskType, nameof(Task<object>.Result), result);
        outputPath = RequireProperty(result, "OutputPath", typeof(string));
        resourceCount = RequireProperty(result, "ResourceCount", typeof(int));
        fileMap = RequireProperty(result, "FileMap", typeof(IReadOnlyDictionary<string, string>));
    }

    internal async Task<PackageResult> CreatePackageAsync(string projectPath, string destination, ISet<string> excluded,
        IProgress<double>? progress, CancellationToken cancellationToken)
    {
        Task task;
        try
        {
            var options = optionsConstructor.Invoke(null);
            includeUiSettings.SetValue(options, true);
            object? reporter = progress is null ? null : progressConstructor.Invoke([(Action<object>)(value =>
            {
                var percent = (double)percentage.GetValue(value)!;
                if (double.IsFinite(percent))
                    progress.Report(Math.Clamp(percent / 100d, 0d, 1d));
            })]);
            task = createPackage.Invoke(null, [projectPath, destination, excluded, options, reporter, cancellationToken]) as Task
                ?? throw new InvalidOperationException("YmmpxLib がパッケージ作成タスクを返しませんでした。");
        }
        catch (TargetInvocationException ex) when (ex.InnerException is not null)
        {
            ExceptionDispatchInfo.Capture(ex.InnerException).Throw();
            throw;
        }

        await task.ConfigureAwait(false);
        var result = taskResult.GetValue(task) ?? throw new InvalidOperationException("YmmpxLib が作成結果を返しませんでした。");
        var path = (string?)outputPath.GetValue(result);
        var count = (int)resourceCount.GetValue(result)!;
        var links = (IReadOnlyDictionary<string, string>?)fileMap.GetValue(result);
        if (string.IsNullOrWhiteSpace(path) || count < 0 || links is null)
            throw new InvalidOperationException("YmmpxLib のパッケージ作成結果が不正です。");
        return new(path, count, links);
    }

    private static Type RequireType(Assembly assembly, string name)
    {
        var type = assembly.GetType("YmmpxLib." + name, throwOnError: true)!;
        if (type.Assembly != assembly || !type.IsPublic)
            throw new TypeLoadException("The required YmmpxLib type must be declared by the verified assembly.");
        return type;
    }

    private static PropertyInfo RequireProperty(Type type, string name, Type propertyType, bool requireSetter = false)
    {
        var property = type.GetProperty(name, BindingFlags.Instance | BindingFlags.Public, null, propertyType, Type.EmptyTypes, null);
        if (property?.GetMethod?.IsPublic != true || (requireSetter && property.SetMethod?.IsPublic != true))
            throw new MissingMemberException(type.FullName, name);
        return property;
    }

    private sealed class ObjectProgress<T>(Action<object> report) : IProgress<T> where T : class
    {
        public void Report(T value)
        {
            if (value is not null) report(value);
        }
    }
}
