using System.Net;
using GameShift.Core;

internal static class AuthTests
{
    public static async Task RunAsync(Action<bool, string> check)
    {
        // A unique name leaves the real STRATA_API_KEY and persisted User/Machine
        // environment untouched. All writes below target this test process only.
        var name = "GAMESHIFT_AUTH_FIXTURE_" + Guid.NewGuid().ToString("N");
        var previous = Environment.GetEnvironmentVariable(name, EnvironmentVariableTarget.Process);
        try
        {
            var handler = new AuthCaptureHandler();
            using var http = new HttpClient(handler);
            var backend = new StrataClient(http);
            var configuration = new Settings { ApiKeyEnvironmentVariable = name };
            configuration.Validate();
            Environment.SetEnvironmentVariable(name, "  fixture-token-one  ", EnvironmentVariableTarget.Process);
            await backend.ProbeAsync(configuration, CancellationToken.None);
            check(handler.Scheme == "Bearer" && handler.Parameter == "fixture-token-one", "Process-only key fallback is trimmed and used");
            Environment.SetEnvironmentVariable(name, "fixture-token-two", EnvironmentVariableTarget.Process);
            await backend.ProbeAsync(configuration, CancellationToken.None);
            check(handler.Parameter == "fixture-token-two", "Credentials are re-read per request rather than cached in the client");
            Environment.SetEnvironmentVariable(name, null, EnvironmentVariableTarget.Process);
            await backend.ProbeAsync(configuration, CancellationToken.None);
            check(handler.Scheme is null && handler.Parameter is null, "Absent key does not reuse a prior Authorization header");
            Environment.SetEnvironmentVariable(name, "fixture\r\ninvalid", EnvironmentVariableTarget.Process);
            var calls = handler.Calls;
            var rejected = false;
            try { await backend.ProbeAsync(configuration, CancellationToken.None); }
            catch (InvalidOperationException error) { rejected = !error.Message.Contains("fixture", StringComparison.Ordinal); }
            check(rejected && handler.Calls == calls, "Invalid credential is rejected before HTTP dispatch without exposing its value");
            await backend.ProbeAsync(configuration with { ApiKeyEnvironmentVariable = "" }, CancellationToken.None);
            check(handler.Scheme is null, "Empty variable name disables key lookup");
        }
        finally { Environment.SetEnvironmentVariable(name, previous, EnvironmentVariableTarget.Process); }
    }

    private sealed class AuthCaptureHandler : HttpMessageHandler
    {
        public string? Scheme { get; private set; }
        public string? Parameter { get; private set; }
        public int Calls { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls++;
            Scheme = request.Headers.Authorization?.Scheme;
            Parameter = request.Headers.Authorization?.Parameter;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{\"service\":\"strata\",\"loaded\":true}") });
        }
    }
}
