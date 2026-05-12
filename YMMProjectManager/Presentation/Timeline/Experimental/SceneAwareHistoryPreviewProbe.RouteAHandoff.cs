using System.Text.Json;

namespace YMMProjectManager.Presentation.Timeline.Experimental;

internal static partial class SceneAwareHistoryPreviewProbe
{
    private static SceneAwareRouteADetailHandoffCandidate BuildRouteADetailHandoffCandidate(
        SceneAwareHistoryPreviewItem? selectedItem,
        string runtimeStableHash)
    {
        if (selectedItem is null)
        {
            return new SceneAwareRouteADetailHandoffCandidate(
                Prepared: false, CanOpen: false, Reason: "No selected history preview item",
                SourceKind: "", SourceFileName: "", SourcePath: "", SnapshotId: null, CompareSessionId: null,
                OldSnapshotId: null, NewSnapshotId: null,
                RouteValidationReportPath: null, PreviewWorkspaceStatePath: null, ComparisonHistoryPath: null,
                RuntimeStableHash: runtimeStableHash, HistoryStableHash: "", Score: 0, Confidence: "None",
                AvailableFields: [], MissingFields: ["sourcePath"],
                Warnings: ["Open RouteA Detail Diff is not enabled in Step 7A (dry-run only)."],
                SummaryText: "routeA handoff: not prepared");
        }

        var extracted = ExtractRouteAHandoffFields(selectedItem.SourcePath);
        var available = new List<string>();
        var missing = new List<string>();
        void Track(string name, string? value) { if (string.IsNullOrWhiteSpace(value)) missing.Add(name); else available.Add(name); }
        Track("snapshotId", extracted.SnapshotId);
        Track("compareSessionId", extracted.CompareSessionId);
        Track("oldSnapshotId", extracted.OldSnapshotId);
        Track("newSnapshotId", extracted.NewSnapshotId);
        Track("routeValidationReportPath", extracted.RouteValidationReportPath);
        Track("previewWorkspaceStatePath", extracted.PreviewWorkspaceStatePath);
        Track("comparisonHistoryPath", extracted.ComparisonHistoryPath);

        var hasSnapshotPair = !string.IsNullOrWhiteSpace(extracted.OldSnapshotId) && !string.IsNullOrWhiteSpace(extracted.NewSnapshotId);
        var hasWorkspaceAndHistory = !string.IsNullOrWhiteSpace(extracted.PreviewWorkspaceStatePath) && !string.IsNullOrWhiteSpace(extracted.ComparisonHistoryPath);
        var canOpen = !string.IsNullOrWhiteSpace(selectedItem.SourcePath)
            && (selectedItem.Confidence is "High" or "Medium")
            && (!string.IsNullOrWhiteSpace(extracted.CompareSessionId) || !string.IsNullOrWhiteSpace(extracted.SnapshotId) || hasSnapshotPair || hasWorkspaceAndHistory);
        var reason = canOpen ? "RouteA handoff metadata is sufficient (dry-run only)" : "Insufficient RouteA handoff metadata";
        var warnings = new List<string>();
        warnings.AddRange(extracted.Warnings);
        warnings.Add("Open RouteA Detail Diff is not enabled in Step 7A (dry-run only).");

        return new SceneAwareRouteADetailHandoffCandidate(
            Prepared: true, CanOpen: canOpen, Reason: reason,
            SourceKind: selectedItem.SourceKind, SourceFileName: selectedItem.SourceFileName, SourcePath: selectedItem.SourcePath,
            SnapshotId: extracted.SnapshotId, CompareSessionId: extracted.CompareSessionId, OldSnapshotId: extracted.OldSnapshotId, NewSnapshotId: extracted.NewSnapshotId,
            RouteValidationReportPath: extracted.RouteValidationReportPath, PreviewWorkspaceStatePath: extracted.PreviewWorkspaceStatePath,
            ComparisonHistoryPath: extracted.ComparisonHistoryPath, RuntimeStableHash: runtimeStableHash, HistoryStableHash: selectedItem.StableHash,
            Score: selectedItem.Score, Confidence: selectedItem.Confidence, AvailableFields: available, MissingFields: missing, Warnings: warnings,
            SummaryText: $"prepared=True canOpen={canOpen} reason={reason}");
    }

    private static SceneAwareRouteAExtractedFields ExtractRouteAHandoffFields(string sourcePath)
    {
        if (string.IsNullOrWhiteSpace(sourcePath) || !File.Exists(sourcePath))
            return new SceneAwareRouteAExtractedFields(null, null, null, null, null, null, null, ["source file not found"]);

        try
        {
            const long maxFileBytes = 5 * 1024 * 1024;
            var fi = new FileInfo(sourcePath);
            if (fi.Length > maxFileBytes)
                return new SceneAwareRouteAExtractedFields(null, null, null, null, null, null, null, ["source file is larger than 5MB and was skipped"]);

            using var doc = JsonDocument.Parse(File.ReadAllText(sourcePath));
            var flat = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            FlattenJson(doc.RootElement, flat, "", 0, 5, 2000);
            return new SceneAwareRouteAExtractedFields(
                TryGetValue(flat, "snapshotId", "SnapshotId", "snapshot", "Snapshot"),
                TryGetValue(flat, "compareSessionId", "CompareSessionId", "sessionId", "SessionId", "compare", "Compare", "comparison", "Comparison"),
                TryGetValue(flat, "oldSnapshotId", "OldSnapshotId", "oldSnapshot", "OldSnapshot", "before", "Before", "left", "Left", "baseSnapshot"),
                TryGetValue(flat, "newSnapshotId", "NewSnapshotId", "newSnapshot", "NewSnapshot", "after", "After", "right", "Right", "targetSnapshot"),
                TryGetValue(flat, "routeValidationReportPath", "RouteValidationReportPath"),
                TryGetValue(flat, "previewWorkspaceStatePath", "PreviewWorkspaceStatePath"),
                TryGetValue(flat, "comparisonHistoryPath", "ComparisonHistoryPath"),
                []);
        }
        catch (Exception ex)
        {
            return new SceneAwareRouteAExtractedFields(null, null, null, null, null, null, null, [$"extract failed: {ex.GetType().Name}"]);
        }
    }

    private static SceneAwareRouteADetailHandoffGap BuildRouteAHandoffGap(SceneAwareRouteADetailHandoffCandidate handoff)
    {
        var critical = new List<string>();
        var important = new List<string>();
        var optional = new List<string>();
        if (string.IsNullOrWhiteSpace(handoff.CompareSessionId)) critical.Add("compareSessionId");
        if (string.IsNullOrWhiteSpace(handoff.SnapshotId) && (string.IsNullOrWhiteSpace(handoff.OldSnapshotId) || string.IsNullOrWhiteSpace(handoff.NewSnapshotId))) critical.Add("snapshotId or snapshot pair");
        if (string.IsNullOrWhiteSpace(handoff.SourcePath) || !File.Exists(handoff.SourcePath)) critical.Add("sourcePath");
        if (string.IsNullOrWhiteSpace(handoff.PreviewWorkspaceStatePath)) important.Add("previewWorkspaceStatePath");
        if (string.IsNullOrWhiteSpace(handoff.ComparisonHistoryPath)) important.Add("comparisonHistoryPath");
        if (string.IsNullOrWhiteSpace(handoff.RouteValidationReportPath)) important.Add("routeValidationReportPath");
        if (string.IsNullOrWhiteSpace(handoff.HistoryStableHash)) optional.Add("sceneAwareStableHash");
        return new SceneAwareRouteADetailHandoffGap(critical, important, optional, ["compareSessionId", "oldSnapshotId", "newSnapshotId", "previewWorkspaceStatePath", "sceneAwareStableHash", "sourceKind"]);
    }

    private static SceneAwareMetadataBlock BuildSceneAwareMetadata(
        DateTimeOffset now,
        SceneAwareSceneIdentityCandidate sceneIdentity,
        SceneAwareTimelineFingerprint fingerprint,
        SceneAwareRouteADetailHandoffCandidate handoff)
        => new(
            1,
            now,
            new SceneAwareMetadataSource("RouteB", "SceneAwareHistoryPreview", true, true),
            new SceneAwareMetadataSceneIdentity(sceneIdentity.SceneName, sceneIdentity.SceneIndex, sceneIdentity.TimelineFingerprintHash, sceneIdentity.Confidence),
            new SceneAwareMetadataTimelineFingerprint(fingerprint.StableHash, fingerprint.ItemCount, fingerprint.LayerCount, fingerprint.MinFrame, fingerprint.MaxFrame, fingerprint.ItemTypeHistogram, fingerprint.TextPresenceHistogram),
            new SceneAwareMetadataRouteAHandoff(handoff.CompareSessionId, handoff.SnapshotId, null, null, handoff.PreviewWorkspaceStatePath, handoff.ComparisonHistoryPath, handoff.RouteValidationReportPath),
            new SceneAwareMetadataPrivacy(true, true, true));

    private static SceneAwareRouteAHandoffMetadata BuildRouteAHandoffMetadata(
        SceneAwareRouteADetailHandoffCandidate handoff,
        SceneAwareSceneIdentityCandidate sceneIdentity,
        DateTimeOffset _)
        => new(
            SchemaVersion: 1,
            Prepared: handoff.Prepared,
            Confidence: handoff.Confidence,
            CompareSession: new SceneAwareCompareSessionReference(handoff.CompareSessionId, null),
            SnapshotPair: new SceneAwareSnapshotReference(handoff.OldSnapshotId, handoff.NewSnapshotId, null, null),
            PreviewWorkspace: new SceneAwarePreviewWorkspaceReference(handoff.PreviewWorkspaceStatePath, handoff.RouteValidationReportPath, handoff.ComparisonHistoryPath),
            SceneAwareLink: new SceneAwareLinkReference(handoff.RuntimeStableHash, handoff.HistoryStableHash, sceneIdentity.SceneName, sceneIdentity.SceneIndex),
            Compatibility: new SceneAwareMetadataCompatibility(ReadOnly: true, DefaultDisabled: true, NonBreaking: true));

    private static SceneAwareRouteAOpenReadiness BuildRouteAOpenReadiness(
        SceneAwareRouteADetailHandoffCandidate handoff,
        SceneAwareRouteADetailHandoffGap gap,
        string bestConfidence,
        SceneAwareSnapshotPairResolution? snapshotPairResolution = null)
    {
        var hasSnapshotPair = (!string.IsNullOrWhiteSpace(handoff.OldSnapshotId) && !string.IsNullOrWhiteSpace(handoff.NewSnapshotId))
            || (snapshotPairResolution?.Resolved ?? false);
        var hasWorkspace = !string.IsNullOrWhiteSpace(handoff.PreviewWorkspaceStatePath);
        var hasHistory = !string.IsNullOrWhiteSpace(handoff.ComparisonHistoryPath);
        var canOpen = handoff.Prepared
            && (bestConfidence is "High" or "Medium")
            && (!string.IsNullOrWhiteSpace(handoff.CompareSessionId) || !string.IsNullOrWhiteSpace(handoff.SnapshotId) || hasSnapshotPair || (hasWorkspace && hasHistory));
        return new SceneAwareRouteAOpenReadiness(
            Prepared: handoff.Prepared,
            CanOpen: canOpen,
            Confidence: bestConfidence,
            HasCompareSessionId: !string.IsNullOrWhiteSpace(handoff.CompareSessionId),
            HasSnapshotPair: hasSnapshotPair,
            HasPreviewWorkspaceState: hasWorkspace,
            HasComparisonHistory: hasHistory,
            MissingCriticalFields: gap.CriticalMissingFields,
            MissingImportantFields: gap.ImportantMissingFields,
            Warnings: handoff.Warnings);
    }

    private static SceneAwareRouteAHandoffMetadata ApplyResolvedSnapshotPair(SceneAwareRouteAHandoffMetadata metadata, SceneAwareSnapshotPairResolution resolved)
    {
        if (!resolved.Resolved) return metadata;
        return metadata with
        {
            SnapshotPair = metadata.SnapshotPair with
            {
                OldSnapshotId = resolved.OldSnapshotHash,
                NewSnapshotId = resolved.NewSnapshotHash,
                OldSnapshotPath = string.IsNullOrWhiteSpace(resolved.OldSnapshotHash) ? null : $"{resolved.SnapshotRepositoryPath}#SnapshotHash={resolved.OldSnapshotHash}",
                NewSnapshotPath = string.IsNullOrWhiteSpace(resolved.NewSnapshotHash) ? null : $"{resolved.SnapshotRepositoryPath}#SnapshotHash={resolved.NewSnapshotHash}",
            },
            PreviewWorkspace = metadata.PreviewWorkspace with
            {
                ComparisonHistoryPath = resolved.ComparisonHistoryPath ?? metadata.PreviewWorkspace.ComparisonHistoryPath
            }
        };
    }

    private static SceneAwareSnapshotPairResolution ResolveSnapshotPairFromHistory(string diagnosticsDirectory)
    {
        var comp = Directory.GetFiles(diagnosticsDirectory, "*comparison-history*.json").OrderByDescending(File.GetLastWriteTime).FirstOrDefault();
        var repo = Directory.GetFiles(diagnosticsDirectory, "*snapshot-repository*.json").OrderByDescending(File.GetLastWriteTime).FirstOrDefault();
        var debug = new SceneAwareSnapshotPairResolverDebug(
            ComparisonHistoryFileFound: !string.IsNullOrWhiteSpace(comp),
            SnapshotRepositoryFileFound: !string.IsNullOrWhiteSpace(repo),
            ComparisonHistoryRootKind: "Unknown",
            SnapshotRepositoryRootKind: "Unknown",
            ComparisonEntryCount: 0,
            SnapshotEntryCount: 0,
            SelectedComparisonEntryIndex: null,
            SelectedComparisonEntryReason: "",
            ExceptionStage: "");
        if (string.IsNullOrWhiteSpace(comp) || string.IsNullOrWhiteSpace(repo))
            return new SceneAwareSnapshotPairResolution(true, false, "None", comp, repo, null, null, false, false, null, "history/repository not found", ["comparison-history or snapshot-repository missing"], ["paths"], debug with { ExceptionStage = "PathDiscovery" });

        try
        {
            var entries = ParseComparisonEntries(comp, ref debug, out var parseWarnings);
            var chosen = entries
                .Where(x => !string.IsNullOrWhiteSpace(x.OldHash) && !string.IsNullOrWhiteSpace(x.NewHash))
                .OrderByDescending(x => x.RowCount > 0)
                .ThenByDescending(x => x.ComparedAt ?? DateTimeOffset.MinValue)
                .FirstOrDefault();
            var selectedIndex = entries.FindIndex(x => x == chosen);
            debug = debug with
            {
                SelectedComparisonEntryIndex = selectedIndex >= 0 ? selectedIndex : null,
                SelectedComparisonEntryReason = selectedIndex >= 0 ? "latest valid old/new hash entry" : "no valid old/new hash entry"
            };

            var hashes = ParseSnapshotRepositoryHashes(repo, ref debug, out var parseRepoWarnings);

            var oldFound = !string.IsNullOrWhiteSpace(chosen.OldHash) && hashes.Contains(chosen.OldHash);
            var newFound = !string.IsNullOrWhiteSpace(chosen.NewHash) && hashes.Contains(chosen.NewHash);
            var resolved = oldFound && newFound;
            var confidence = resolved ? "High" : (oldFound || newFound) ? "Medium" : "Low";
            var reason = resolved ? "comparison-history hashes resolved in snapshot-repository" : "snapshot hash pair not fully resolved";
            var missing = new List<string>();
            if (!oldFound) missing.Add("oldSnapshotHash");
            if (!newFound) missing.Add("newSnapshotHash");
            var warnings = new List<string>();
            warnings.AddRange(parseWarnings);
            warnings.AddRange(parseRepoWarnings);
            return new SceneAwareSnapshotPairResolution(true, resolved, confidence, comp, repo, chosen.OldHash, chosen.NewHash, oldFound, newFound, chosen.ComparedAt, reason, warnings, missing, debug);
        }
        catch (Exception ex)
        {
            var warning = $"exception={ex.GetType().Name}: {ex.Message}";
            return new SceneAwareSnapshotPairResolution(true, false, "None", comp, repo, null, null, false, false, null, "resolution failed", [warning], ["parse"], debug with { ExceptionStage = string.IsNullOrWhiteSpace(debug.ExceptionStage) ? "Unknown" : debug.ExceptionStage });
        }
    }

    private static List<(string? OldHash, string? NewHash, DateTimeOffset? ComparedAt, int RowCount)> ParseComparisonEntries(string path, ref SceneAwareSnapshotPairResolverDebug debug, out List<string> warnings)
    {
        warnings = [];
        var entries = new List<(string? OldHash, string? NewHash, DateTimeOffset? ComparedAt, int RowCount)>();
        debug = debug with { ExceptionStage = "ReadComparisonHistory" };
        using var ch = JsonDocument.Parse(File.ReadAllText(path));
        debug = debug with { ComparisonHistoryRootKind = ch.RootElement.ValueKind.ToString(), ExceptionStage = "ParseComparisonEntries" };
        foreach (var e in EnumerateCandidateEntries(ch.RootElement, ["value", "entries", "history", "items", "results"]))
        {
            var oldHash = ReadStringByKeys(e, ["OldSnapshotHash", "oldSnapshotHash", "OldSnapshotId", "oldSnapshotId", "OldSnapshot.SnapshotHash", "OldSnapshot.Hash", "oldSnapshot.hash"]);
            var newHash = ReadStringByKeys(e, ["NewSnapshotHash", "newSnapshotHash", "NewSnapshotId", "newSnapshotId", "NewSnapshot.SnapshotHash", "NewSnapshot.Hash", "newSnapshot.hash"]);
            DateTimeOffset? comparedAt = null;
            var t = ReadStringByKeys(e, ["ComparedAt", "comparedAt", "CreatedAt", "createdAt"]);
            if (!string.IsNullOrWhiteSpace(t) && DateTimeOffset.TryParse(t, out var dt)) comparedAt = dt;
            var rowCount = 0;
            int.TryParse(ReadStringByKeys(e, ["Metadata.rowCount", "rowCount"]), out rowCount);
            entries.Add((oldHash, newHash, comparedAt, rowCount));
        }
        debug = debug with { ComparisonEntryCount = entries.Count };
        return entries;
    }

    private static HashSet<string> ParseSnapshotRepositoryHashes(string path, ref SceneAwareSnapshotPairResolverDebug debug, out List<string> warnings)
    {
        warnings = [];
        var hashes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        debug = debug with { ExceptionStage = "ReadSnapshotRepository" };
        using var sr = JsonDocument.Parse(File.ReadAllText(path));
        debug = debug with { SnapshotRepositoryRootKind = sr.RootElement.ValueKind.ToString(), ExceptionStage = "ParseSnapshotEntries" };
        var count = 0;
        foreach (var e in EnumerateCandidateEntries(sr.RootElement, ["value", "snapshots", "items", "repository", "entries"]))
        {
            count++;
            var h = ReadStringByKeys(e, ["Snapshot.Metadata.SnapshotHash", "SnapshotHash", "snapshotHash", "Hash", "Snapshot.Hash"]);
            if (!string.IsNullOrWhiteSpace(h)) hashes.Add(h.Trim());
        }
        debug = debug with { SnapshotEntryCount = count };
        return hashes;
    }

    private static IEnumerable<JsonElement> EnumerateCandidateEntries(JsonElement root, string[] collectionKeys)
    {
        if (root.ValueKind == JsonValueKind.Array)
        {
            foreach (var e in root.EnumerateArray()) yield return e;
            yield break;
        }
        if (root.ValueKind == JsonValueKind.Object)
        {
            foreach (var key in collectionKeys)
            {
                if (root.TryGetProperty(key, out var child))
                {
                    if (child.ValueKind == JsonValueKind.Array)
                    {
                        foreach (var e in child.EnumerateArray()) yield return e;
                        yield break;
                    }
                    if (child.ValueKind == JsonValueKind.Object)
                    {
                        yield return child;
                        yield break;
                    }
                }
            }
            yield return root;
        }
    }

    private static string? ReadStringByKeys(JsonElement element, string[] keys)
    {
        foreach (var key in keys)
        {
            var value = TryReadPath(element, key);
            if (!string.IsNullOrWhiteSpace(value)) return value.Trim();
        }
        return null;
    }

    private static string? TryReadPath(JsonElement element, string dottedPath)
    {
        var current = element;
        foreach (var part in dottedPath.Split('.'))
        {
            if (current.ValueKind != JsonValueKind.Object || !current.TryGetProperty(part, out current))
                return null;
        }
        return current.ValueKind switch
        {
            JsonValueKind.String => current.GetString(),
            JsonValueKind.Number => current.ToString(),
            JsonValueKind.True => "true",
            JsonValueKind.False => "false",
            _ => null
        };
    }
}
