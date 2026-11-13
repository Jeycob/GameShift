using System.Net;
using System.Text.Json;

namespace GameShift.Core;

public sealed record Settings
{
    public SwitchMode Mode { get; init; } = SwitchMode.Ai;
    public string StrataUrl { get; init; } = "http://127.0.0.1:8080";
    public string ApiKeyEnvironmentVariable { get; init; } = "STRATA_API_KEY";
    public bool AutoDiscoverGames { get; init; } = true;
    public int ResumeDelaySeconds { get; init; } = 30;
    public string[] GamePaths { get; init; } = [];
    public string[] AiProcessPaths { get; init; } = [];
    public void Validate()
    {
        ValidateEndpoint(StrataUrl);
        if (GamePaths is null || AiProcessPaths is null || ApiKeyEnvironmentVariable is null) throw new ArgumentException("Configuration fields must not be null.");
        if (!Enum.IsDefined(Mode)) throw new ArgumentException("Unknown mode.");
        if (Mode == SwitchMode.Automatic && !AutoDiscoverGames && GamePaths.Length == 0) throw new ArgumentException("Enable game-library discovery or choose at least one game executable for Automatic mode.");
        if (ResumeDelaySeconds is < 5 or > 600) throw new ArgumentException("Resume delay must be 5-600 seconds.");
        if (GamePaths.Length > 50 || AiProcessPaths.Length > 20) throw new ArgumentException("Too many executable paths.");
        foreach (var file in GamePaths.Concat(AiProcessPaths))
            if (!Path.IsPathFullyQualified(file) || !file.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) || file.Contains('*'))
                throw new ArgumentException("Choose a full executable (.exe) path, without wildcards.");
        if (ApiKeyEnvironmentVariable.Length > 100 || ApiKeyEnvironmentVariable.Any(c => !char.IsAsciiLetterOrDigit(c) && c != '_'))
            throw new ArgumentException("Use an environment variable NAME, not an API key.");
    }
    public static Uri ValidateEndpoint(string value)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) || uri.Scheme != "http" ||
            !(uri.Host.Equals("localhost", StringComparison.OrdinalIgnoreCase) || IPAddress.TryParse(uri.Host.Trim('[', ']'), out var ip) && IPAddress.IsLoopback(ip)) ||
            uri.AbsolutePath != "/" || uri.UserInfo.Length != 0 || uri.Query.Length != 0 || uri.Fragment.Length != 0)
            throw new ArgumentException("Use a local native Strata server root, for example http://127.0.0.1:8080 (no /v1, credentials or query).");
        return uri;
    }
    public static readonly JsonSerializerOptions Json = new() { WriteIndented = true, PropertyNameCaseInsensitive = true };
}
