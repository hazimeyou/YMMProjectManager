using System.IO;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using YMMProjectManager.Infrastructure;

namespace YMMProjectManager.Tests;

internal static class ProjectFileCleanupTests
{
    private const string ReviewedHost = "4.57.0.1+f7f1e4a691d0b476c5693586b4be2ce0c9b13a3c";
    private const string ProjectBody = """
        "FilePath":"C:\\projects\\input.ymmp",
        "Timelines":[ { "Items":[ { "FilePath":"..\\assets\\image.png","Effect":{"Strength":1e-09,"LayoutXml":""} } ], "Scene":{"ToolStates":null} } ],
        "Unknown":{"U":"\u0041", "LayoutXml":"", "ToolStates":null},
        "PluginMetadata":{"Relative":"..\\metadata.json"}
        """;

    internal static async Task RunAsync()
    {
        await Program.RunNamedCaseAsync("PF cleanup splices only approved redundant root properties", PreservesRawContentsAsync);
        await Program.RunNamedCaseAsync("PF cleanup keeps meaningful settings, legacy and unknown format", KeepsSettingsAsync);
        await Program.RunNamedCaseAsync("PF cleanup refuses ambiguous, malformed and non-project data", RefusesInvalidInputsAsync);
        await Program.RunNamedCaseAsync("PF cleanup refuses stale preview and leaves original unchanged", RefusesStalePreviewAsync);
        await Program.RunNamedCaseAsync("PF cleanup refuses output collision at publication", RefusesCollisionAsync);
        await Program.RunNamedCaseAsync("PF cleanup cancellation before publication retains original", ConservesSourceOnCancelAsync);
        var libraryPath = Environment.GetEnvironmentVariable("YMMPX_LIB_PATH");
        if (!File.Exists(libraryPath))
            Program.ReportConditionalSkip("PF research: YmmpxLib RemoveUiSettings unavailable");
        else
            await Program.RunNamedCaseAsync("PF research: actual YmmpxLib removes both root UI fields", () => VerifyLibraryAsync(libraryPath));
    }

    private static async Task PreservesRawContentsAsync()
    {
        using var fixture = new Fixture();
        var service = new ProjectFileCleanupService();
        var prepared = new[]
        {
            ("{ \"LayoutXml\" : \"\", \"ToolStates\" : null, " + ProjectBody + " }", "{ " + ProjectBody + " }"),
            ("{" + ProjectBody + ",\r\n \"LayoutXml\":\"\",\"ToolStates\":null \r\n}", "{" + ProjectBody + " \r\n}"),
            ("{\"ToolStates\":null, " + ProjectBody + ",\"LayoutXml\":\"\"}", "{" + ProjectBody + "}"),
            ("{ \"LayoutXml\":\"\", " + ProjectBody + ",\"ToolStates\":null,\r\n \"Keep\": true}",
                "{ " + ProjectBody + ",\"Keep\": true}"),
        };
        for (var index = 0; index < prepared.Length; index++)
        {
            var (input, expected) = prepared[index];
            var source = fixture.PathFor($"source{index}.ymmp");
            var output = fixture.PathFor($"clean{index}.ymmp");
            var encoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: index == 0);
            await File.WriteAllTextAsync(source, input, encoding);
            var before = await HashAsync(source);
            var report = await service.InspectAsync(source);
            Require(report.CanSave && report.Candidates.Count == 2, "expected two approved root properties");
            Require(report.FormatForDisplay().Contains("既定値", StringComparison.Ordinal), "reason missing");
            Require(await service.SaveAsAsync(report, output) == output, "incorrect publication path");
            Require(await HashAsync(source) == before, "source was changed");
            var after = await File.ReadAllBytesAsync(output);
            Require(after.SequenceEqual(encoding.GetPreamble().Concat(encoding.GetBytes(expected))),
                $"fixture {index}: unexpected output outside the removed root properties");
            Require(!(await service.InspectAsync(output)).CanSave, "cleanup is not idempotent");
        }
        fixture.RequireNoStaging();
    }

    private static async Task KeepsSettingsAsync()
    {
        using var fixture = new Fixture();
        var service = new ProjectFileCleanupService();
        var source = fixture.PathFor("state.ymmp");
        var input = "{" + ProjectBody + ",\"LayoutXml\":\"<state/>\"" +
            ",\"ToolStates\":{\"Plugin\":{\"SavedState\":\"{\\\"FilePath\\\":\\\"private.bin\\\"}\"}}}";
        await File.WriteAllTextAsync(source, input);
        var before = await HashAsync(source);
        var report = await service.InspectAsync(source);
        Require(report.Candidates.Count == 2 && !report.CanSave, "meaningful state was approved");
        var output = fixture.PathFor("must-not-exist.ymmp");
        await ThrowsAsync<InvalidOperationException>(() => service.SaveAsAsync(report, output));
        Require(await HashAsync(source) == before && !File.Exists(output), "meaningful project changed");

        foreach (var (value, host) in new[]
        {
            ("{\"Timelines\":[{\"Items\":[]}],\"ToolStates\":{},\"LayoutXml\":null}", ReviewedHost),
            ("{\"Timelines\":[{\"Items\":[]}],\"ToolStates\":null,\"LayoutXml\":\"\"}", "4.57.0.0"),
            ("{\"Timelines\":[{\"Items\":[]}],\"ToolStates\":null,\"LayoutXml\":\"\"}", "4.57.0.1+other-build"),
            ("{\"Timeline\":{\"Items\":[]},\"ToolStates\":null,\"LayoutXml\":\"\"}", ReviewedHost),
            ("{\"$type\":\"SomeType\",\"Timelines\":[{\"Items\":[]}],\"ToolStates\":null,\"LayoutXml\":\"\"}", ReviewedHost),
        })
        {
            var candidates = ProjectFileCleanupService.Inspect(Encoding.UTF8.GetBytes(value), host, default);
            Require(candidates.Count == 2 && candidates.All(candidate =>
                candidate.Disposition == ProjectFileCleanupDisposition.Conditional), "unsupported format was approved");
        }
        fixture.RequireNoStaging();
    }

    private static async Task RefusesInvalidInputsAsync()
    {
        using var fixture = new Fixture();
        var service = new ProjectFileCleanupService();
        var source = fixture.PathFor("invalid.ymmp");
        foreach (var input in new[]
        {
            "[]", "{}", "{\"Timelines\":[]}", "{\"Timelines\":[{\"Items\":null}]}",
            "{\"Timelines\":[{\"Items\":[]}],\"LayoutXml\":\"\",\"layoutxml\":\"<must retain/>\"}",
            "{\"Timelines\":[{\"Items\":[]}],\"Plugin\":{\"x\":1,\"x\":2}}",
        })
        {
            await File.WriteAllTextAsync(source, input);
            var before = await HashAsync(source);
            await ThrowsAsync<InvalidDataException>(() => service.InspectAsync(source));
            Require(await HashAsync(source) == before, "invalid input was changed");
        }
        await File.WriteAllTextAsync(source, "{ ");
        await ThrowsAsync<JsonException>(() => service.InspectAsync(source));
        await File.WriteAllBytesAsync(source,
            Encoding.UTF8.GetBytes("{\"Timelines\":[{\"Items\":[]}],\"Unrecognized\":\"").Concat(new byte[] { 0xff, 0x22, 0x7d }).ToArray());
        await ThrowsAsync<InvalidDataException>(() => service.InspectAsync(source));
        await ThrowsAsync<ArgumentException>(() => service.InspectAsync("relative.ymmp"));
        await ThrowsAsync<ArgumentException>(() => service.InspectAsync(source + ":other.ymmp"));
    }

    private static async Task RefusesStalePreviewAsync()
    {
        using var fixture = new Fixture();
        var source = await fixture.WriteSourceAsync();
        var service = new ProjectFileCleanupService();
        var report = await service.InspectAsync(source);
        var output = fixture.PathFor("new.ymmp");
        var secondDirectory = fixture.PathFor("different-folder");
        Directory.CreateDirectory(secondDirectory);
        await ThrowsAsync<IOException>(() => service.SaveAsAsync(report, Path.Combine(secondDirectory, "new.ymmp")));
        await ThrowsAsync<IOException>(() => service.SaveAsAsync(report, source));
        await File.AppendAllTextAsync(source, " ");
        var changed = await HashAsync(source);
        await ThrowsAsync<IOException>(() => service.SaveAsAsync(report, output));
        Require(await HashAsync(source) == changed && !File.Exists(output), "stale preview published");

        report = await service.InspectAsync(source);
        var replacement = fixture.PathFor("replacement.ymmp");
        File.Copy(source, replacement);
        File.Move(replacement, source, overwrite: true);
        await ThrowsAsync<IOException>(() => service.SaveAsAsync(report, output));
        Require(await HashAsync(source) == changed && !File.Exists(output), "physical replacement published");
        fixture.RequireNoStaging();
    }

    private static async Task RefusesCollisionAsync()
    {
        using var fixture = new Fixture();
        var source = await fixture.WriteSourceAsync();
        var output = fixture.PathFor("collision.ymmp");
        var service = new ProjectFileCleanupService(() => File.WriteAllText(output, "competing project"));
        var report = await service.InspectAsync(source);
        var before = await HashAsync(source);
        await ThrowsAsync<IOException>(() => service.SaveAsAsync(report, output));
        Require(await File.ReadAllTextAsync(output) == "competing project", "another destination changed");
        Require(await HashAsync(source) == before, "source was changed");
        fixture.RequireNoStaging();
    }

    private static async Task ConservesSourceOnCancelAsync()
    {
        using var fixture = new Fixture();
        using var cancellation = new CancellationTokenSource();
        var source = await fixture.WriteSourceAsync();
        var output = fixture.PathFor("cancel.ymmp");
        var service = new ProjectFileCleanupService(cancellation.Cancel);
        var report = await service.InspectAsync(source);
        var before = await HashAsync(source);
        await ThrowsAsync<OperationCanceledException>(() => service.SaveAsAsync(report, output, cancellation.Token));
        Require(await HashAsync(source) == before && !File.Exists(output), "cancellation changed project");
        fixture.RequireNoStaging();
    }

    private static async Task<string> HashAsync(string path) =>
        Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(path)));

    private static Task VerifyLibraryAsync(string libraryPath)
    {
        var library = Assembly.LoadFrom(libraryPath);
        var options = library.GetType("YmmpxLib.YmmpxPackagingOptions", throwOnError: true)!;
        Require(options.GetProperty("IncludeProjectUiSettings")!.GetValue(Activator.CreateInstance(options)) is true,
            "upstream's default behavior changed");
        var remove = library.GetType("YmmpxLib.YmmpxProjectJson", throwOnError: true)!
            .GetMethod("RemoveUiSettings", BindingFlags.Public | BindingFlags.Static)!;
        var root = JsonNode.Parse("{" + ProjectBody + ",\"LayoutXml\":\"<layout/>\",\"ToolStates\":{\"Plugin\":true}}");
        Require(remove.Invoke(null, [root]) is true && JsonNode.DeepEquals(root, JsonNode.Parse("{" + ProjectBody + "}")),
            "upstream changed fields other than the documented two root members");
        Require(remove.Invoke(null, [root]) is false, "upstream removal was not stable on repetition");
        return Task.CompletedTask;
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private static async Task ThrowsAsync<T>(Func<Task> action) where T : Exception
    {
        try { await action(); }
        catch (T) { return; }
        throw new InvalidOperationException($"Expected {typeof(T).Name}.");
    }

    private sealed class Fixture : IDisposable
    {
        private readonly string directory = Directory.CreateTempSubdirectory("pf-").FullName;

        internal string PathFor(string name) => Path.Combine(directory, name);

        internal async Task<string> WriteSourceAsync()
        {
            var source = PathFor("source.ymmp");
            await File.WriteAllTextAsync(source, "{" + ProjectBody + ",\"ToolStates\":null}");
            return source;
        }

        internal void RequireNoStaging() =>
            Require(!Directory.EnumerateFiles(directory, ".ymmpm-pf-*.tmp").Any(), "an owned temporary file leaked");

        public void Dispose() => Directory.Delete(directory, recursive: true);
    }
}
