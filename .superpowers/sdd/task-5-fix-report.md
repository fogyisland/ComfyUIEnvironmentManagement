# M5.2 Task 5 — Review Fix Report

**Date:** 2026-07-14
**File modified:** `src-wpf/ComfyUI.Manager/Infrastructure/ProcessLauncher.cs`
**Test added:** `tests-wpf/ComfyUI.Manager.Tests/Infrastructure/ProcessLauncherTests.cs`

Note: `.superpowers/sdd/task-5-review.md` did not exist on disk; worked from the
findings supplied in the task brief.

---

## Fix 1 — 🔴 Critical: StopEnvAsync grace timeout wired to wrong token

**Location:** `ProcessLauncher.cs:237` (was line 236)

**Before:**
```csharp
using var shutdownCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
shutdownCts.CancelAfter(TimeSpan.FromSeconds(timeoutSeconds));
await process.WaitForExitAsync(ct);          // <-- caller token, timeout never fires
```

**After:**
```csharp
using var shutdownCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
shutdownCts.CancelAfter(TimeSpan.FromSeconds(timeoutSeconds));
await process.WaitForExitAsync(shutdownCts.Token);   // honors the 3s grace deadline
```

**Why:** `shutdownCts` was configured with `CancelAfter` but never observed —
`WaitForExitAsync` awaited the caller's `ct` instead. A ComfyUI process that
ignores `CloseMainWindow()` (all console processes do) would leave the Stop
button hung for the child's entire lifetime. Passing `shutdownCts.Token` makes
the existing `catch (OperationCanceledException)` fire on timeout and fall
through to `TryKillProcessTree`.

---

## Fix 2 — 🟠 Important: Exited handler / StopEnvAsync double-DB-write race

**Location:** `ProcessLauncher.cs:395-403` (Exited handler top)

**Before:**
```csharp
process.Exited += (_, _) =>
{
    lock (_runningLock)
    {
        _running.Remove(envId);
    }
    // ... process_state.Delete + env-row Upsert
```

**After:**
```csharp
process.Exited += (_, _) =>
{
    lock (_runningLock)
    {
        // StopEnvAsync removes _running before awaiting exit; if it's already
        // gone, Stop is taking over cleanup — bail to avoid DB double-write /
        // clobbering a concurrent restart.
        if (!_running.ContainsKey(envId)) return;
        _running.Remove(envId);
    }
    // ... process_state.Delete + env-row Upsert
```

**Why:** `StopEnvAsync` removes the entry from `_running` (under lock) *before*
awaiting exit. When the process later fires `Exited` on the threadpool, the
guard now short-circuits, so only one code path writes `process_state.Delete`
+ `environments` row. Eliminates the redundant write and the risk of the
Exited handler stamping `status=stopped/pid=null` over a fresh Start the user
clicked immediately after Stop. Single-line guard, existing lock reused.

---

## Fix 3 — 🟠 Important: WaitForPortAsync used blocking connect

**Location:** `ProcessLauncher.cs:434-476` (`WaitForPortAsync` rewritten)

**Before:** loop called `IsPortInUse(host, port)` which does
`client.ConnectAsync(...).Wait(500ms)` — a synchronous 500 ms block per
iteration even when the port is already listening.

**After:** inlined an async connect using
`CancellationTokenSource.CreateLinkedTokenSource(ct)` + `CancelAfter(timeout)`
and `await client.ConnectAsync(host, port, deadlineCts.Token)`. Returns the
instant the connect succeeds; retries on refusal with `Task.Delay(500, token)`;
throws `TimeoutException` when the deadline `CancelAfter` trips, and rethrows a
genuine caller-cancel as `OperationCanceledException`.

**Signature kept** as `(string host, int port, TimeSpan timeout, CancellationToken ct)`
so the single call site (`StartEnvAsync:167`) is unchanged.

**`IsPortInUse` kept:** it is `public` and still used by the pre-flight
port-conflict guard at `StartEnvAsync:110`. No test or other reference relies
on it beyond that, so it was not removed.

---

## Fix 4 — 🟠 Important: ResolvePythonExecutable was Windows-only

**Location:** `ProcessLauncher.cs:294-310` + new `using` at top

**Before:**
```csharp
var exe = Path.Combine(env.VenvPath, "Scripts", "python.exe");  // Windows only
```

**After:**
```csharp
var relative = RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
    ? Path.Combine("Scripts", "python.exe")
    : Path.Combine("bin", "python");
var exe = Path.Combine(env.VenvPath, relative);
```

Added `using System.Runtime.InteropServices;`.

**Why:** aligns with `VenvVerifier` (Scripts/python.exe on Windows,
bin/python on Linux/macOS). Prevents silent failure if the app is run under
WSL/macOS in dev. Behavior on Windows (the shipping target) is unchanged.

---

## Regression test

**Added:** `tests-wpf/ComfyUI.Manager.Tests/Infrastructure/ProcessLauncherTests.cs`
— `StopEnvAsync_TimesOutAndKills_WhenProcessIgnoresClose`.

- Writes a temp `main.py` that binds the assigned `--port` then `sleep(60)`.
  A console process ignores `CloseMainWindow()`, so the only exit path is the
  grace-timeout -> kill logic — exactly the Critical bug's code path.
- Resolves a real interpreter via `sys.executable` (absolute path, since
  `ResolvePythonExecutable` does `File.Exists`), seeds an env row + real repos
  over a `TestDb` SQLite file, launches through `ProcessLauncher.StartEnvAsync`
  (waits for the real port to listen), then calls
  `StopEnvAsync(timeoutSeconds: 2)`.
- Asserts the call returns in `< 20s` (pre-fix it would block ~60s) and that
  `IsRunning` is false afterward.
- Gracefully returns (skips) if no Python is on PATH — build-only verification
  still covers the fix in that case.

Local run: Python 3.10.6 present, test executed the real kill path and passed
(full suite finished in ~7s, confirming no 60s hang).

---

## Verification

**Build:** `dotnet build src-wpf/ComfyUI.Manager/ComfyUI.Manager.csproj -v quiet`
→ **0 警告 0 错误**.

**Tests:** `dotnet test tests-wpf/ComfyUI.Manager.Tests/ --nologo`
→ **通过: 15, 失败: 0, 跳过: 0** (was 14/14; +1 new regression test).

---

## Commit

See `fix(wpf): T5 review fixes — StopEnvAsync timeout, Exited/Stop race, cross-platform Python resolution`.
