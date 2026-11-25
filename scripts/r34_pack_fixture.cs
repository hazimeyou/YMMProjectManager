using System.Reflection;

// Invoked in a fresh build directory by r34_fixtures.py with the approved library.
if (args.Length != 2)
    throw new ArgumentException("Expected a synthetic project and a new package path.");

var library = Assembly.Load(File.ReadAllBytes("YmmpxLib.dll"));
var service = library.GetType("YmmpxLib.YmmpxPackageService", throwOnError: true)!;
var options = library.GetType("YmmpxLib.YmmpxPackagingOptions", throwOnError: true)!;
var progress = library.GetType("YmmpxLib.YmmpxPackagingProgress", throwOnError: true)!;
var create = service.GetMethod("CreatePackageAsync",
    [typeof(string), typeof(string), typeof(ISet<string>), options, typeof(IProgress<>).MakeGenericType(progress),
        typeof(CancellationToken)]) ?? throw new MissingMethodException("Expected YmmpxLib 1.1 packaging API.");

// YmmpxLib writes a staged package in this fresh build directory. The fixture is copied to a new name.
var staged = Path.GetFullPath("stage.ymmpx");
var createTask = (Task)create.Invoke(null, [args[0], staged, null, null, null, CancellationToken.None])!;
await createTask;
File.Copy(staged, args[1], overwrite: false);
