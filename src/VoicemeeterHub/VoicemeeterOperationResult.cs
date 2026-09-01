namespace VoicemeeterHub;

public sealed record VoicemeeterOperationResult(bool Success, long StatusCode, string? ParamName, string? ErrorSummary)
{
    public static VoicemeeterOperationResult Ok(string? paramName = null)
    {
        return new VoicemeeterOperationResult(true, 0, paramName, null);
    }

    public static VoicemeeterOperationResult Fail(long statusCode, string? paramName, string? errorSummary)
    {
        return new VoicemeeterOperationResult(false, statusCode, paramName, errorSummary ?? DescribeStatusCode(statusCode));
    }

    public static string DescribeStatusCode(long statusCode)
    {
        return statusCode switch
        {
            0 => "OK",
            -1 => "Voicemeeter error",
            -2 => "Voicemeeter not running or not logged in",
            -3 => "Unknown Voicemeeter parameter",
            -5 => "Voicemeeter structure mismatch",
            _ => $"Voicemeeter error (code {statusCode})"
        };
    }

    public static bool IndicatesDisconnected(long statusCode) => statusCode == -2;
}
