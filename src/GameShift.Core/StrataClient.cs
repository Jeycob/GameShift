using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace GameShift.Core;

public sealed record BackendResult(bool Unloaded, bool Busy, string Message);
public sealed record LoadResult(bool Ready, string Message);

public sealed class StrataClient(HttpClient client)
{
    public async Task<LoadResult> EnsureLoadedAsync(Settings settings, CancellationToken cancellationToken)
    {
        if (await ReadModelLoadedAsync(settings, cancellationToken) == true)
            return new(true, "Strata is already ready.");
        var root = Settings.ValidateEndpoint(settings.StrataUrl);
        using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(root, "load"));
        request.Content = new StringContent("{}", Encoding.UTF8, "application/json");
        AddAuth(request, settings);
        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        if (!response.IsSuccessStatusCode)
            return new(false, $"Model load returned HTTP {(int)response.StatusCode}. Harness remains held; retrying.");
        return await ReadModelLoadedAsync(settings, cancellationToken) == true
            ? new(true, "Strata confirms loaded=true. Waiting Harness requests can continue.")
            : new(false, "Model load was acknowledged but readiness is not confirmed. Harness remains held; retrying.");
    }

    public async Task<BackendResult> EnsureUnloadedAsync(Settings settings, CancellationToken cancellationToken)
    {
        // Always call unload, even if the engine is already absent: Strata also
        // releases an orphaned vision encoder here. Health alone is insufficient.
        var result = await UnloadAsync(settings, cancellationToken);
        if (!result.Unloaded) return result;
        return await ReadModelLoadedAsync(settings, cancellationToken) == false
            ? Released()
            : new(false, false, "Unload was acknowledged, but /health did not confirm loaded=false. RAM/VRAM release is NOT confirmed; retrying.");
    }

    private static BackendResult Released() => new(true, false,
        "Strata reports loaded=false: model allocations are released from RAM and VRAM. Windows may retain a reclaimable file cache; it is not cleared.");

    private async Task<bool?> ReadModelLoadedAsync(Settings settings, CancellationToken cancellationToken)
    {
        var root = Settings.ValidateEndpoint(settings.StrataUrl);
        using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(root, "health"));
        AddAuth(request, settings);
        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        if (!response.IsSuccessStatusCode) return null;
        var body = await ReadBoundedAsync(response.Content, cancellationToken);
        if (body is null) return null;
        try
        {
            using var json = JsonDocument.Parse(body);
            var rootValue = json.RootElement;
            if (rootValue.ValueKind != JsonValueKind.Object ||
                !rootValue.TryGetProperty("service", out var service) || service.ValueKind != JsonValueKind.String || service.GetString() != "strata" ||
                !rootValue.TryGetProperty("loaded", out var loaded)) return null;
            return loaded.ValueKind switch { JsonValueKind.True => true, JsonValueKind.False => false, _ => null };
        }
        catch (JsonException) { return null; }
    }

    public async Task<BackendResult> UnloadAsync(Settings settings, CancellationToken cancellationToken)
    {
        var root = Settings.ValidateEndpoint(settings.StrataUrl);
        using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(root, "unload"));
        request.Content = new StringContent("{}", Encoding.UTF8, "application/json");
        AddAuth(request, settings);
        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        if (response.StatusCode == HttpStatusCode.Conflict) return new(false, true, "Current request is running. Waiting; nothing was killed.");
        if (!response.IsSuccessStatusCode) return new(false, false, $"Unload refused: HTTP {(int)response.StatusCode}. Check the native API URL and authentication.");
        var body = await ReadBoundedAsync(response.Content, cancellationToken);
        if (body is null) return new(false, false, "Unexpected large unload response; state not confirmed.");
        try
        {
            using var json = JsonDocument.Parse(body);
            var status = json.RootElement.ValueKind == JsonValueKind.Object &&
                json.RootElement.TryGetProperty("status", out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
            return status switch
            {
                "unloaded" or "not loaded" or "already_unloaded" => new(true, false, "Model unloaded (native API confirmed). RAM/VRAM can be reused by the game."),
                "busy" => new(false, true, "Backend is busy. Waiting; nothing was killed."),
                _ => new(false, false, "API did not confirm unloading. Check the native server version/URL.")
            };
        }
        catch (JsonException) { return new(false, false, "Invalid unload response; state not confirmed."); }
    }
    private static async Task<byte[]?> ReadBoundedAsync(HttpContent content, CancellationToken cancellationToken)
    {
        await using var stream = await content.ReadAsStreamAsync(cancellationToken);
        var buffer = new byte[8193];
        var length = 0;
        while (length < buffer.Length)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(length), cancellationToken);
            if (read == 0) break;
            length += read;
        }
        return length > 8192 ? null : buffer[..length];
    }
    public async Task<string> ProbeAsync(Settings settings, CancellationToken cancellationToken)
    {
        var root = Settings.ValidateEndpoint(settings.StrataUrl);
        using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(root, "health"));
        AddAuth(request, settings);
        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        if (!response.IsSuccessStatusCode) return $"Health check: HTTP {(int)response.StatusCode}.";
        var authentication = await ProbeAuthenticationAsync(settings, cancellationToken);
        return authentication == 200 ? "Native health and authenticated model-list endpoints respond. No model is loaded, unloaded or paused by this check."
            : $"Health responds, but authenticated model-list check returned HTTP {authentication}. Check the current-user API key variable.";
    }
    public async Task<int> ProbeAuthenticationAsync(Settings settings, CancellationToken cancellationToken)
    {
        var root = Settings.ValidateEndpoint(settings.StrataUrl);
        using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(root, "v1/models"));
        AddAuth(request, settings);
        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        return (int)response.StatusCode;
    }
    private static void AddAuth(HttpRequestMessage request, Settings settings)
    {
        var name = settings.ApiKeyEnvironmentVariable;
        if (string.IsNullOrEmpty(name)) return;
        string? key = null;
        if (OperatingSystem.IsWindows())
        {
            // Explorer and Task Scheduler may retain an older environment block.
            // Read the current user's persisted value on EVERY request so a key
            // rotation does not need a permanent PowerShell wrapper or sign-out.
            try { key = Environment.GetEnvironmentVariable(name, EnvironmentVariableTarget.User); }
            catch (Exception error) when (error is System.Security.SecurityException or UnauthorizedAccessException)
            { throw new InvalidOperationException("The current-user API key variable could not be read. Check current-user environment permissions."); }
        }
        if (string.IsNullOrWhiteSpace(key)) key = Environment.GetEnvironmentVariable(name, EnvironmentVariableTarget.Process);
        if (string.IsNullOrWhiteSpace(key)) return;
        key = key.Trim();
        if (key.Any(char.IsControl)) throw new InvalidOperationException("The API key variable contains invalid control characters. No request was sent.");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
    }
}
