using System.IO;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;
using System.Runtime.Loader;
using YMMProjectManager.Infrastructure;
using YMMProjectManager.Infrastructure.Packaging;

internal static class R30ReflectionTests
{
    internal static async Task RunAsync(string workRoot)
    {
        var root = Directory.CreateDirectory(Path.Combine(workRoot, "ymmpx-resolution")).FullName;
        var logger = new FileLogger(Path.Combine(root, "tests.log"));
        var configured = Path.Combine(root, "YmmpxLib.dll");
        var bridge = new YmmpxLibBridge(logger, configured, root);

        await Program.RunNamedCaseAsync("YmmpxLib probe distinguishes missing and corrupt DLLs", async () =>
        {
            Require(!bridge.TryEnsureAvailable(out var missing) && missing is null &&
                bridge.LastStatus == YmmpxLibStatus.NotInstalled, "Missing library classification failed.");
            await File.WriteAllBytesAsync(configured, [1, 2, 3]);
            Require(!bridge.TryEnsureAvailable(out var bad) && bad is null &&
                bridge.LastStatus == YmmpxLibStatus.Invalid, "Corrupt image was accepted.");
            File.Delete(configured);
        });
        await Program.RunNamedCaseAsync("YmmpxLib rejects unrelated assemblies and unapproved bytes", () =>
        {
            File.Copy(typeof(R30ReflectionTests).Assembly.Location, configured);
            Require(!bridge.TryEnsureAvailable(out _) && bridge.LastStatus == YmmpxLibStatus.Invalid,
                "An unrelated assembly was accepted.");
            File.Delete(configured);
            CreateMetadataFixture(configured, new(1, 1, 0, 0));
            Require(!bridge.TryEnsureAvailable(out _) && bridge.LastStatus == YmmpxLibStatus.Invalid,
                "A claimed compatible version bypassed the content check.");
            File.Delete(configured);
            return Task.CompletedTask;
        });
        await Program.RunNamedCaseAsync("YmmpxLib rejects unsupported versions and reports a sharing failure", () =>
        {
            CreateMetadataFixture(configured, new(1, 0, 0, 0));
            Require(!bridge.TryEnsureAvailable(out _) && bridge.LastStatus == YmmpxLibStatus.Unsupported,
                "An unsupported version was not distinguished.");
            using (new FileStream(configured, FileMode.Open, FileAccess.Read, FileShare.None))
                Require(!bridge.TryEnsureAvailable(out _) && bridge.LastStatus == YmmpxLibStatus.LoadFailed,
                    "A corresponding DLL that is inaccessible was not reported.");
            File.Delete(configured);
            return Task.CompletedTask;
        });
        await Program.RunNamedCaseAsync("YmmpxLib reports incompatible APIs before attempting a package", () =>
        {
            var incompatible = new YmmpxLibBridge(logger, typeof(object).Assembly);
            Require(!incompatible.TryEnsureAvailable(out var loaded) && loaded is null &&
                incompatible.LastStatus == YmmpxLibStatus.Unsupported, "The incompatible API was accepted.");
            return Task.CompletedTask;
        });

        await Program.RunNamedCaseAsync("Legacy YmmpxLib folders do not count as a supported plugin installation", () =>
        {
            Directory.CreateDirectory(Path.Combine(root, "user", "plugin", "YmmpxLib"));
            WithoutLibraryOverride(() =>
            {
                var installed = new YmmpxLibBridge(logger, baseDirectory: root);
                Require(!installed.TryEnsureAvailable(out _) && installed.LastStatus == YmmpxLibStatus.NotInstalled,
                    "A legacy folder was reported as supported.");
            });
            return Task.CompletedTask;
        });

    }

    internal static async Task RunRealAsync(string workRoot, string release)
    {
        var root = Directory.CreateDirectory(Path.Combine(workRoot, "ymmpx-official-core")).FullName;
        var logger = new FileLogger(Path.Combine(root, "tests.log"));
        var configured = Path.Combine(root, "YmmpxLib.dll");
        var bridge = new YmmpxLibBridge(logger, configured, root);
        await Program.RunNamedCaseAsync("YmmpxLib loads release bytes without changing default binding", async () =>
        {
            var hostAssembly = AssemblyLoadContext.Default.Assemblies.FirstOrDefault(assembly => assembly.GetName().Name == "YmmpxLib");
            File.Copy(release, configured);
            Require(YmmpxLibResolver.TryResolveAssembly(out var library, out var actual, out _, root, configured) &&
                actual == configured && library is not null && AssemblyLoadContext.GetLoadContext(library) != AssemblyLoadContext.Default,
                "The verified release did not load in an isolated context.");
            Require(bridge.TryEnsureAvailable(out _) && bridge.LastStatus == YmmpxLibStatus.Ready, "The official creation API was not found.");
            Require(AssemblyLoadContext.Default.Assemblies.FirstOrDefault(assembly => assembly.GetName().Name == "YmmpxLib") == hostAssembly,
                "Default-context library resolution changed.");

            await File.WriteAllBytesAsync(configured, [1, 2, 3]);
            Require(!bridge.TryEnsureAvailable(out _) && bridge.LastStatus == YmmpxLibStatus.Invalid,
                "A replaced DLL was treated as the cached release.");
            File.Delete(configured);
        });

        await Program.RunNamedCaseAsync("A loose official core does not count as a standalone plugin installation", () =>
        {
            var installedRoot = Directory.CreateDirectory(Path.Combine(root, "user", "plugin", "YmmpxLibPlugin")).FullName;
            File.Copy(release, Path.Combine(installedRoot, "YmmpxLib.dll"));
            WithoutLibraryOverride(() =>
            {
                var installed = new YmmpxLibBridge(logger, baseDirectory: root);
                Require(!installed.TryEnsureAvailable(out _) && installed.LastStatus == YmmpxLibStatus.Invalid,
                    "A loose DLL was reported as an installed plugin.");
            });
            return Task.CompletedTask;
        });
    }

    internal static async Task RunInstalledAsync(string workRoot)
    {
        var archive = Environment.GetEnvironmentVariable("YMM_TEST_YMMPX_PLUGIN_ARCHIVE");
        if (string.IsNullOrWhiteSpace(archive) || !File.Exists(archive))
        {
            Program.ReportConditionalSkip("R30 official plugin archive fixture unavailable");
            return;
        }

        await Program.RunNamedCaseAsync("YmmpxLib verifies the standalone plugin's entry DLL and release core", () =>
        {
            var root = Directory.CreateDirectory(Path.Combine(workRoot, "ymmpx-official-install")).FullName;
            var directory = Directory.CreateDirectory(Path.Combine(root, "user", "plugin", "YmmpxLibPlugin")).FullName;
            var core = Path.Combine(directory, "YmmpxLib.dll");
            var markerPath = Path.Combine(directory, "YmmpxLibPlugin.dll");
            using var file = File.OpenRead(archive);
            Require(Convert.ToHexString(SHA256.HashData(file)) == "9D5EE82C96D6A5ED57D805306CFE037A6669F0D3D15D6C28919E1A59158680DA",
                "Unexpected library install fixture.");
            file.Position = 0;
            using var package = new ZipArchive(file);
            (package.GetEntry("YmmpxLib.dll") ?? throw new InvalidOperationException("Core DLL is missing.")).ExtractToFile(core);
            (package.GetEntry("YmmpxLibPlugin.dll") ?? throw new InvalidOperationException("Plugin entry DLL is missing.")).ExtractToFile(markerPath);

            var logger = new FileLogger(Path.Combine(root, "tests.log"));
            WithoutLibraryOverride(() =>
            {
                var installed = new YmmpxLibBridge(logger, baseDirectory: root);
                Require(installed.TryEnsureAvailable(out var found) && installed.LastStatus == YmmpxLibStatus.Ready && found == core,
                    "The official installed layout was not recognized.");
                File.WriteAllBytes(markerPath, [1, 2, 3]);
                Require(!installed.TryEnsureAvailable(out _) && installed.LastStatus == YmmpxLibStatus.Invalid,
                    "An altered entry DLL was reported as an installed plugin.");
            });
            return Task.CompletedTask;
        });
    }

    private static void WithoutLibraryOverride(Action test)
    {
        const string name = "YMMPX_LIB_PATH";
        var previous = Environment.GetEnvironmentVariable(name);
        Environment.SetEnvironmentVariable(name, null);
        try { test(); }
        finally { Environment.SetEnvironmentVariable(name, previous); }
    }

    private static void CreateMetadataFixture(string path, Version version)
    {
        var metadata = new MetadataBuilder();
        metadata.AddModule(0, metadata.GetOrAddString("YmmpxLib.dll"), metadata.GetOrAddGuid(Guid.NewGuid()), default, default);
        metadata.AddAssembly(metadata.GetOrAddString("YmmpxLib"), version, default, default, 0, AssemblyHashAlgorithm.None);
        metadata.AddTypeDefinition(TypeAttributes.NotPublic, default, metadata.GetOrAddString("<Module>"), default,
            MetadataTokens.FieldDefinitionHandle(1), MetadataTokens.MethodDefinitionHandle(1));
        var image = new ManagedPEBuilder(new PEHeaderBuilder(imageCharacteristics: Characteristics.Dll),
            new MetadataRootBuilder(metadata), new BlobBuilder());
        var bytes = new BlobBuilder();
        image.Serialize(bytes);
        using var file = new FileStream(path, FileMode.CreateNew, FileAccess.Write);
        bytes.WriteContentTo(file);
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
