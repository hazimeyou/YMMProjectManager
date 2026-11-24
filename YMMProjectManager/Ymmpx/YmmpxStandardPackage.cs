using System.IO;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using YMMProjectManager.Domain.Packaging;
using static YMMProjectManager.Infrastructure.OwnedFilePublication;
using static YMMProjectManager.Domain.Packaging.YmmpxMaterialDisposition;

namespace YMMProjectManager.Infrastructure.Packaging;

internal static partial class YmmpxStandardPackage
{
    internal const string UnavailablePrefix = "unavailable-assets/";
    private const int MaxAssets = 9997;
    private const long MaxPackageBytes = 100L * 1024 * 1024 * 1024;
    private const long MaxAssetBytes = 50L * 1024 * 1024 * 1024;

    private sealed record Reference(JsonObject Owner, string Name, string RawPath, string SourcePath, bool Sequence);

    internal static async Task<YmmpxTransferPlan> InspectAsync(string sourcePath, CancellationToken token,
        IReadOnlyList<string>? additionalRoots = null, IReadOnlyList<YmmpxExclusionRule>? exclusions = null)
    {
        token.ThrowIfCancellationRequested();
        sourcePath = AssetPool.FullOrdinaryPath(Path.GetFullPath(sourcePath));
        RequireLocal(sourcePath);
        var parent = Path.GetDirectoryName(sourcePath)!;
        using var sourceFence = DirectoryFence.Open(parent, enforceShare: true);
        await using var source = YmmpxTransfer.OpenOrdinaryRead(sourcePath);
        var (project, hash) = await YmmpxTransfer.ReadProjectAsync(source, sourcePath, token).ConfigureAwait(false);
        var references = ReadReferences(project, parent);
        var roots = (additionalRoots ?? []).Select(AssetPool.FullOrdinaryPath).Append(parent)
            .Select(Path.TrimEndingDirectorySeparator).Distinct(StringComparer.Ordinal).ToArray();
        foreach (var root in roots) RequireLocal(root);
        var candidates = new Dictionary<string, YmmpxTransferCandidate>(StringComparer.OrdinalIgnoreCase);
        foreach (var group in references.GroupBy(reference => reference.SourcePath, StringComparer.OrdinalIgnoreCase))
        {
            token.ThrowIfCancellationRequested();
            var reference = group.First();
            var path = reference.SourcePath;
            var excluded = group.Any(entry => YmmpxExclusionRules.IsExcluded(entry.RawPath, parent, exclusions ?? []) ||
                YmmpxExclusionRules.IsExcluded(entry.SourcePath, parent, exclusions ?? []));
            var caseConflict = group.Any(entry => !string.Equals(entry.SourcePath, path, StringComparison.Ordinal));
            var directlyPackaged = group.Any(entry => IsFilePath(entry.Name));
            var candidate = await InspectCandidateAsync(path, excluded, caseConflict, directlyPackaged).ConfigureAwait(false);
            candidates[path] = candidate;
        }

        foreach (var path in references.Where(reference => reference.Sequence).Select(reference => reference.SourcePath)
                     .Distinct(StringComparer.OrdinalIgnoreCase))
        {
            token.ThrowIfCancellationRequested();
            var representative = candidates[path];
            if (!IsWithinRoots(path, roots) || !YmmpxTransfer.IsPortableName(representative.OriginalFileName))
                continue;
            var directory = Path.GetDirectoryName(path)!;
            if (!Directory.Exists(directory)) continue;
            var sequencePrefix = SequencePrefix(path);
            List<string> siblings;
            try
            {
                using var fence = DirectoryFence.Open(directory, enforceShare: true);
                siblings = Directory.EnumerateFiles(directory).Where(file => IsPng(file) &&
                        string.Equals(SequencePrefix(file), sequencePrefix, StringComparison.OrdinalIgnoreCase))
                    .Take(MaxAssets + 1).ToList();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                candidates[path] = representative with { Disposition = Unavailable, Reason = "連番 PNG のフォルダーを確認できません。" };
                continue;
            }
            if (siblings.Count < 2) continue;
            candidates[path] = representative with { SequenceMember = true };
            if (siblings.Count > MaxAssets || candidates.Count > MaxAssets - siblings.Count + 1)
                throw new InvalidDataException("同梱する素材が上限を超えています。");
            foreach (var sibling in siblings)
            {
                token.ThrowIfCancellationRequested();
                if (!candidates.TryGetValue(sibling, out var item))
                    item = await InspectCandidateAsync(sibling, YmmpxExclusionRules.IsExcluded(sibling, parent, exclusions ?? []),
                        false, directlyPackaged: true).ConfigureAwait(false);
                candidates[sibling] = item with { SequenceMember = true };
            }
        }

        return new(sourcePath, hash, Array.AsReadOnly(candidates.Values.ToArray()), ProjectFileEvidence.ReadPhysicalIdentity(source));

        async Task<YmmpxTransferCandidate> InspectCandidateAsync(string path, bool excluded, bool caseConflict, bool directlyPackaged)
        {
            var name = Path.IsPathFullyQualified(path) ? Path.GetFileName(path) : "unavailable";
            if (excluded) return new(path, name, null, null, Excluded, "除外設定に一致");
            if (caseConflict) return Missing("大文字と小文字が異なる参照の同一性を確認できません。");
            if (!directlyPackaged) return Missing("独立したレイヤー参照は標準 YMMPX の同梱対象にありません。");
            if (!YmmpxTransfer.IsPortableName(name) || Path.GetExtension(name).Equals(".ymmp", StringComparison.OrdinalIgnoreCase))
                return Missing("受け渡しに使用できない素材名です。");
            if (!IsWithinRoots(path, roots) || string.Equals(sourcePath, path, StringComparison.OrdinalIgnoreCase))
                return Missing("許可したローカルフォルダー内の素材を確認できません。");
            try
            {
                using var fence = DirectoryFence.Open(Path.GetDirectoryName(path)!, enforceShare: true);
                await using var material = YmmpxTransfer.OpenOrdinaryRead(path);
                if (material.Length > MaxAssetBytes) return Missing("素材がライブラリのサイズ上限を超えています。");
                var digest = await SHA256.HashDataAsync(material, token).ConfigureAwait(false);
                return new(path, name, material.Length, Convert.ToHexString(digest), Included, null);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return Missing("素材が見つからないか読み取れません。"); }

            YmmpxTransferCandidate Missing(string reason) => new(path, name, null, null, Unavailable, reason);
        }
    }

    internal static async Task<YmmpxPackageResult> CreateAsync(YmmpxTransferPlan plan, string destination,
        bool includeUiSettings, bool acceptUnavailable, CancellationToken token, IProgress<double>? progress,
        YmmpxLibBridge bridge, FileLogger logger, Func<CancellationToken, Task<IDisposable>>? pinRules = null)
    {
        token.ThrowIfCancellationRequested();
        if (plan.Assets.Any(asset => asset.Disposition == Unavailable) && !acceptUnavailable)
            throw new InvalidDataException("不足素材を確認してから続行してください。");
        if (!bridge.TryEnsureAvailable(out _)) return new() { ErrorMessage = bridge.MissingLibraryMessage };
        destination = AssetPool.FullOrdinaryPath(Path.GetFullPath(destination));
        RequireLocal(destination);
        if (!Path.GetExtension(destination).Equals(".ymmpx", StringComparison.OrdinalIgnoreCase) ||
            !YmmpxTransfer.IsPortableName(Path.GetFileName(destination)))
            throw new InvalidDataException("新しい .ymmpx の出力先を指定してください。");
        var parent = Path.GetDirectoryName(destination)!;
        using var destinationFence = DirectoryFence.Open(parent, enforceShare: true);
        if (File.Exists(destination) || Directory.Exists(destination)) throw new IOException("出力先が既に存在します。");
        var sourceDirectory = Path.GetDirectoryName(plan.ProjectPath)!;
        using var sourceFence = DirectoryFence.Open(sourceDirectory, enforceShare: true);
        await using var source = YmmpxTransfer.OpenOrdinaryRead(plan.ProjectPath);
        var (project, sha) = await YmmpxTransfer.ReadProjectAsync(source, plan.ProjectPath, token).ConfigureAwait(false);
        if (sha != plan.ProjectSha256 || ProjectFileEvidence.ReadPhysicalIdentity(source) != plan.ProjectIdentity)
            throw new InvalidDataException("確認後にプロジェクトが変更されました。");
        var references = ReadReferences(project, sourceDirectory);
        var projectName = Path.GetFileName(plan.ProjectPath);
        if (!YmmpxTransfer.IsPortableName(projectName) ||
            project.Any(field => field.Key.Equals(YmmpxTransfer.EvidenceProperty, StringComparison.OrdinalIgnoreCase)))
            throw new InvalidDataException("プロジェクト名または既存の受け渡し証跡を確認してください。");

        using var work = OwnedStagingDirectory.Reserve(parent, ".ymmpm-package-input-");
        using var workHandle = work.PinRecoveryPath();
        var files = new Dictionary<string, (ulong Volume, Guid Id)>(StringComparer.Ordinal);
        var directories = new Dictionary<string, (ulong Volume, Guid Id)>(StringComparer.Ordinal);
        var directoryPins = new List<Microsoft.Win32.SafeHandles.SafeFileHandle>();
        var filePins = new List<FileStream>();
        var bySourcePath = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var omitted = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        Microsoft.Win32.SafeHandles.SafeFileHandle? sequenceRoot = null;
        var stagedDirectories = new Dictionary<string, string>(StringComparer.Ordinal);
        var package = Path.Combine(work.Path, "candidate.ymmpx");
        try
        {
            long totalBytes = 0;
            var knownMaterial = new List<(long Length, string Sha256)>();
            foreach (var candidate in plan.Assets)
            {
                token.ThrowIfCancellationRequested();
                var ordinal = $"asset-{bySourcePath.Count:x8}";
                if (candidate.Disposition != Included && !candidate.SequenceMember)
                {
                    var placeholder = UnavailablePrefix + ordinal + "/" +
                        (YmmpxTransfer.IsPortableName(candidate.OriginalFileName) ? candidate.OriginalFileName : "unavailable");
                    bySourcePath.Add(candidate.SourcePath, placeholder);
                    omitted.Add(placeholder);
                    continue;
                }
                // Group the original filenames of PNG companions in an owned directory. Reserve
                // excluded names as zero-byte witnesses so the native file-count threshold still
                // detects a sequence when only one approved frame remains after filtering.
                var group = candidate.SequenceMember ? Path.GetDirectoryName(candidate.SourcePath)! : candidate.SourcePath;
                if (!stagedDirectories.TryGetValue(group, out var directory))
                {
                    if (candidate.SequenceMember && sequenceRoot is null)
                    {
                        const string sequenceDirectory = "unavailable-assets";
                        sequenceRoot = OwnedStagingDirectory.CreateAndPin(workHandle, sequenceDirectory);
                        directoryPins.Add(sequenceRoot);
                        directories.Add(Path.Combine(work.Path, sequenceDirectory), ProjectFileEvidence.ReadPhysicalIdentity(sequenceRoot));
                    }
                    var parentHandle = candidate.SequenceMember ? sequenceRoot! : workHandle;
                    var handle = OwnedStagingDirectory.CreateAndPin(parentHandle, ordinal);
                    directoryPins.Add(handle);
                    directory = candidate.SequenceMember
                        ? Path.Combine(work.Path, UnavailablePrefix.TrimEnd('/'), ordinal)
                        : Path.Combine(work.Path, ordinal);
                    directories.Add(directory, ProjectFileEvidence.ReadPhysicalIdentity(handle));
                    stagedDirectories.Add(group, directory);
                }
                // A native archive with one of the historical v2 reserved names would be
                // ambiguous with a legacy transfer whose sidecar was removed.
                var copyName = YmmpxTransfer.IsLibraryMemberName(candidate.OriginalFileName)
                    ? ordinal + Path.GetExtension(candidate.OriginalFileName)
                    : candidate.OriginalFileName;
                var copyPath = Path.Combine(directory, copyName);
                var relative = Path.GetRelativePath(work.Path, copyPath);
                bySourcePath.Add(candidate.SourcePath, relative);
                if (candidate.Disposition != Included)
                {
                    omitted.Add(relative);
                    if (YmmpxTransfer.IsPortableName(candidate.OriginalFileName))
                    {
                        await using (var witness = CreateOwnedOutputFile(copyPath))
                            files.Add(copyPath, ProjectFileEvidence.ReadPhysicalIdentity(witness));
                        var witnessPin = YmmpxTransfer.OpenOrdinaryRead(copyPath);
                        filePins.Add(witnessPin);
                        if (ProjectFileEvidence.ReadPhysicalIdentity(witnessPin) != files[copyPath] || witnessPin.Length != 0)
                            throw new IOException("連番 PNG の作業ファイルが変更されました。");
                    }
                    continue;
                }
                using var materialFence = DirectoryFence.Open(Path.GetDirectoryName(candidate.SourcePath)!, enforceShare: true);
                await using var material = YmmpxTransfer.OpenOrdinaryRead(candidate.SourcePath);
                if (material.Length != candidate.Length || material.Length > MaxPackageBytes - totalBytes)
                    throw new InvalidDataException("確認後に素材サイズが変更されたか、合計サイズが上限を超えています。");
                totalBytes += material.Length;
                await using (var copy = CreateOwnedOutputFile(copyPath))
                {
                    files.Add(copyPath, ProjectFileEvidence.ReadPhysicalIdentity(copy));
                    await material.CopyToAsync(copy, token).ConfigureAwait(false);
                }
                var pin = YmmpxTransfer.OpenOrdinaryRead(copyPath);
                filePins.Add(pin);
                var hash = Convert.ToHexString(await SHA256.HashDataAsync(pin, token).ConfigureAwait(false));
                if (ProjectFileEvidence.ReadPhysicalIdentity(pin) != files[copyPath] || hash != candidate.Sha256)
                    throw new InvalidDataException("確認後に素材が変更されました。");
                knownMaterial.Add((material.Length, hash));
            }

            foreach (var reference in references)
                reference.Owner[reference.Name] = bySourcePath[reference.SourcePath].Replace('\\', '/');
            SetImplicitLayerPaths(references);
            var stagedProject = Path.Combine(work.Path, projectName);
            var projectBytes = JsonSerializer.SerializeToUtf8Bytes(project);
            await using (var snapshot = CreateOwnedOutputFile(stagedProject))
            {
                files.Add(stagedProject, ProjectFileEvidence.ReadPhysicalIdentity(snapshot));
                await snapshot.WriteAsync(projectBytes, token).ConfigureAwait(false);
            }
            var projectPin = YmmpxTransfer.OpenOrdinaryRead(stagedProject);
            filePins.Add(projectPin);
            var projectDigest = await SHA256.HashDataAsync(projectPin, token).ConfigureAwait(false);
            if (ProjectFileEvidence.ReadPhysicalIdentity(projectPin) != files[stagedProject] ||
                !SHA256.HashData(projectBytes).AsSpan().SequenceEqual(projectDigest))
                throw new IOException("同梱前に作業中のプロジェクトが変更されました。");

            var result = await bridge.CreatePackageAsync(stagedProject, package, omitted, token, progress,
                includeUiSettings, approvedSnapshot: true).ConfigureAwait(false);
            if (!result.Success) return result;

            await using var output = new FileStream(YmmpxPackageReader.OpenPackageForPublication(package), FileAccess.Read);
            files.Add(package, result.PublishedIdentity ?? throw new IOException("YMMPX 出力を識別できません。"));
            if (ProjectFileEvidence.ReadPhysicalIdentity(output) != files[package])
                throw new IOException("YMMPX 出力が置換されました。");
            await VerifyResourcesAsync(output, knownMaterial, token).ConfigureAwait(false);
            using var rulesGuard = pinRules is null ? null : await pinRules(token).ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            RenameOpenedPath(output.SafeFileHandle, destination);
            files.Remove(package);
            return new()
            {
                Success = true,
                OutputPath = destination,
                PublishedIdentity = ProjectFileEvidence.ReadPhysicalIdentity(output),
                DetectedMaterialCount = plan.Assets.Count,
                PackagedMaterialCount = knownMaterial.Count,
                MissingMaterialCount = plan.Assets.Count(asset => asset.Disposition == Unavailable),
            };
        }
        finally
        {
            foreach (var pin in filePins) pin.Dispose();
            foreach (var pin in directoryPins) pin.Dispose();
            Cleanup(files, isDirectory: false);
            Cleanup(directories, isDirectory: true);
            workHandle.Dispose();
            try
            {
                if (!work.DeleteIfEmpty()) logger.Info("YMMPX の未確認な作業データを保持しました。");
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            { logger.Info($"YMMPX の作業フォルダーを保持しました。error={ex.GetType().Name}"); }
        }

        void Cleanup(Dictionary<string, (ulong Volume, Guid Id)> paths, bool isDirectory)
        {
            foreach (var (path, identity) in paths.OrderByDescending(entry => entry.Key.Length))
            {
                try
                {
                    using var owned = OpenOrdinaryPath(path, expectDirectory: isDirectory, allowRename: true);
                    if (ProjectFileEvidence.ReadPhysicalIdentity(owned) == identity) TryDeleteOpenedPath(owned);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                { logger.Info($"YMMPX 作業データを保持しました。error={ex.GetType().Name}"); }
            }
        }
    }

    private static async Task VerifyResourcesAsync(Stream input, IEnumerable<(long Length, string Sha256)> selected, CancellationToken token)
    {
        var expected = selected.GroupBy(item => item).ToDictionary(group => group.Key, group => group.Count());
        using var archive = new ZipArchive(input, ZipArchiveMode.Read, leaveOpen: true);
        foreach (var entry in archive.Entries.Where(entry => entry.FullName.StartsWith("resources/", StringComparison.Ordinal)))
        {
            token.ThrowIfCancellationRequested();
            await using var data = entry.Open();
            var key = (entry.Length, Convert.ToHexString(await SHA256.HashDataAsync(data, token).ConfigureAwait(false)));
            if (!expected.TryGetValue(key, out var remaining))
                throw new InvalidDataException("確認していない素材が YMMPX に含まれています。");
            if (remaining == 1) expected.Remove(key);
            else expected[key]--;
        }
        if (expected.Count != 0) throw new InvalidDataException("確認済みの素材が YMMPX に不足しています。");
    }

    private static IReadOnlyList<Reference> ReadReferences(JsonObject project, string directory)
    {
        var references = new List<Reference>();
        Visit(project);
        return references;

        void Visit(JsonNode node)
        {
            if (node is JsonObject obj)
            {
                var isVideo = obj["$type"] is JsonValue type && type.TryGetValue<string>(out var typeName) &&
                    typeName.StartsWith("YukkuriMovieMaker.Project.Items.VideoItem,", StringComparison.Ordinal);
                foreach (var (name, child) in obj.ToArray())
                {
                    if (YmmpxTransfer.IsReferenceProperty(name) && child is JsonValue value &&
                        value.TryGetValue<string>(out var path) && !string.IsNullOrWhiteSpace(path))
                    {
                        if (ReferenceEquals(obj, project) && IsFilePath(name))
                        {
                            obj[name] = "";
                            continue;
                        }
                        var resolved = PackagingDetector.ResolveMaterialPath(
                            Environment.ExpandEnvironmentVariables(path.Trim().Trim('"')), directory) ?? path;
                        var sequence = isVideo && name == "FilePath" && IsPng(resolved) && SequencePrefix(resolved) is not null;
                        references.Add(new(obj, name, path, resolved, sequence));
                        if (references.Count > MaxAssets) throw new InvalidDataException("素材参照が上限を超えています。");
                    }
                    else if (child is not null) Visit(child);
                }
            }
            else if (node is JsonArray array)
                foreach (var child in array)
                    if (child is not null) Visit(child);
        }
    }

    private static void SetImplicitLayerPaths(IEnumerable<Reference> references)
    {
        foreach (var reference in references)
        {
            var owner = reference.Owner;
            var value = owner[reference.Name]!.GetValue<string>();
            if (IsFilePath(reference.Name) && Path.GetExtension(value).Equals(".psd", StringComparison.OrdinalIgnoreCase) &&
                !value.StartsWith(UnavailablePrefix, StringComparison.Ordinal) &&
                !owner.Any(field => field.Key.Equals("EnableLayersFilePath", StringComparison.OrdinalIgnoreCase)) &&
                owner.Any(field => field.Key.Equals("EnableLayers", StringComparison.OrdinalIgnoreCase) ||
                    field.Key.Equals("EnableLayerPaths", StringComparison.OrdinalIgnoreCase)))
                owner["EnableLayersFilePath"] = value;
        }
    }

    private static bool IsFilePath(string name) => name.Equals("FilePath", StringComparison.OrdinalIgnoreCase);
    private static bool IsPng(string path) => Path.GetExtension(path).Equals(".png", StringComparison.OrdinalIgnoreCase);
    private static string? SequencePrefix(string path)
    {
        var match = PngSequenceStem().Match(Path.GetFileNameWithoutExtension(path));
        return match.Success ? match.Groups["prefix"].Value : null;
    }

    [GeneratedRegex("^(?<prefix>.*?)\\d+$", RegexOptions.CultureInvariant)]
    private static partial Regex PngSequenceStem();

    private static bool IsWithinRoots(string path, IEnumerable<string> roots) =>
        Path.IsPathFullyQualified(path) && roots.Any(root =>
            path.StartsWith(Path.EndsInDirectorySeparator(root) ? root : root + Path.DirectorySeparatorChar, StringComparison.Ordinal));

    private static void RequireLocal(string path)
    {
        if (path.StartsWith(@"\\", StringComparison.Ordinal) || Path.GetPathRoot(path) is not string root ||
            new DriveInfo(root).DriveType != DriveType.Fixed)
            throw new IOException("既存のローカルフォルダーを指定してください。");
    }
}
