using System.Text.Json;

namespace YMMProjectManager.Application.TimelineCore;

public static class DiffTimelineManualUiValidationLogWriter
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public static string Write(string diagnosticsDirectory, DiffTimelineManualUiValidationLog log)
    {
        Directory.CreateDirectory(diagnosticsDirectory);
        var path = Path.Combine(diagnosticsDirectory, $"manual-ui-validation-{log.SessionId}.json");
        File.WriteAllText(path, JsonSerializer.Serialize(log, JsonOptions));
        return path;
    }

    public static string WriteSummary(string diagnosticsDirectory, DiffTimelineManualUiValidationSessionSummary summary)
    {
        Directory.CreateDirectory(diagnosticsDirectory);
        var path = Path.Combine(diagnosticsDirectory, $"manual-ui-validation-summary-{summary.SessionId}.json");
        File.WriteAllText(path, JsonSerializer.Serialize(summary, JsonOptions));
        return path;
    }
}
