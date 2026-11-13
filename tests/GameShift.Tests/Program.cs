using System.Net;
using GameShift.Core;

var count = 0;
void Check(bool condition, string label) { count++; if (!condition) throw new Exception(label); }
void Reject(Action action, string label)
{
    try { action(); } catch (ArgumentException) { count++; return; }
    throw new Exception("Expected rejection: " + label);
}
try
{
    var now = DateTimeOffset.UtcNow;
    var policy = new SwitchPolicy();
    Check(policy.Evaluate(SwitchMode.Automatic, false, now, 30) == WorkState.Ai, "No game");
    Check(policy.Evaluate(SwitchMode.Automatic, true, now, 30) == WorkState.Gaming, "Game");
    Check(policy.Evaluate(SwitchMode.Automatic, false, now.AddSeconds(29), 30) == WorkState.Gaming, "Cooldown");
    Check(policy.Evaluate(SwitchMode.Automatic, false, now.AddSeconds(30), 30) == WorkState.Ai, "Cooldown boundary");
    Check(policy.Evaluate(SwitchMode.Gaming, false, now, 30) == WorkState.Gaming, "Manual gaming");
    Check(policy.Evaluate(SwitchMode.Background, true, now, 30) == WorkState.Background, "Manual background");
    Check(policy.Evaluate(SwitchMode.Ai, true, now, 30) == WorkState.Ai, "Explicit resume");
    policy.RestartCooldown(now);
    Check(policy.Evaluate(SwitchMode.Automatic, false, now.AddSeconds(29), 30) == WorkState.Gaming, "Restored automatic hold preserves cooldown");
    Check(policy.Evaluate(SwitchMode.Automatic, false, now.AddSeconds(30), 30) == WorkState.Ai, "Restored automatic mode resumes after cooldown");
    policy.Evaluate(SwitchMode.Automatic, true, now.AddSeconds(31), 30);
    Check(policy.Evaluate(SwitchMode.Automatic, false, now.AddSeconds(59), 30) == WorkState.Gaming, "Game restart resets cooldown");
    foreach (var url in new[] { "http://127.0.0.1:8080", "http://localhost:18500", "http://[::1]:8000" })
        Check(Settings.ValidateEndpoint(url).IsLoopback, "Loopback");
    foreach (var url in new[] { "https://example.com", "http://127.0.0.1.evil/", "http://127.0.0.1/v1", "http://user@localhost", "http://localhost/?secret=x", "http://192.168.0.48:8080" })
        Reject(() => Settings.ValidateEndpoint(url), url);
    new Settings().Validate(); count++;
    var migrated = System.Text.Json.JsonSerializer.Deserialize<Settings>("{\"AllowModelUnload\":false}", Settings.Json)!;
    migrated.Validate();
    Check(!System.Text.Json.JsonSerializer.Serialize(migrated, Settings.Json).Contains("AllowModelUnload"), "Legacy opt-out cannot disable mandatory gaming unload");
    Reject(() => (new Settings { GamePaths = ["python.exe"] }).Validate(), "Relative path");
    Reject(() => (new Settings { ResumeDelaySeconds = 0 }).Validate(), "Delay");
    Reject(() => (new Settings { Mode = SwitchMode.Automatic, AutoDiscoverGames = false }).Validate(), "Automatic mode needs game paths or discovery");
    new Settings { Mode = SwitchMode.Automatic }.Validate(); count++;
    Reject(() => (new Settings { ApiKeyEnvironmentVariable = "key-value" }).Validate(), "Env name");
    var command = new GateCommand(1, "id", true, "Gaming", now);
    var ack = new GateAck(1, "id", true, 0, now);
    Check(ControlFiles.CanUnload(command, ack, now), "Idle matching ACK");
    Check(!ControlFiles.CanUnload(command, null, now), "No ACK");
    Check(!ControlFiles.CanUnload(command, ack with { ActiveStreams = 1 }, now), "Busy ACK");
    Check(!ControlFiles.CanUnload(command, ack with { CommandId = "old" }, now), "Old command");
    Check(!ControlFiles.CanUnload(command, ack with { ObservedAt = now.AddSeconds(-9) }, now), "Stale ACK");
    Check(!ControlFiles.CanUnload(command, ack with { ObservedAt = now.AddSeconds(3) }, now), "Future ACK");
    Check(!ControlFiles.CanUnload(command with { Blocked = false }, ack, now), "Not blocked");
    Check(!ControlFiles.CanUnload(command with { Version = 2 }, ack, now), "Version");
    Check(!ControlFiles.CanUnload(command with { CommandId = "" }, ack with { CommandId = "" }, now), "Empty ID");
    var dir = Path.Combine(Path.GetTempPath(), "GameShift-test-" + Guid.NewGuid().ToString("N"));
    try
    {
        var file = Path.Combine(dir, "command.json");
        Check(ControlFiles.Read<GateCommand>(file) is null, "Missing file");
        ControlFiles.Write(file, command);
        Check(ControlFiles.Read<GateCommand>(file) == command, "Atomic round trip");
        Check(Directory.GetFiles(dir).Length == 1, "No temp remnants");
    }
    finally { if (Directory.Exists(dir)) Directory.Delete(dir, true); }
    foreach (var (status, body, unloaded, busy) in new[] {
        (HttpStatusCode.OK, "{\"status\":\"unloaded\"}", true, false),
        (HttpStatusCode.OK, "{\"status\":\"already_unloaded\"}", true, false),
        (HttpStatusCode.OK, "{\"status\":\"not loaded\"}", true, false),
        (HttpStatusCode.Conflict, "", false, true),
        (HttpStatusCode.OK, "{\"status\":\"busy\"}", false, true),
        (HttpStatusCode.Forbidden, "secret", false, false),
        (HttpStatusCode.OK, "{\"status\":1}", false, false),
        (HttpStatusCode.OK, "[]", false, false),
        (HttpStatusCode.OK, "broken", false, false),
        (HttpStatusCode.OK, new string('x', 9000), false, false) })
    {
        using var http = new HttpClient(new FakeHandler(status, body));
        var result = await new StrataClient(http).UnloadAsync(new Settings(), CancellationToken.None);
        Check(result.Unloaded == unloaded && result.Busy == busy, "Response " + body[..Math.Min(body.Length, 40)]);
        Check(!result.Message.Contains("secret"), "No raw response leak");
    }
    foreach (var (unloadStatus, healthBody, healthCode, expected) in new[] {
        ("unloaded", "{\"service\":\"strata\",\"loaded\":false}", HttpStatusCode.OK, true),
        ("not loaded", "{\"service\":\"strata\",\"loaded\":false}", HttpStatusCode.OK, true),
        ("unloaded", "{\"service\":\"strata\",\"loaded\":true}", HttpStatusCode.OK, false),
        ("unloaded", "{\"loaded\":false}", HttpStatusCode.OK, false),
        ("unloaded", "{\"service\":\"other\",\"loaded\":false}", HttpStatusCode.OK, false),
        ("unloaded", "{\"service\":\"strata\",\"loaded\":\"false\"}", HttpStatusCode.OK, false),
        ("unloaded", "broken", HttpStatusCode.OK, false),
        ("unloaded", "[]", HttpStatusCode.OK, false),
        ("unloaded", "{\"service\":\"strata\",\"loaded\":false}", HttpStatusCode.Forbidden, false),
        ("unloaded", new string('x', 9000), HttpStatusCode.OK, false) })
    {
        var handler = new SequenceHandler(unloadStatus, healthBody, healthCode);
        using var http = new HttpClient(handler);
        var backend = new StrataClient(http);
        var result = await backend.EnsureUnloadedAsync(new Settings(), CancellationToken.None);
        Check(result.Unloaded == expected, "Health verification: " + healthBody[..Math.Min(healthBody.Length, 60)]);
        Check(handler.Calls.SequenceEqual(new[] { "POST /unload", "GET /health" }), "Unload before verification, even if already unloaded");
        await backend.EnsureUnloadedAsync(new Settings(), CancellationToken.None);
        Check(handler.Calls.Count == 4, "No stale cached success: repeated check unloads/verifies again");
    }
    using (var http = new HttpClient(new FakeHandler(HttpStatusCode.Conflict, "")))
    {
        var result = await new StrataClient(http).EnsureUnloadedAsync(new Settings(), CancellationToken.None);
        Check(result.Busy && !result.Unloaded, "Busy inference cannot be reported as released");
    }
    foreach (var (initiallyLoaded, loadCode, loadWorks, expectedReady) in new[] {
        (true, HttpStatusCode.OK, true, true),
        (false, HttpStatusCode.OK, true, true),
        (false, HttpStatusCode.OK, false, false),
        (false, HttpStatusCode.Forbidden, false, false),
        (false, HttpStatusCode.Conflict, false, false) })
    {
        var handler = new LifecycleHandler(initiallyLoaded, loadCode, loadWorks);
        using var http = new HttpClient(handler);
        var result = await new StrataClient(http).EnsureLoadedAsync(new Settings(), CancellationToken.None);
        Check(result.Ready == expectedReady, "Ready only when confirmed: " + loadCode);
        Check(handler.LoadCalls == (initiallyLoaded ? 0 : 1), "Already loaded does not restart engine");
        Check(handler.HealthCalls == (initiallyLoaded || loadCode != HttpStatusCode.OK ? 1 : 2), "Health check before and after successful load");
    }
    {
        var pendingLoad = new TaskCompletionSource<LoadResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;
        using var coordinator = new ModelResumeCoordinator((_, _) => { calls++; return pendingLoad.Task; });
        Check(!coordinator.Poll(new Settings(), now, CancellationToken.None).Ready, "Resume keeps hold while starting");
        Check(!coordinator.Poll(new Settings(), now.AddSeconds(5), CancellationToken.None).Ready && calls == 1, "Only one model load in flight");
        pendingLoad.SetResult(new(true, "ready"));
        Check(coordinator.Poll(new Settings(), now.AddSeconds(10), CancellationToken.None).Ready, "Ready permits release of original waiting requests");
    }
    {
        var pendingLoad = new TaskCompletionSource<LoadResult>();
        var calls = 0;
        using var coordinator = new ModelResumeCoordinator((_, _) => { calls++; return pendingLoad.Task; });
        coordinator.Poll(new Settings(), now, CancellationToken.None);
        coordinator.Hold(); // Game restarted before model load completed.
        Check(!coordinator.Poll(new Settings(), now.AddSeconds(1), CancellationToken.None).Ready && calls == 1, "Cancelled old load not duplicated");
        pendingLoad.SetResult(new(true, "old success"));
        Check(!coordinator.Poll(new Settings(), now.AddSeconds(2), CancellationToken.None).Ready, "Game restart invalidates a late successful load");
    }
    {
        var pendingLoad = new TaskCompletionSource<LoadResult>();
        var calls = 0;
        using var coordinator = new ModelResumeCoordinator((_, _) => { calls++; return pendingLoad.Task; });
        coordinator.Poll(new Settings(), now, CancellationToken.None);
        var changed = new Settings { StrataUrl = "http://localhost:18500" };
        Check(!coordinator.Poll(changed, now.AddSeconds(1), CancellationToken.None).Ready, "Endpoint change blocks old readiness");
        pendingLoad.SetResult(new(true, "wrong endpoint"));
        Check(!coordinator.Poll(changed, now.AddSeconds(2), CancellationToken.None).Ready && calls == 2, "New endpoint must load independently");
    }
    {
        var calls = 0;
        using var coordinator = new ModelResumeCoordinator((_, _) => { calls++; return Task.FromResult(new LoadResult(false, "offline")); });
        coordinator.Poll(new Settings(), now, CancellationToken.None);
        Check(!coordinator.Poll(new Settings(), now.AddSeconds(1), CancellationToken.None).Ready, "Offline keeps hold");
        coordinator.Poll(new Settings(), now.AddSeconds(5), CancellationToken.None);
        Check(calls == 1, "Offline retries backed off");
        coordinator.Poll(new Settings(), now.AddSeconds(12), CancellationToken.None);
        Check(calls == 2, "Offline recovery retries automatically");
    }
    {
        var handler = new LifecycleHandler(true, HttpStatusCode.OK, true);
        using var http = new HttpClient(handler);
        var backend = new StrataClient(http);
        for (var i = 0; i < 3; i++)
        {
            Check((await backend.EnsureUnloadedAsync(new Settings(), CancellationToken.None)).Unloaded && !handler.Loaded, "Game unload releases model");
            Check((await backend.EnsureLoadedAsync(new Settings(), CancellationToken.None)).Ready && handler.Loaded, "Game exit restores same backend");
        }
    }
    await AuthTests.RunAsync(Check);
    GameLibraryTests.Run(Check);
    Console.WriteLine($"PASS: {count} assertions; mock HTTP and disposable game-library fixtures only, no hardware changes.");
    return 0;
}
catch (Exception error) { Console.Error.WriteLine(error); return 1; }

sealed class FakeHandler(HttpStatusCode code, string body) : HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        if (request.Method != HttpMethod.Post || request.RequestUri?.AbsolutePath != "/unload") throw new Exception("Wrong request");
        if (request.Content?.Headers.ContentType?.MediaType != "application/json") throw new Exception("Missing content type");
        return Task.FromResult(new HttpResponseMessage(code) { Content = new StringContent(body) });
    }
}

sealed class SequenceHandler(string unloadStatus, string healthBody, HttpStatusCode healthCode) : HttpMessageHandler
{
    public List<string> Calls { get; } = [];
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var call = request.Method + " " + request.RequestUri!.AbsolutePath;
        Calls.Add(call);
        return call switch {
            "POST /unload" => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{\"status\":\"" + unloadStatus + "\"}") }),
            "GET /health" => Task.FromResult(new HttpResponseMessage(healthCode) { Content = new StringContent(healthBody) }),
            _ => throw new Exception("Unexpected request " + call)
        };
    }
}

sealed class LifecycleHandler(bool initiallyLoaded, HttpStatusCode loadCode, bool loadWorks) : HttpMessageHandler
{
    public bool Loaded { get; private set; } = initiallyLoaded;
    public int LoadCalls { get; private set; }
    public int HealthCalls { get; private set; }
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var status = HttpStatusCode.OK;
        string body;
        switch (request.Method + " " + request.RequestUri!.AbsolutePath)
        {
            case "GET /health":
                HealthCalls++; body = "{\"service\":\"strata\",\"loaded\":" + (Loaded ? "true" : "false") + "}"; break;
            case "POST /load":
                LoadCalls++; status = loadCode; if (loadWorks && status == HttpStatusCode.OK) Loaded = true;
                body = "{\"status\":\"loaded\"}"; break;
            case "POST /unload":
                Loaded = false; body = "{\"status\":\"unloaded\"}"; break;
            default: throw new Exception("Unexpected native API request");
        }
        return Task.FromResult(new HttpResponseMessage(status) { Content = new StringContent(body) });
    }
}
