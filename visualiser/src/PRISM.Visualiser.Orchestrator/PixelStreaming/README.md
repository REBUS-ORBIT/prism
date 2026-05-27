# PixelStreaming — Phase F components

Phase F of the
[`PRISM Visualiser` plan](../../../../.cursor/plans/prism_visualiser_role_d36fa628.plan.md)
brings up a Pixel Streaming 2 session on localhost after Phase E's
Unreal-side import has completed.

This directory implements BUILD.md Phase 4 (`Pixel Streaming bring-up`).
End state per the plan: **a localhost browser sees the stream**.

## Components

| File                          | Purpose                                                                                                                                                              |
| ----------------------------- | -------------------------------------------------------------------------------------------------------------------------------------------------------------------- |
| `PortAllocator.cs`            | TCP / UDP port allocation via the `IPAddress.Loopback:0` bind-and-release trick. Includes a "distinct N ports in one shot" helper that binds all sockets in parallel to guarantee uniqueness. |
| `SignallingSupervisor.cs`     | Locates Cirrus under `<UE_ROOT>\Engine\Plugins\Media\PixelStreaming2\Resources\WebServers\SignallingWebServer\` and Node at `<UE_ROOT>\Engine\Binaries\ThirdParty\Node\Win64\node.exe`. Spawns Cirrus via `ProcessSupervisor`, parses the ready line + the "streamer connected" line via permissive regexes. Both can be overridden for smoke-testing via `PRISM_VISUALISER_CIRRUS_SCRIPT` / `PRISM_VISUALISER_NODE_EXE` env vars. |
| `PixelStreamingSession.cs`    | Composes `UnrealGameHandle` + `SignallingHandle`, exposes `RunUntilExitAsync` (block until UE exits or the orchestrator-side `CancellationToken` trips), and `ShutdownAsync` (kill UE first, then Cirrus, with a `5s` grace period before the JobObject KILL_ON_JOB_CLOSE backstop reclaims anything stuck). |

The matching `UnrealLauncher.LaunchGameMode(...)` (in
[`../Unreal/UnrealLauncher.cs`](../Unreal/UnrealLauncher.cs)) spawns
`UnrealEditor-Cmd.exe` with the PS2 flag set documented in BUILD.md
§ Phase 4:

```
UnrealEditor-Cmd.exe <project>.uproject /Game/REBUS/Maps/Imported_<runId> -game \
  -RenderOffScreen -ResX=1920 -ResY=1080 \
  -PixelStreamingURL=ws://127.0.0.1:<signallingPort> \
  -PixelStreamingID=orbit_<short> \
  -Unattended -NoSplash -NoPause -stdout -FullStdOutLogOutput -log
```

The deprecated `-PixelStreamingIP` / `-PixelStreamingPort` flags from
PixelStreaming 1.x must not be used — PS2 (UE 5.5+) refuses to bind
to them.

## Cleanup ordering

When the orchestrator-side `CancellationToken` trips (Ctrl+C or
parent-agent SIGTERM):

1. `ShutdownAsync` calls `Kill` on the UE-game process tree.
2. Waits up to 5 s for UE to exit cleanly.
3. Calls `Kill` on the Cirrus process tree.
4. Waits up to 5 s for Cirrus to exit.
5. Anything still alive at this point is reclaimed by the Win32 Job
   Object the orchestrator created in `Program.Main`
   (`JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE`).

UE-first ordering keeps the shutdown log clean: if Cirrus dies first
UE spews a wall of "WebRTC peer connection failed" lines before
finally exiting.

## Stdout contract

After Phase F's bring-up, the orchestrator emits a final
`prism-visualiser/ready/v1` JSON line on stdout with the real local
URLs (see `Models/ReadyEvent.cs`):

```json
{
  "schema": "prism-visualiser/ready/v1",
  "status": "ready",
  "runId": "...",
  "projectId": "...",
  "modelId": "...",
  "versionId": "...",
  "playerUrl": "http://127.0.0.1:<signallingPort>/",
  "signallingUrl": "ws://127.0.0.1:<signallingPort>/ws",
  "streamerId": "orbit_<short>",
  "ueProcessId": <pid>,
  "signallingProcessId": <pid>,
  "logsDir": "..."
}
```

The orchestrator then blocks until UE exits or external cancellation
is observed.

## Failure surfaces

| Code                       | Cause                                                                          | Exit code |
| -------------------------- | ------------------------------------------------------------------------------ | --------- |
| `signalling_not_found`     | Cirrus script doesn't exist under the UE root (PS2 plugin not installed).      | 1         |
| `node_not_found`           | UE's bundled `node.exe` doesn't exist (partial UE install).                    | 1         |
| `signalling_start_timeout` | Cirrus didn't log a ready line within `30s`.                                   | 7         |
| `ue_game_start_timeout`    | UE didn't register a streamer with Cirrus within `120s`.                       | 8         |
| `ue_game_crashed`          | UE exited non-zero before the streamer connected.                              | 8         |

All five emit a `prism-visualiser/failed/v1` line on stdout before
the orchestrator returns the matching exit code.

## End-to-end testing

Out of scope for this phase. Real "localhost browser sees stream"
verification requires:

1. A workstation with UE 5.7 installed.
2. Phase D's artist-populated `v1.0.0-ue5.7` template.
3. A GPU with a hardware NVENC encoder (PS2 cannot use software encoding).

Synthetic Cirrus smoke testing IS feasible locally — set
`PRISM_VISUALISER_CIRRUS_SCRIPT` + `PRISM_VISUALISER_NODE_EXE` to a
small Node script that logs `Listening on :8888` and stays alive,
then run `prism-visualiser stream … --json`.
