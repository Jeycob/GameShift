# GameShift

A small Windows tray switch for sharing one PC between games and local AI.

**Status:** first local build, tested with mock HTTP and plugin tests. Not yet deployed or game-tested on Sklenik. The ZIP includes a private .NET runtime; no administrator or system-wide runtime install is required.

## Start

1. Extract the entire ZIP to a permanent folder such as `C:\Tools\GameShift`.
2. Run `GameShift.exe`. Do not move the EXE away from its DLLs and `runtime` folder.
3. Configure the native Strata URL. Use its local server root, not `/v1` and not the desktop app's web/proxy port. Default: `http://127.0.0.1:8080`; an existing installation may use another port.
4. If authentication is enabled, enter the **name** of the environment variable containing the key. Default: `STRATA_API_KEY`. Never paste the secret into this field. Restart GameShift after changing a user environment variable.
5. Install the Harness plugin below while Harness is idle. Until its acknowledgement arrives, GameShift will NOT unload the model.
6. Add the real game `.exe` files, not launcher shortcuts. Save settings.
7. Choose Automatic. In Gaming, unloading model allocations from RAM and VRAM is mandatory; no opt-in checkbox is needed.

## Modes

| Mode | What happens |
|---|---|
| AI | After a gaming hold, loads Strata and verifies readiness before releasing original waiting Harness requests. Restores CPU priorities changed by GameShift. |
| Background | Lowers CPU priority only for the exact AI executables you selected. It does **not** release VRAM or limit GPU usage. |
| Gaming | Holds new Harness model calls, lets an existing response finish, then always asks Strata to unload model allocations from RAM and VRAM. |
| Automatic | Gaming while any configured game process exists. After all exit and cooldown expires, reloads Strata, verifies readiness, then lets Harness continue. Alt-tab does not resume AI. |

GameShift never force-kills or suspends processes, changes cooling, or edits GPU power/clock settings. A current long response can delay unloading. Cancel it in Harness yourself if you need the GPU immediately.

Game detection runs every five seconds. The UI shows release as pending until a fresh matching idle Harness acknowledgement arrives, native `/unload` succeeds and `/health` confirms `service=strata` and `loaded=false`. Unloading and verification are repeated during Gaming, so an old successful response cannot mask a reload by another client. An old `AllowModelUnload=false` setting is ignored on upgrade.

Strata's native unload stops its model engine and vision encoder, releasing their RAM and VRAM allocations. Windows may keep model files in its **reclaimable standby/file cache**; this is not active model memory. GameShift does not purge system caches, reserve memory, unload other models or claim that total Task Manager memory usage becomes zero. API confirmation is not independent GPU/process telemetry.

Only one standalone Harness process and its configured providers are gated. OpenCode, LM Studio, another Harness process and Strata's own chat can still use/reload the model. This is **not** a global GPU scheduler. The first version does not implement LM Studio unloading.

Waiting happens before the next model request, not by freezing tools or Windows processes. A tool already running can continue. Chat history and project files are not deleted. Resuming can require a model reload and prefill; this is not preservation of an in-flight GPU context.

## Automatic return after a game

Keep GameShift, native Strata's HTTP server and Harness running. Select Automatic and configure actual game executables.

1. All configured games must have exited. The default cooldown is 30 seconds.
2. GameShift restores the selected AI processes' former CPU priorities and checks Strata.
3. If needed, it calls native `POST /load`. The Harness hold remains active while loading.
4. Only verified `/health` with `service=strata` and `loaded=true` permits release of the hold.
5. Already waiting Harness operations continue in their original sessions with their original context. No replacement prompt, session or duplicated tool call is created by GameShift.

Loading happens asynchronously; the game detector and UI remain responsive. Errors keep Harness held and are retried with a backoff. If a game restarts during loading, GameShift invalidates the pending resume and requests unloading again; a late load response cannot release the hold. Native loading may still finish before Strata accepts unloading, so RAM/VRAM release is not instantaneous in this race.

Valid saved Automatic mode is restored after a GameShift restart. An existing hold is kept through a fresh cooldown/game check and model-readiness verification; it does not become a permanent manual Gaming mode. A malformed configuration still fails closed.

This resumes **live, waiting** operations. It does not resurrect a manually cancelled, timed-out, failed or closed Harness run, restart an exited Harness/server, or replay finished tools. If Windows/Harness was restarted, reopen the original saved session in Harness and continue it there. Other Harness timeouts or approval prompts may still require attention.

## Install the DeepSeek Harness plugin

Back up `cordis.patch.yml`. Wait for all agent runs to become idle before changing plugins; profile reloads may interrupt runs.

Keep your existing turn-queue plugin. Add this separate entry using the existing Cordis patch `insert` structure (merge it into that structure; do not replace your profile):

```yaml
- insert:
    - id: gameshift-gaming-gate-v1
      name: file:///C:/Tools/GameShift/integrations/deepseek-harness/gaming-gate.mjs
      config:
        providers: [strata1]
```

Replace the path and provider ID with your actual values. Verify that the plugin is active in Harness's plugin inventory. This code targets the same `agent/pre-step` and global `llm/stream` interfaces as the existing local turn queue; compatibility with your installed Harness release still needs an on-PC acceptance test.

Both applications must run as the same Windows user. Their default bridge directory is `%LOCALAPPDATA%\GameShift`. An optional plugin `directory` setting must point to that same absolute directory.

The bridge contains:

- `settings.json`: GameShift settings, no API secret.
- `control.json`: persistent pause/resume request.
- `harness-ack.json`: plugin acknowledgement, stream count and timestamp.
- `harness.lock`: one-writer guard, preventing a second Harness from overwriting a busy client's acknowledgement.

A malformed file fails closed. After a Harness crash, stop all Harness processes before removing a stale `harness.lock`, then restart Harness. Do not remove a lock belonging to a live process. To release a hold, open GameShift and choose AI. Exiting or crashing GameShift does not silently resume AI during a game.

## First real acceptance test

1. Leave other AI clients idle. Save a small test project first.
2. Start a short Harness response. Select Gaming while it is producing output.
3. Confirm the current response finishes and no next model request starts.
4. Wait for native unload and health confirmation. Independently inspect GPU memory using `nvidia-smi` and the model engine's RAM/process presence in Task Manager; the app does not independently measure these allocations.
5. Select AI. Confirm model readiness is checked before the same chat continues and its files are unchanged.
6. Try Automatic with one configured game. Exit it; check that loading and resume happen only after the cooldown. Restart the game during loading and confirm Harness remains held.
7. Restart GameShift in a valid saved Automatic mode during the gaming hold. Exit the game and confirm the normal cooldown/readiness path resumes the original still-open Harness run.

If the native API returns 409, GameShift waits rather than killing work. If it reports an error or there is no fresh plugin acknowledgement, the UI does not claim the model is unloaded. Older Strata versions without the documented `/unload` contract need updating or a matching connector.

## Window and startup

Closing the window hides it in the notification area. Double-click its tray icon to reopen it. Exit is in the tray menu. An active gaming hold remains after Exit; CPU priority changes are restored where permissions permit.

Start-at-sign-in is opt-in and uses only the current user's `GameShift` Run entry. No elevation is requested. Keep the extracted folder in place. Disable this option before deleting GameShift.

## Build and test

Requires .NET 10 SDK and a current Node.js for tests. No third-party NuGet packages or npm install are required.

```powershell
dotnet restore src/GameShift.App --configfile NuGet.Config
dotnet build src/GameShift.App -c Release --no-restore
dotnet run --project tests/GameShift.Tests -c Release
node --test integrations/deepseek-harness/gaming-gate.test.mjs
```

The distributed EXE is framework-dependent with an adjacent private Windows x64 .NET 10 runtime. Source builds can use an installed .NET 10 Windows Desktop runtime. The bundled Microsoft license notices must stay with redistributions.

## Limitations

No live Sklenik test or automatic remote installation has been performed. No game-speed, GPU-priority, instantaneous pause, LM Studio integration, or automatic recovery of arbitrary failed tools is guaranteed. Process priority changes are best-effort and apply only to exact executable paths selected by the user.
