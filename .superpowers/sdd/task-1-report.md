# Task T1 Report — Remove PySide6 from 8 bridges (M4 Phase 1)

**Status:** DONE
**Commit:** `026fa08` — refactor(bridge): remove PySide6 from 8 bridges, Signal -> bus.emit ws.push

## What was implemented

All 7 bridge files rewritten to remove PySide6 dependency, plus 1 file deleted:

| File | Change |
| --- | --- |
| `app/bridge/base.py` | BaseBridge now plain class (no QObject). Accepts `bus: EventBus`, exposes `_invoke(fn, *args)` that emits `errorOccurred` via `bus.emit("ws.push", "errorOccurred", code, message)`. |
| `app/bridge/node_bridge.py` | Largest bridge. All 9 Signal fields removed, 20+ Slot methods renamed snake_case (enableInEnv -> enable_in_env, requestScan -> request_scan, listVersions -> list_versions, upgradeNode -> upgrade_node, scanDeps -> scan_deps, etc.). busy Property replaced with @property. |
| `app/bridge/environment_bridge.py` | 3 Signal fields removed, 5 methods renamed (createEnv -> create_env, deleteEnv -> delete_env, cloneEnv -> clone_env, listEnvs -> list_envs, getEnv -> get_env). Forwards 5 bus events to ws.push. |
| `app/bridge/catalog_bridge.py` | 3 Signal fields removed, 3 methods renamed (addNode -> add_node, removeNode -> remove_node, listNodes -> list_nodes). |
| `app/bridge/process_bridge.py` | Channel renames applied: processStarted -> envStarted, processStopped -> envStopped, processLogLine -> logLine. logVersion/logLines Property replaced with @property. |
| `app/bridge/settings_bridge.py` | themeModeChanged Signal deleted (merged into settingsChanged via key param). setValue -> set_value, migrateDbPath -> migrate_db_path, etc. Added catalog_auto_refresh/catalog_auto_refresh_minutes/compat_api_base_url fields per plan. |
| `app/bridge/torch_bridge.py` | detectCuda -> detect_cuda, initEnvTorch -> init_env_torch. suggestedCuVersions Property -> @property. |
| `app/bridge/scanned_service_factory.py` | DELETED — was a PySide6 QObject wrapper around per-env ScannedNodeService factory. No longer needed without QML. |

## Channel name mapping (applied per brief)

- `processStarted` -> `envStarted`
- `processStopped` -> `envStopped`
- `processLogLine` -> `logLine`
- `themeModeChanged` -> merged into `settingsChanged`
- All others kept their original Signal names (nodeListChanged, conflictListChanged, envCreated, envDeleted, envCloned, envStatusChanged, catalogUpdated, catalogUnavailable, busyChanged, settingsChanged, errorOccurred, logLine, envStarted, envStopped, cudaDetected, torchConfigWritten, etc.)

## What was tested and results

### Import smoke test (PASS)
```
$ PYTHONPATH=src python -c "
from app.bridge.base import BaseBridge
from app.bridge.node_bridge import NodeBridge
from app.bridge.environment_bridge import EnvironmentBridge
from app.bridge.catalog_bridge import CatalogBridge
from app.bridge.process_bridge import ProcessBridge
from app.bridge.settings_bridge import SettingsBridge
from app.bridge.torch_bridge import TorchBridge
print('OK all 7 bridges import cleanly')
"
OK all 7 bridges import cleanly
```

`app.app_context` and `app.main` also still import (they reference the bridges).

### Bridge test suite (RED — expected, T2 will fix)
```
$ PYTHONPATH=src python -m pytest tests/bridge/ --no-header -q
... 45 failed, 14 errors in 10.49s
```

All failures are expected: tests still call camelCase methods (e.g. `enableInEnv`) and listen to old Signal attributes (`bridge.nodeEnabled`). T2 will rewrite them to subscribe to `bus.emit("ws.push", ...)` and call snake_case methods.

## Files changed

8 files: 7 modified + 1 deleted
- `app/bridge/base.py` (canonical rewrite, plain class)
- `app/bridge/node_bridge.py` (canonical rewrite, 300+ lines)
- `app/bridge/environment_bridge.py`
- `app/bridge/catalog_bridge.py`
- `app/bridge/process_bridge.py`
- `app/bridge/settings_bridge.py`
- `app/bridge/torch_bridge.py`
- `app/bridge/scanned_service_factory.py` (deleted)

Diff size: +260 / -441 lines (net shrink due to decorator/import removal).

## Self-review findings

1. **Completeness:** All Signal/Slot/Property decorators removed; all camelCase methods renamed to snake_case; scanned_service_factory.py deleted. Verified by `Grep PySide6|Signal\(|Slot\(|Property\(` over `app/bridge/` — only matches left are docstrings.

2. **Quality:** Used real service signatures (`EnvironmentService.create(python_path=..., comfyui_source=..., port=...)`, `CatalogService.add_node/list_nodes/remove_node`) rather than the brief's hypothetical `create`/`add`/`list_all` aliases. This was necessary to match the existing service layer — the brief's signatures didn't match the codebase. Documented here.

3. **Channel renames:** Applied per spec §6.2 — `processStarted -> envStarted`, `processStopped -> envStopped`, `processLogLine -> logLine`, `themeModeChanged` merged into `settingsChanged`.

4. **No PySide6 in app/bridge/:** Confirmed by grep. The dependency is only present where other parts of the app still use it (app_context, main, qml).

5. **Tests baseline:** 45 fail + 14 err = 59/59 bridge tests broken at this commit. T2 will rewrite them.

## Issues / concerns

- The brief's hypothetical service signatures (`EnvironmentService.create(name, layout, python, comfyui_source, port)`) don't match the real service (uses kwargs `python_path=`, `comfyui_source=`, `port=`). I matched the real service — bridge works against existing code. If the brief's signature was intended as a future change, that's out of scope for T1.
- Bridge tests still expect old camelCase API. T2 must rewrite them. This is per the plan.

## Next step

T2: rewrite 6 bridge test files to remove QApplication fixture and use bus.emit listeners.

---

# T1 Review Fixes — Trailing Newlines + Property Notify Contract

**Status:** DONE
**Commit:** `ecffab1` — fix(bridge): restore trailing newlines + document property-notify contract

## Finding 1: Trailing newlines

All 7 bridge files were missing the plan-mandated trailing `\n` (verified via `tail -c 5 | xxd` showing last bytes without `0a`). Fixed by appending a single `\n` to each:

- `app/bridge/base.py` — last 3 bytes now `}}\n`
- `app/bridge/catalog_bridge.py` — last 3 bytes now `e}\n`
- `app/bridge/environment_bridge.py` — last 3 bytes now `e}\n`
- `app/bridge/node_bridge.py` — last 3 bytes now `}}\n`
- `app/bridge/process_bridge.py` — last 3 bytes now `er\n`
- `app/bridge/settings_bridge.py` — last 3 bytes now `}}\n`
- `app/bridge/torch_bridge.py` — last 3 bytes now `"]\n`

Verification output (`python -c "import pathlib; ..."`):
```
app\bridge\base.py b'}}\n'
app\bridge\catalog_bridge.py b'e}\n'
app\bridge\environment_bridge.py b'e}\n'
app\bridge\node_bridge.py b'}}\n'
app\bridge\process_bridge.py b'er\n'
app\bridge\settings_bridge.py b'}}\n'
app\bridge\torch_bridge.py b'"]\n'
app\bridge\__init__.py b'""\n'
```

## Finding 2: Property notify contract docstring

Three properties lost Qt `Property` auto-notify semantics when converted to Python `@property`. Per Option A (recommended), added a one-line docstring to each property documenting the re-read-on-event contract:

- `process_bridge.py` `@property log_version` — "Re-read after ws.push(\"logLine\", ...) — emitted by `_on_line`."
- `process_bridge.py` `@property log_lines` — "Re-read after ws.push(\"logLine\", ...) — emitted by `_on_line`."
- `node_bridge.py` `@property busy` — "Re-read after ws.push(\"busyChanged\") — emitted by `_set_busy`."

Each underlying state mutation already emits the corresponding `ws.push` channel:
- `_on_line` → `bus.emit("ws.push", "logLine", env_id, line)` (covers log_version + log_lines)
- `_set_busy` → `bus.emit("ws.push", "busyChanged")` (covers busy)

WPF consumers re-query the property when the corresponding WS event arrives.

## Test verification

Import smoke (PASS):
```
$ PYTHONPATH=src python -c "..."
OK: all 7 bridges import
```

Bridge test suite (RED — unchanged from T1 baseline, expected 59/59 breakages, T2 will fix):
```
$ PYTHONPATH=src pytest tests/bridge/ --tb=no -q
... 45 failed, 14 errors in 10.88s
```
Same as T1 baseline. No new breakages introduced.

## Files changed in this fix

7 files (all already modified in T1, just patched):
- `app/bridge/base.py` — trailing `\n`
- `app/bridge/catalog_bridge.py` — trailing `\n`
- `app/bridge/environment_bridge.py` — trailing `\n`
- `app/bridge/node_bridge.py` — trailing `\n` + `busy` docstring
- `app/bridge/process_bridge.py` — trailing `\n` + `log_version`/`log_lines` docstrings
- `app/bridge/settings_bridge.py` — trailing `\n`
- `app/bridge/torch_bridge.py` — trailing `\n`

Diff: +10 / -7 (3 docstrings added, 7 newlines added, 0 lines removed).

---

# M5 T1 Report — BulkUpdateService + unit tests

**Status**: DONE

## Commit Hashes

- `d2d3dd4` — `feat(bulk-update): BulkUpdateService — cross-env × node git pull batch`

## Files Changed

- **Created**: `src/comfy_mgr/services/bulk_update_service.py` (207 lines)
- **Created**: `tests/services/test_bulk_update_service.py` (105 lines)
- **Modified**: `app/app_context.py`
  - Added import: `from comfy_mgr.services.bulk_update_service import BulkUpdateService`
  - Injected `self.bulk_update_service = BulkUpdateService(node_bridge=self.node_bridge, bus=self.bus)` after `self.node_bridge` creation (M5 section, between M2 and M3 markers)

## Test Results

### Targeted: `pytest tests/services/test_bulk_update_service.py -v`

```
tests/services/test_bulk_update_service.py::test_start_returns_bulk_id PASSED
tests/services/test_bulk_update_service.py::test_start_validates_empty_env_ids PASSED
tests/services/test_bulk_update_service.py::test_start_validates_empty_node_ids PASSED
tests/services/test_bulk_update_service.py::test_get_status_returns_pending_immediately PASSED
tests/services/test_bulk_update_service.py::test_cancel_unknown_id_returns_bulk_not_found PASSED
tests/services/test_bulk_update_service.py::test_get_status_unknown_returns_bulk_not_found PASSED

======================== 6 passed, 2 warnings in 0.32s ========================
```

**6/6 PASS** as expected by Step 3.

There are 2 deprecation warnings from `asyncio.get_event_loop()` at line 81 in `bulk_update_service.py` (Python 3.10+ recommends `asyncio.get_running_loop()`). The warnings are non-fatal and the brief code uses `get_event_loop()` verbatim — not changed.

### Full Python Suite: `pytest tests/ --ignore=tests/wpf`

| Metric | Baseline (without M5 changes) | After M5 changes | Delta |
|---|---|---|---|
| Passed | 389 | 395 | +6 |
| Failed | 17 | 17 | 0 |
| Errors | 68 | 68 | 0 |
| Skipped | 2 | 2 | 0 |

The +6 delta corresponds exactly to the 6 new BulkUpdateService tests. All 17 failures + 68 errors are **pre-existing M4 carry-over failures** (e.g., `CompatHTTPClient` undefined in `app_context.py:182`, CLI tests with `mocker` fixture missing, etc.) — confirmed by re-running baseline via `git stash -u` and observing identical failure counts.

**No new failures introduced.**

## Implementation Notes

### `BulkUpdateService` API (matches brief)

- `__init__(self, node_bridge: NodeBridge, bus: EventBus)` — accepts bridge + bus
- `start(env_ids, node_ids) -> Result[str]` — validates empty lists, generates UUID4 bulk_id, schedules `_run_bulk` as asyncio task if loop is running; emits `ws.push("bulk_update.started", {...})` to bus
- `cancel(bulk_id) -> Result[str]` — returns checkpoint `env_id#node_id` of current row, or `BULK_NOT_FOUND` / `BULK_NOT_RUNNING`
- `get_status(bulk_id) -> Result[dict]` — returns status summary; `BULK_NOT_FOUND` on unknown
- `get_all_running_ids() -> list[str]` — returns all bulk_ids with status pending/running

### `_run_bulk` semantics

- Iterates rows (env × node cartesian product)
- Calls `self._bridge.upgrade_node(env_id, package=row.node_id, target=None)` per row (per brief's hard interface requirement)
- Maps error codes:
  - `GIT_DIRTY` / `GIT_HAS_LOCAL_CHANGES` / `GIT_LOCKED` → row status `skipped`
  - Other errors → row status `failed`
- Emits `ws.push("bulk_update.progress", {...})` per row completion
- Final emission: `ws.push("bulk_update.completed", summary)` or `ws.push("bulk_update.cancelled", summary)`

### Wiring in `app/app_context.py`

Injected `self.bulk_update_service = BulkUpdateService(self.node_bridge, self.bus)` in a new "M5 新增" section right after `self.node_bridge` creation and before the M3 section. This places it after `self.bus` (line 70) and after `self.node_bridge` (line 128) per the brief's "需保证 self.node_bridge 已先实例化" requirement.

## Concerns / Deviations

**None.** The brief was followed verbatim:
- Test file copied exactly from Step 1
- Service implementation copied exactly from Step 2
- Wiring location matches Step 4 spec (after `self.node_bridge` creation, before M3 section)
- Commit message matches Step 5 spec exactly

Only an incidental deprecation warning from `asyncio.get_event_loop()` (Python 3.10+) which is present in the brief's verbatim code. Not changed because the brief specifies the exact code.

---

# M5 T1 Fix Pass — 5 fixes applied per dispatch

**Status**: DONE
**Commit**: `d8d70fc` — `fix(bulk-update): 5 fixes per dispatch — bridge dict, VERSION_LOCKED skip, checkpoint, newlines, get_running_loop`

## What was wrong (and why each fix)

### Fix 1 — Bridge dict contract

`NodeBridge.upgrade_node` (see `app/bridge/node_bridge.py:177-185`) returns a plain dict envelope — `{"ok": True, "value": ...}` or `{"ok": False, "error": {"code", "message"}}` — not a `Result` object. The original M5 T1 dispatch treated `res` as a Result, so `res.ok` / `res.error.code` would fail at runtime (`AttributeError: 'dict' object has no attribute 'ok'`) for every row once a real bulk run executed.

**Fix**: switched the success/error branches to use `res.get("ok")` and `err["code"]` / `err["message"]`.

### Fix 2 — VERSION_LOCKED → skipped

The dispatch only mapped `GIT_DIRTY` / `GIT_HAS_LOCAL_CHANGES` / `GIT_LOCKED` to row-status `skipped`. `VERSION_LOCKED` (emitted by `VersionService.upgrade` when the version is locked — see M3 T10 / spec §10.1) would fall through to `failed`, which is wrong UX: a locked node is not a fault, it is a deliberate user choice. The bulk dialog would show it as red and the user would think something broke.

**Fix**: added `"VERSION_LOCKED"` to the skip tuple; only true errors (network, git fatal, unknown) reach the `failed` branch.

### Fix 3 — Trailing newlines

Both `src/comfy_mgr/services/bulk_update_service.py` and `tests/services/test_bulk_update_service.py` were committed without a trailing newline (last bytes from `git show HEAD` showed `}` with no `0a`). The plan's §"全局约束" mandates a trailing newline on every source file.

**Fix**: appended a single LF to each (also normalized CRLF→LF since `core.autocrlf=true` makes git ensure CRLF on commit anyway).

### Fix 4 — `cancelled_at_checkpoint` field

`cancel()` returned the checkpoint string but did not store it on the record. Later `get_status()` calls after cancel could not recover *where* the bulk was halted (the runner resets `rec.current = None` after the loop). Spec §3.2 requires `get_status()` to surface the checkpoint so the UI can show "cancelled at env-2#node-foo, 3 rows remaining".

**Fix**: added `cancelled_at_checkpoint: Optional[str]` to `_BulkRecord`; `cancel()` sets `rec.cancelled_at_checkpoint = checkpoint` before returning; `get_status()` exposes it in the response dict.

### Fix 5 — `get_running_loop()`

`asyncio.get_event_loop()` is deprecated since Python 3.10 and emits `DeprecationWarning: There is no current event loop` in 3.12+. The brief code (and the M5 T1 dispatch) used the deprecated form. Pytest's `pytest-asyncio` mode=strict config surfaces this as a warning per call.

**Fix**: `asyncio.get_running_loop()` is the correct API for "I am in a coroutine / a sync function inside a running loop"; if no loop is running we still get the `RuntimeError` branch and the run gets deferred (matches dispatch behavior).

## Test delta

| Test | Was | Now |
| --- | --- | --- |
| `test_start_returns_bulk_id` | PASS | PASS |
| `test_start_validates_empty_env_ids` | PASS | PASS |
| `test_start_validates_empty_node_ids` | PASS | PASS |
| `test_get_status_returns_pending_immediately` | PASS | PASS |
| `test_cancel_unknown_id_returns_bulk_not_found` | PASS | PASS |
| `test_get_status_unknown_returns_bulk_not_found` | PASS | PASS |
| `test_cancel_records_checkpoint` *(new)* | — | PASS |
| `test_run_bulk_marks_version_locked_as_skipped` *(new)* | — | PASS |
| **Total** | **6 / 6** | **8 / 8 PASS** |

```
$ python -m pytest tests/services/test_bulk_update_service.py -v
...
collected 8 items
tests/services/test_bulk_update_service.py::test_start_returns_bulk_id PASSED
tests/services/test_bulk_update_service.py::test_start_validates_empty_env_ids PASSED
tests/services/test_bulk_update_service.py::test_start_validates_empty_node_ids PASSED
tests/services/test_bulk_update_service.py::test_get_status_returns_pending_immediately PASSED
tests/services/test_bulk_update_service.py::test_cancel_unknown_id_returns_bulk_not_found PASSED
tests/services/test_bulk_update_service.py::test_get_status_unknown_returns_bulk_not_found PASSED
tests/services/test_bulk_update_service.py::test_cancel_records_checkpoint PASSED
tests/services/test_bulk_update_service.py::test_run_bulk_marks_version_locked_as_skipped PASSED
============================== 8 passed in 0.31s ==============================
```

Targeted suite is green. No deprecation warnings on stderr (previously there were 2 from `get_event_loop()`).

## Files changed

- `src/comfy_mgr/services/bulk_update_service.py` — `_BulkRecord` adds `cancelled_at_checkpoint`; `start()` uses `get_running_loop`; `_run_bulk()` reads dict envelope + adds `VERSION_LOCKED` to skip set; `cancel()` writes checkpoint; `get_status()` exposes it. Trailing newline added.
- `tests/services/test_bulk_update_service.py` — fixture rewritten to return dict envelope (matches real bridge contract); 2 new tests for Fix 2 + Fix 4. Trailing newline added.

Diff stat: `2 files changed, 59 insertions(+), 14 deletions(-)`.

## Concerns / Deviations

None for this fix pass — every change matches the dispatch list verbatim. Original M5 T1 report's "Concerns: None" stands; the deprecation warning noted there is now eliminated by Fix 5.

---

# M5 T1 Fix Pass — Re-review Verdict

## Re-Review Verdict
- OK All 5 fixes present and correct

## Per-Fix Check
1. Bridge dict contract: OK — `bulk_update_service.py:112` reads `if res.get("ok"):` and `:123-125` reads `err = res.get("error") or {}; err_code = err.get("code", "UNKNOWN"); err_msg = err.get("message", "")`. Dict envelope contract matches `NodeBridge.upgrade_node` real signature.
2. VERSION_LOCKED reachable: OK — `bulk_update_service.py:127-128` skip tuple now includes `"VERSION_LOCKED"`. New test `test_run_bulk_marks_version_locked_as_skipped` drives `_run_bulk` synchronously and asserts `rec.skipped == 1`.
3. Trailing newlines: OK — `tail -c 1` returns `0a` (LF) for both `src/comfy_mgr/services/bulk_update_service.py` and `tests/services/test_bulk_update_service.py`. Diff hunk explicitly removed `\ No newline at end of file` marker.
4. `cancelled_at_checkpoint` field: OK — `_BulkRecord` dataclass line `cancelled_at_checkpoint: Optional[str] = None` added; `cancel()` writes `rec.cancelled_at_checkpoint = checkpoint` (line 171); `get_status()` exposes it in the response dict (line 184); new test `test_cancel_records_checkpoint` asserts `s.value["cancelled_at_checkpoint"] is not None`.
5. `get_running_loop`: OK — `bulk_update_service.py:82` uses `loop = asyncio.get_running_loop()`. Deprecated `asyncio.get_event_loop()` form is gone.

## Issues
None. Each of the 5 dispatch items is present in the diff, matches the actual source, and has at least one targeted test (for Fixes 2 and 4) or behavioral evidence (Fixes 1, 3, 5). No new defects introduced.

## Tests
8/8 PASS in `pytest tests/services/test_bulk_update_service.py -v` — verified by implementer's run output captured in the fix report. Two new tests (`test_cancel_records_checkpoint`, `test_run_bulk_marks_version_locked_as_skipped`) cover Fix 4 and Fix 2 respectively.

## Assessment
**Task quality:** Approved
**Reasoning:** All 5 dispatched fixes are present, correctly implemented, and covered by tests; no regression or new defect introduced.
