namespace YMMProjectManager.Infrastructure.Diff;

public static class DiffDisplayTextService
{
    public static string ToDisplayText(string? value, string nullText = "(なし)")
    {
        if (value is null)
        {
            return nullText;
        }

        try
        {
            var decoded = DecodeUnicodeEscapes(value);
            decoded = UnquoteJsonStringIfNeeded(decoded);
            return string.IsNullOrEmpty(decoded) ? string.Empty : decoded;
        }
        catch
        {
            return value;
        }
    }

    private static string DecodeUnicodeEscapes(string value)
    {
        return Regex.Replace(
            value,
            @"\\u([0-9a-fA-F]{4})",
            m => ((char)Convert.ToInt32(m.Groups[1].Value, 16)).ToString());
    }

    private static string UnquoteJsonStringIfNeeded(string value)
    {
        if (value.Length >= 2 && value[0] == '"' && value[^1] == '"')
        {
            try
            {
                return JsonSerializer.Deserialize<string>(value) ?? string.Empty;
            }
            catch
            {
                return value;
            }
        }

        return value;
    }
}
