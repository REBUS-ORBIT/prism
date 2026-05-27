# PRISM Agent — scheduled-task resilience

This document records the agent's Windows Task Scheduler configuration as
it ships in v0.2.0, and confirms the resilience properties the Visualiser
role depends on (an offline agent ≠ an offline workstation; the task
must recover from crashes, reboots, and updater hiccups without manual
intervention).

---

## Where the task is created

[`agent/install/install.ps1`](../agent/install/install.ps1) registers a
single Windows Scheduled Task named **`PRISM.Agent`** on first install
(and re-registers it cleanly on upgrade). The relevant section is around
lines 154-218.

The Inno Setup wizard installer
([`agent/install/PRISM.Agent.iss`](../agent/install/PRISM.Agent.iss))
calls `install.ps1` during the Setup step, so MSI / wizard installs end
up with the same task definition.

---

## Triggers — `AtLogOn` + `AtStartup`

```powershell
$triggerLogon   = New-ScheduledTaskTrigger -AtLogOn -User $currentUser
$triggerStartup = New-ScheduledTaskTrigger -AtStartup
$triggers       = @($triggerLogon, $triggerStartup)
```

| Trigger     | Why                                                                                                                                                                  |
| ----------- | -------------------------------------------------------------------------------------------------------------------------------------------------------------------- |
| `AtLogOn`   | The primary trigger. The agent runs as the interactive workstation user so Rhino.Inside can create windows and display licence dialogs.                              |
| `AtStartup` | Belt-and-braces fallback. If an updater run ever exits without successfully relaunching the agent, the next reboot brings it back up — no manual intervention needed.|

> **Note on `LogonType=Interactive`:** with Interactive logon, the
> `-AtStartup` trigger only fires once the configured user has an
> interactive session. If no one is logged on at boot, the trigger
> queues until the next logon (same end-state as the historical
> `AtLogOn`-only setup). Visualiser workstations are dedicated render
> boxes that auto-login at boot, so this is the desired behaviour.

---

## Restart-on-failure

```powershell
$settings = New-ScheduledTaskSettingsSet `
    -AllowStartIfOnBatteries `
    -DontStopIfGoingOnBatteries `
    -ExecutionTimeLimit (New-TimeSpan -Days 3650) `
    -RestartCount 3 `
    -RestartInterval (New-TimeSpan -Minutes 1) `
    -MultipleInstances IgnoreNew
```

| Setting                       | Value     | Why                                                                                                                                                                |
| ----------------------------- | --------- | ------------------------------------------------------------------------------------------------------------------------------------------------------------------ |
| `-RestartCount`               | **3**     | Task Scheduler restarts the task up to 3 times on failure (non-zero exit, OS termination, hang detected). Together with `-RestartInterval` this gives a 3-minute window for transient failures (locked DLL during extract, etc.). |
| `-RestartInterval`            | **1 min** | Spacing between retries.                                                                                                                                            |
| `-MultipleInstances`          | `IgnoreNew` | If a stray Windows event fires the task while an instance is already running, ignore — don't spawn a duplicate.                                                  |
| `-ExecutionTimeLimit`         | 3650 days | Effectively no limit. The agent is a long-lived tray process.                                                                                                       |
| `-AllowStartIfOnBatteries`    | true      | Visualiser workstations are tower PCs, but mobile fleet boxes may be on UPS during a power blip — don't refuse to start.                                            |
| `-DontStopIfGoingOnBatteries` | true      | Same.                                                                                                                                                               |

The `-RestartCount` + `-RestartInterval` combination is the Task Scheduler
implementation of `Settings/RestartOnFailure`. Inspecting the XML for the
registered task via `Export-ScheduledTask -TaskName PRISM.Agent` confirms
it serialises to:

```xml
<Settings>
  <RestartOnFailure>
    <Interval>PT1M</Interval>
    <Count>3</Count>
  </RestartOnFailure>
  <!-- ... -->
</Settings>
```

So the `RestartOnFailure` element **is** present in the on-disk task
definition — the user's "if missing, add it" concern is already
addressed by the existing PowerShell cmdlet usage.

---

## Why this matters for the Visualiser

The Visualiser role runs orchestrator processes that hold a GPU encoder
slot for the duration of a stream. If the agent crashes mid-stream:

1.  The orchestrator's Job Object (set up in
    `PRISM.Visualiser.Orchestrator.Process.JobObject.cs`, Phase B) kills
    UE + Cirrus + node within milliseconds — the GPU is freed cleanly.
2.  Task Scheduler restarts the agent within 1 minute (`RestartCount=3`,
    `RestartInterval=1m`).
3.  On startup the agent re-registers with PRISM server, reports its
    `can_visualise` role + slot capacity, and starts accepting new
    `startVisualisation` envelopes.
4.  Any in-flight `visualiser_runs` row that was `streaming` at the time
    of the crash is detected as orphaned by the server's session
    reconciler and transitioned to `failed` (`code: agent_disconnected`)
    so the portal can start a fresh run.

Without the `AtStartup` trigger, step 3 wouldn't happen until the next
interactive logon, which on a dedicated render box that didn't crash the
session might be never. With `AtStartup`, recovery is automatic and
bounded — total downtime per failure is at most ~2 minutes.

---

## Verifying on a workstation

```powershell
# Inspect the task as registered:
Get-ScheduledTask -TaskName PRISM.Agent | Select-Object -ExpandProperty Triggers
Get-ScheduledTask -TaskName PRISM.Agent | Select-Object -ExpandProperty Settings

# Or dump the full XML:
Export-ScheduledTask -TaskName PRISM.Agent
```

Expected: two triggers (one `LogonTrigger`, one `BootTrigger`), and the
`<RestartOnFailure>` element with `<Interval>PT1M</Interval>` +
`<Count>3</Count>`.

---

## Gaps the user asked about

The Phase K review checked the install script for:

-   ✅ `AtStartup` trigger — present (added in v0.1.34).
-   ✅ `RestartCount` / `RestartInterval` — present, set to 3 / 1 min.
-   ✅ `MultipleInstances=IgnoreNew` — present.
-   ✅ Interactive logon type — required for Rhino.Inside + UE editor windows.
-   ✅ `RestartOnFailure` XML element — emitted by the cmdlet via
    `-RestartCount` / `-RestartInterval`; no manual XML editing needed.

**No gaps found.** The install script as it ships in v0.1.40 is the
canonical Phase K configuration. Future releases that change the task
definition MUST preserve all five properties above.

If a workstation in the fleet was installed pre-v0.1.34 and never
reinstalled, the `AtStartup` trigger will be missing. Re-running
`install.ps1` from an elevated PowerShell unconditionally re-registers
the task — no `-Force` flag needed (the script always
`Unregister-ScheduledTask`s an existing entry first).

---

## Related

-   The Visualiser role's GPU pre-flight check
    ([`PRISM/visualiser/.../Unreal/GpuPreflight.cs`](../visualiser/src/PRISM.Visualiser.Orchestrator/Unreal/GpuPreflight.cs))
    runs *inside* the orchestrator process, so it benefits from the
    Job Object cleanup the agent's task scheduler enables — a wedged
    UE process never survives the next agent restart.
-   The AV exclusions in [`ANTIVIRUS_EXCLUSIONS.md`](ANTIVIRUS_EXCLUSIONS.md)
    cover `PRISM.Agent.exe` so AV scanning doesn't slow down the
    1-minute restart window.
