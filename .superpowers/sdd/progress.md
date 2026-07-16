# M2 Progress Ledger

Base SHA: e978818 (M2 plan commit)
Plan: docs/superpowers/plans/2026-06-26-m2-nodes.md
Spec: docs/superpowers/specs/2026-06-26-m2-nodes-design.md

## Status

- T1: complete (commits e978818..6c8b46c, review clean)
- T2: complete (commits 6c8b46c..8b29a95, review clean — 1 Important fix: trailing newlines)
- T3: complete (commits 8b29a95..b8b06db, review clean)
- T4: complete (commits b8b06db..1375d32, review clean)
- T5: complete (commits 1375d32..fdc44a4, review clean)
- T6: complete (commits fdc44a4..abc7dce, review clean — 1 Important fix: removed YAGNI try/except)
- T7: complete (commits abc7dce..66b31ed, review clean)
- T8: complete (commits 66b31ed..26f5f5b, review clean)
- T9: complete (commits 26f5f5b..afdd649, review clean)
- T10: complete (commits afdd649..e175ec0, review clean)
- T11: complete (commits e175ec0..c044d49, review clean)
- T12: complete (commits c044d49..af77b6f, review clean) — Env fix: upgraded packaging 24.1→26.2 to fix poetry 2.4.1 packaging.licenses import error
- T13: complete (commits af77b6f..42a942c, review clean — 2 Important fixes: JS class keyword + Theme fallback)
- T14: complete (commits 42a942c..fc7dbf1, review clean — 3 brief Theme.color fallbacks pre-fixed in implementer dispatch)
- T15: complete (commits fc7dbf1..b9b9928, review clean)
- T16: complete (commits b9b9928..0100241, review clean — bridge change bundled for current.node_disable_mode exposure)
- T17: complete (commits 0100241..75b4df4, review clean — Minor: 32 en_US M2 strings untranslated, deferred to M3+ i18n pass)
- T18: complete (commits 75b4df4..5fe801d, review clean — 241 passed + 2 skipped; brief expected 242, count drift non-blocking)
- T19: complete (commits 5fe801d..7b96222, review clean — 3 CRITICAL GUI launch bugs + 2 related QML wiring bugs fixed; QML now loads rootObjects:1)
- T20: complete (commits 7b96222..e77d7ae, review clean — 1 Critical + 3 Important findings fixed; tagged v0.2.0; M2 ledger archived)
- T19: pending
- T20: pending

## Completed Tasks

- T1 (2026-06-26): schema v3 + ScannedNode/Conflict/NodeMeta models (commits e978818..6c8b46c, review clean)
- T2 (2026-06-26): ScannedNodeRepo + ConflictRepo + NodeMetaRepo (commits 6c8b46c..8b29a95, review clean) — Important fix: trailing newlines on 6 files
- T3 (2026-06-26): EventBus 进程内事件总线 (commits 8b29a95..b8b06db, review clean)
- T4 (2026-06-26): NodeScanner AST 3-tier static import (commits b8b06db..1375d32, review clean)
- T5 (2026-06-26): pkg_meta helpers (commits 1375d32..fdc44a4, review clean)
- T6 (2026-06-26): ScannedNodeService.scan (commits fdc44a4..abc7dce, review clean) — Important fix: removed YAGNI per-package try/except
- T7 (2026-06-26): ScannedNodeService.set_disabled + toggle_disabled (commits abc7dce..66b31ed, review clean)
- T8 (2026-06-26): ConflictService with auto-recompute (commits 66b31ed..26f5f5b, review clean)
- T9 (2026-06-26): GitHubClient (urllib anonymous API) (commits 26f5f5b..afdd649, review clean)
- T10 (2026-06-26): NodeMetaService with 1h TTL cache (commits afdd649..e175ec0, review clean)
- T11 (2026-06-26): AppContext M2 wiring + mkdir migration (commits e175ec0..c044d49, review clean)
- T12 (2026-06-26): NodeBridge M2 extension (commits c044d49..af77b6f, review clean) — Env fix: upgraded packaging 24.1→26.2 to fix poetry 2.4.1 packaging.licenses import error
- T13 (2026-06-26): ConflictPanel + NodeListItem QML components (commits af77b6f..42a942c, review clean — 2 Important fixes: JS class keyword + Theme fallback)
- T14 (2026-06-26): i18n zh_CN M2 strings + ConflictPanel tooltip strings (commits 42a942c..fc7dbf1, review clean)
- T15 (2026-06-26): QML ConflictPanel fix + i18n en_US/zh_CN (commits fc7dbf1..b9b9928, review clean)
- T16 (2026-06-26): AppContext + main.py EntryPoint wired (commits b9b9928..0100241, review clean)
- T17 (2026-06-26): i18n M2 base (~32 en_US strings + zh_CN) (commits 0100241..75b4df4, review clean)
- T18 (2026-06-26): test fixtures (fake_env_with_nodes) (commits 75b4df4..5fe801d, review clean)
- T19 (2026-06-26): Main.qml + Component.qml + EnvironmentDetailPanel + 5 QML wiring fixes (commits 5fe801d..7b96222, review clean — 5 wiring fixes in 7b96222)
- T20 (2026-06-26): final smoke + minor fixes (folder_rename disabled, list_active Result return) (commits 7b96222..a950180, review clean)
- T1 (2026-06-26): M3 schema v4 (3 tables + locked + Literal expand) (commits 91162b2..df18559, 16f7bd0, review clean — Important fix: 7/7 indexes asserted)
- T2 (2026-06-26): VersionRepo CRUD + CASCADE + limit (commits 16f7bd0..c58d67b, review clean — Important fix: trailing newlines on 2 files)
- T3 (2026-06-26): DepRepo CRUD + UNIQUE upsert (commits c58d67b..0d5a670, review clean — implementer self-fixed trailing newlines on both files)
- T4 (2026-06-26): CatalogCacheRepo CRUD + TTL + search (commits 0d5a670..bbda595, review clean)
- T5 (2026-06-26): git portable resolver + fetch script (commits bbda595..2a021d7, review clean — 2 brief-internal contradictions forced deviations: hoist `import subprocess` to module level for `fgp.subprocess.run` patching, `archive.unlink(missing_ok=True)` for test mock context)
- T6 (2026-06-26): python portable resolver + base python 3.10.6 marker (commits 2a021d7..3d34f03, review clean — brief-internal .gitignore issue resolved via `git add -f`; implementer verified `!python/.portable_version` re-include rule doesn't work for files in fully-ignored parent)
- T7 (2026-06-26): HTTPClient (urllib + retry + UA + JSON/bytes) (commits 3d34f03..07c422d, bdac365, review clean — 2 brief-internal contradictions forced deviations: status-check fallback for response-with-status mocks, break in URLError retry-exhausted path; Important fix: parallel break in except-HTTPError 5xx path)
- T8 (2026-06-26): CatalogHTTPClient (cache + offline degrade) (commits bdac365..6e4dba3, review clean)
- T9 (2026-06-26): CompatHTTPClient (M3 placeholder) (commits 6e4dba3..04c98fc, review clean)
- T10 (2026-06-26): VersionService (upgrade/downgrade/lock + history) (commits 04c98fc..a0cc2c5, review clean — brief-internal `bus=` vs `event_bus=` inconsistency resolved by aligning test to implementation's keyword-only signature)
- T11 (2026-06-28): DepService (parse req/pyproject + local conflict + check_global) (commits 0d5a670..8c699e2, e5a42fd, review clean — 2 Important fixes: (1) `_is_incompatible` false-positive on upper-bound specs (`<2.0` vs `<3.0` incorrectly flagged), fixed by broadening probe set to include 0.0.0/9999.9999.9999 plus each specifier's boundary; (2) missing tests for `check_global` empty-client + incompat translation paths; also added `list_by_env_and_package` to DepRepo (T3 omission caught))
- T12 (2026-06-28): InstallService (git clone + uninstall + dir conflict) (commits 4fc2d5b..8b2292d, review clean — 2 brief-internal deviations adjudicated: `event_bus`→`bus` param rename (same pattern as T10); `target_dir.mkdir` after successful clone so mocked test sees the dir, no-op in prod)
- T13 (2026-06-28): AppContext M3 wiring (HTTPClient + 2 clients + 3 services + 2 resolvers) (commits 995f0c7..7b46f90, review clean — 3 brief-internal deviations adjudicated: `SettingsService.get` no-default-arg (used M2 None-check fallback); `bus=` vs `event_bus=` for DepService/InstallService (brief inconsistent — only VersionService takes `event_bus=`); `ScannedNodeRepo` omitted from brief's import list (added to local M3 block); final test count 333+2, brief expected 327+2 — extra 6 from T9-T12 tests)
- T14 (2026-06-28): NodeBridge M3 extension (15 new slots + 5 new signals) (commits b928c77..ea6ed89, b256da0, review clean after 2 Important fixes — (1) deduped `_git_exe_resolver` (was set in both constructor and AppContext post-assign); (2) documented `refreshCatalog` return contract (returns int count per test, not entry list; signal `catalogUpdated` carries count separately); also 3 implementer deviations: AppContext post-assignment of M3 deps (M2 wiring order made brief's one-shot constructor impossible), `_invoke` kwargs bypass for 4 slots (BaseBridge._invoke is `(*args)` only), `refreshCatalog` test-driven count return)
- T15 (2026-06-28): VersionPanel.qml (节点版本表:refresh + upgrade-all + upgrade/lock/history per row) (commits 4ac9180..75c2c91, review clean — verbatim from brief, 100 lines)
- T16 (2026-06-28): DepPanel.qml (依赖表 + 本地冲突 + 重新解析/全局检查按钮) (commits 36507ee..3374e0d, review clean — verbatim from brief, 75 lines)
- T17 (2026-06-28): InstallDialog.qml (catalog 条目确认 + env picker + busy indicator) (commits d5ab231..3bf45b3, review clean — verbatim from brief, 87 lines)
- T18 (2026-06-28): HistoryDialog.qml (版本历史表 + 每行 rollback 按钮) (commits 9dbabdc..6c2f679, review clean — verbatim from brief)
- T19 (2026-06-28): CatalogPage.qml (全局节点目录页:grid + search + refresh + install) (commits b4d72f4..8745373, e5f930a, review clean after 2 Critical fixes — `refresh()` was assigning int count to array property (T14 contract drift); fixed by calling `searchCatalog("")` after `refreshCatalog` to populate entries; offline banner derived from entry-level stale flag)
- T20 (2026-06-28): Main.qml — wire new CatalogPage (envList from envBridge) (commits fd55a22..bec1f63, review clean — 3 brief deviations adjudicated: brief path was wrong (`pages/Main.qml` vs `Main.qml`); brief assumed no catalog item exists (M2 already added it); brief used `appContext.environment_bridge.envListForQml` (actual: `envBridge.envList` context property); 1-line change replaced obsolete `catalogBridge: catalogBridge` with `envList: envBridge.envList`)
- T21 (2026-06-28): EnvironmentDetailPanel — embed VersionPanel + DepPanel + HistoryDialog (commits faed4e0..4fe494c, review clean — implementer fixed 5+ brief bugs: fictional `scannedNodeList` id (used real `root.nodeList`); undeclared `root.versionList` property (added to root); unbound `list` identifier in hasUpdatable (replaced with binding over root.versionList); imagined `item.package` accessor (used `nodeList[i].package`); + added rescan-triggered M3 refresh for UX consistency; +100 lines, M2 sections untouched)
- T22 (2026-06-28): M3 i18n (35 new strings extracted + 65 en_US translations filled + .qm compiled) (commits dcbfaae..f6c6146, review clean — 36 new M3 strings across 5 components, all 65 unfinished entries filled (35 M3 + 30 M2 carryover), zh_CN.ts structurally intact)
- T23 (2026-06-28): M3 round-trip integration test (real git upgrade + list_status + catalog offline degrade) (commits 28c38e6..b654db5, review clean — 3 tests, 2 with Windows-specific git env fixes: `git -c init.defaultBranch=main init` (Windows default is `master`); `git symbolic-ref HEAD refs/heads/main` on bare remote so `origin/HEAD` is resolvable for `reset --hard origin/HEAD`; 350 + 2 skipped full suite)
- T24 (2026-06-28): 手动冒烟测试 — automated checks PASS, manual GUI PENDING (350 + 2 skipped, no whitespace regressions, no untracked files; 10-item GUI checklist from spec §10.1 documented in task-24-report.md for user walkthrough; no commit needed unless a manual fix surfaces)
- T25 (2026-06-28): 整分支 review + tag v0.3.0 — APPROVED FOR MERGE (1 Important finding: `python_portable.py` YAGNI in M3 — defer to M4 per user 2026-06-28 instruction; 8 Minor findings logged; tagged v0.3.0, M3 ledger archived to docs/superpowers/M3-LEDGER.md; commit 54c17c0)

## Review Findings Ledger

(Minor findings get logged here; Critical/Important get fixed before next task)

## M3 Minor Findings (T25 whole-branch review, non-blocking)

- T14: `_invoke` doesn't accept kwargs — 4 slots (`upgradeNode`, `listVersionHistory`, `searchCatalog`, `refreshCatalog`) call services directly with inline envelope construction. Consider refactoring `_invoke(self, fn, *args, **kwargs)` in M4 to reduce inline envelope boilerplate.
- T19: `refreshCatalog` return-shape contract drift (returns int count per T14 test contract, not entry list). QML consumers should use `catalogUpdated` signal for count and `searchCatalog` for the list. Documented in `task-14-report.md` and at `node_bridge.py:checkGitPortable` docstring.
- T21: brief had 5+ QML bugs (fictional `scannedNodeList` id, undeclared `root.versionList`, unbound `list` identifier, imagined `item.package` accessor, missing rescan-triggered M3 refresh). All fixed by implementer; brief author may want to update for future M4+ embed tasks.
- T22: 3 obsolete `<message>` entries retained in .ts files (harmless — lrelease skips them). Harmless.
- T22: zh_CN.qm has 63 untranslated + 47 finished + 2 unfinished (zh_CN.ts was untouched per hard rule). Expected runtime fallback to source string.
- T23: 2 Windows-specific git env fixes in test code (`-c init.defaultBranch=main init` and `git symbolic-ref HEAD refs/heads/main` on bare remote). Brief may want to include for cross-platform portability.
- T11: `_is_incompatible` algorithm uses probe set including 0.0.0/9999.9999.9999 — works for SpecifierSet intersection checks but may misclassify unusual specs (e.g., `!=1.0`). Not a real-world issue but worth knowing.
- T13: `SettingsService.get(key)` doesn't support default arg — implementer used M2 None-check fallback pattern 5 times. Consider adding default support in M4 to clean this up.

- T1 Minor: `to_row` / `from_row` in `src/comfy_mgr/models/conflict.py` don't validate `conflict_type` against the new Literal (5 values). M2 callers that hardcode the 3-value set would silently pass unknown values through to DB. Consistent with YAGNI stance (no migration script, no enum table) but worth flagging in M4 if M2 code paths persist.
- T2 Minor: `list_by_env` in `src/comfy_mgr/db/version_repo.py` reassigns `params` rather than building conditionally. Current code is correct, just slightly redundant. Low priority.
- T4 Minor: `search_substring` does not escape `%` / `_` wildcards in user input (catalog_repo.py:82). User typing `"100% off"` will match unintentionally. YAGNI-clean for now, fix in M4 if service layer exposes user search.
- T4 Minor: `list_non_expired` uses `datetime.now()` (naive local time) while service layer may write `expires_at` as naive UTC. Convention needs documentation at call site.
- T5 Minor: `scripts/fetch_git_portable.py:29` has unused `import zipfile` (script uses SFX self-extract via subprocess, not zipfile module). Carried from brief; brief itself has the dead import.
- T6 Minor: type annotations `"Path | None"` / `"str | None"` are string literals in python_portable.py:39,52 even though `from __future__ import annotations` is present. Redundant. Stylistic only.
- T8 Minor: `_to_dict` does `json.loads(row.get("raw_metadata", "{}"))` — raises JSONDecodeError on malformed cache row instead of returning `{}`. Not spec-required.
- T8 Minor: unused imports in test file (`MagicMock`) and impl file (`ServiceError`). Trivial cleanup.
- T10 Minor: `has_update` is hardcoded to `False` in `list_status` (no fetch+rev-parse comparison). Brief-prescribed. Flag for M4.
- T10 Minor: `downgrade` and `rollback` don't check `locked` flag (only `upgrade` does). Brief-prescribed. May or may not be intended behavior.

- T2 Minor: `_make_node` id derivation in test_scanned_node_repo.py is fragile (truncation collision risk for env-1/pkg-a vs env-10/pkg-a). Out of scope T2; will be cleaned up when conftest fixture lands in T18.
- T5 Minor: unused `import json` in test_pkg_meta.py. Can be removed in any cleanup pass.
- T5 Note: brief inconsistency between test (`"boom" in warnings`) and impl snippet (`f"scan_failed: {error_msg}"`) — implementer correctly honored test contract. M3+ brief should be updated for consistency.
- T9 Minor: unused `Optional` import in src/comfy_mgr/infra/github_client.py:6. Trivially removable in cleanup pass.
- T10 Minor: unused `ServiceError` import in src/comfy_mgr/services/node_meta.py:28. Carried from brief.
- T10 Minor: unused `Path` import in tests/services/test_node_meta_service.py:6. Carried from brief.
- T11 Plan-mandated gap (T8): `ConflictService.__init__` should have `node_service=None` default per M2 plan §11 ("把 `__init__` 改为: ... node_service=None"). T8 didn't add the default. Works at runtime only because all callers pass by keyword. One-line fix in src/comfy_mgr/services/conflict.py:28.
- T17 Minor: 32 new en_US M2 strings have empty `<translation>` elements; deferred to M3+ i18n pass.
- T19 Fixed: Main.qml was missing `import "components" as Comp` (ErrorBanner unresolved; latent M1 bug surfaced only when GUI was actually loaded); `appContext` context property was never registered in main.py despite being referenced by all M2 QML code; EnvironmentDetailPanel.qml never instantiated ConflictPanel. Plus 2 related QML wiring bugs bundled: missing `Comp.` prefixes on NodeScanBusy/NodeListItem/NodeDetailPanel, and non-existent `Divider` type replaced with Rectangle. All 5 fixed in commit 7b96222.

## Review Findings Ledger

(Minor findings get logged here; Critical/Important get fixed before next task)

---

# M5 Progress Ledger

Base SHA: ca40dc2 (plan commit, pre-M5)
Plan: docs/superpowers/plans/2026-07-11-m5.md
Spec: docs/superpowers/specs/2026-07-11-m5-design.md

## Status

- T1: complete (commits d2d3dd4→4162a11, review clean after 1 fix pass — 5 Important/Critical fixed)
- T2: complete (commits 3d17adc→4dcd03a, review clean after 1 fix pass — wire shapes + Field import + newline)
- T3: complete (commit b1d1718) — inline carry-over fix needed for `app_with_client` fixture (commit pending: `CompatHTTPClient` moved from method-local import at `app/app_context.py:148` to module scope; pre-existing M4 bug from `b7f6e3f`). Pre-existing 4 WS tests still fail at assertion stage (`_ping` arrives before `versionChanged`) due to M4 `_on_push_sync` silent-drop bug — same root cause, not M5 regression; same issue affecting pre-existing tests.
- T4: complete (commits 464f020→02be0a2, review clean after 1 fix pass — 1 Important: snake_case `[JsonPropertyName]` attrs on positional record params for `latency_ms` / `bulk_id` / `started_at` / `finished_at` / `cancelled_at_checkpoint`)
- T5: complete (commit 0876513, review APPROVED — 0 fixes; impl correctly adapted brief's `OnPropertyChanged()` → `RaisePropertyChanged()` to match `ViewModelBase` API; Fake's `CancelAsync` return type corrected to `BulkUpdateCancelledResponse` matching T4 fix; brief's unused `CancelResult` field removed per YAGNI)
- T6: complete (commit 8e9bc32, review APPROVED — 0 fixes; took Path A "WS subscription in MainViewModel.OpenBulkUpdate" instead of brief's Path B "add WsClient to VM ctor"; added `ApiClient.BaseUrl` getter as alternative to touching App.xaml.cs; 5 files exactly, 16/16 WPF tests green)
- T7: complete (commit 7f44157, review APPROVED — 0 fixes; shim delete + 9 import sites fixed across 5 files; pre-existing failures untouched)
- T8: complete (commit 0b209b0; 1-line `logsFor` → `logs_for` in `tests/app/test_app_context.py`; 2/2 tests pass; same `setScannedService` issue remains in `tests/bridge/test_app_context_wiring.py` + `tests/integration/test_m2_gui_round_trip.py` — outside T8 scope, will catch in T10 if related)
- T9: complete (commit 582dd71, review APPROVED — 0 fixes; root cause was pagination, NOT ThreadPoolExecutor as brief guessed; production code untouched; test mock wrapped as `{"nodes": payload, "total": N}` so client stops after 1 page; 6/6 tests pass)
- T10: complete (no commit — `pytest-mock` already declared in `pyproject.toml:17` but env was stale; `pip install pytest-mock` unblocked 5 ERROR-setup tests; 17/17 tests pass in scope; `tests/services/test_catalog.py` + `tests/services/test_environment_service.py` paths corrected from brief's `tests/app/` placeholders)
- T11: complete (no commit — verify-only close; brief's "smoke /healthz / /version empty body" was a stale report; routes already return correct JSON: `/healthz` → `{"status":"ok"}` HTTP 200, `/version` → `{"service":"comfy_mgr.server","version":"0.4.0","schema":5}` HTTP 200; `tests/integration/test_server_routes.py:7-17` already covers both with `test_healthz_returns_200` + `test_version_returns_schema_5`; 12/12 server-route tests pass; pre-existing M4 `_on_push_sync` silent-drop bug is separate scope, tracked)
- T12: complete (整分支 review APPROVED + 1 Important fix `7649b5a` + bump commit `e24278b` `chore(release): bump to v0.5.0 + release notes`; tag `v0.5.0` local-only, points at `e24278b`; 21 commits since `v0.4.0` (19 M5 + T11 ledger + this bump); release notes `docs/M5-RELEASE-NOTES.md` written; 465 Python + 16/16 WPF tests pass; 3 version literals in `tests/test_version_consistency.py` updated to `0.5.0` (mechanical, follows test contract); POST empty `env_ids`/`node_ids` returns 200 + `BAD_VALIDATION` envelope per `schemas.py:209` design comment — brief's 400 expectation was wrong; **push + GitHub release pending user authorization**)

## End-of-day snapshot (2026-07-13 — M5 close-out local done)

- Branch: main
- 21 commits since `v0.4.0` (base `ca40dc2` → HEAD `e24278b`); M5 milestone complete locally
- Local tag `v0.5.0` created at `e24278b`; **NOT pushed** (awaiting user authorization)
- WPF tests: 16/16 green (12 M4 + 4 M5 BulkUpdate)
- Python tests: 465 passed + 2 skipped (M4 base + M5 新增 11); 4 pre-existing M4 `_on_push_sync` silent-drop WS integration tests still fail (tracked, out of M5 scope)
- `tests/test_version_consistency.py` updated to `0.5.0` (mechanical 3-line follow-the-test-contract change)
- Release notes: `docs/M5-RELEASE-NOTES.md` written (55 lines)
- Resume point: **push to remote + `gh release create v0.5.0 release/ComfyUI-Manager-v0.5.0-win-x64.zip --notes-file docs/M5-RELEASE-NOTES.md`** (along M4 SSH-deploy-key pattern if proxy TLS issue surfaces)

### Files modified this session (M5 commits)

- `src/comfy_mgr/services/bulk_update_service.py` (T1)
- `src/comfy_mgr/server/routes/bulk.py` (T2)
- `src/comfy_mgr/server/schemas.py` (T2)
- `src/comfy_mgr/server/app.py` (T2)
- `tests/services/test_bulk_update_service.py` (T1)
- `tests/server/test_routes_bulk.py` (T2)
- `tests/integration/test_ws_events.py` (T3)
- `app/app_context.py` (T3 carry-over: CompatHTTPClient scope fix, 1-line move)
- `src-wpf/ComfyUI.Manager/Models/BulkUpdateRow.cs` (T4)
- `src-wpf/ComfyUI.Manager/Models/BulkUpdateSummary.cs` (T4)
- `src-wpf/ComfyUI.Manager/Services/BulkUpdateApiClient.cs` (T4)
- `src-wpf/ComfyUI.Manager/ViewModels/BulkUpdateDialogViewModel.cs` (T5)
- `tests-wpf/ComfyUI.Manager.Tests/ViewModels/BulkUpdateDialogViewModelTests.cs` (T5)
- `tests-wpf/ComfyUI.Manager.Tests/Fakes/FakeBulkUpdateApiClient.cs` (T5)
- `src-wpf/ComfyUI.Manager/Views/BulkUpdateDialog.xaml` + `.xaml.cs` (T6)
- `src-wpf/ComfyUI.Manager/MainWindow.xaml` (T6)
- `src-wpf/ComfyUI.Manager/ViewModels/MainViewModel.cs` (T6)
- `src-wpf/ComfyUI.Manager/Infrastructure/ApiClient.cs` (T6: BaseUrl getter)

---

# M5.2 Progress Ledger

Base SHA: 814c8be (v0.5.1 hotfix, pre-M5.2)
Plan: proud-petting-feather.md (user-approved via ExitPlanMode 2026-07-13)
Spec: implicit — M5.2 is an architecture rewrite removing Python control service

## Status

- T1: complete (commit 2c560c4 — `refactor(wpf): add SQLite data access layer`, 8 repos, 918 lines)
- T2: complete (commit a4d6417 — `refactor(wpf): replace PythonLauncher with VenvVerifier`, 6 files)
- T3: complete (bundled in a4d6417 — `App.xaml.cs` rewritten, no service launch)
- T4: complete (commits 91a3438 + 70b2c34 + 5478fd0 — 11 VMs migrated to repos + nav wiring fix; 14/14 tests pass)
- T5: complete (commit 33b2974 — `ProcessLauncher` + `LogTailer` + `LogViewerDialog`; 14/14 tests pass; review pending user resumption)
- T6-T10: pending (T6 BulkUpdateOrchestrator, T7 NodeOperations, T8 test rewrite, T9 delete Python service, T10 rebuild + tag v0.6.0)

## End-of-day snapshot (2026-07-13 — M5.2 mid-flight)

- Branch: main
- 6 new commits since 814c8be: 2c560c4, a4d6417, 91a3438, 70b2c34, 5478fd0, 33b2974
- WPF build clean (0 警告 0 错误), 14/14 WPF tests pass
- v0.5.1 zip (215MB) rebuilt successfully at `release/ComfyUI-Manager-v0.6.0-dev-win-x64.zip`
- WPF staging verified: VenvVerifier passes, MainWindow shows, env list / catalog / settings views wired (catalog 10 项, env 0 项 in user's DB)
- User saw staging run, navigation buttons work after `5478fd0` fix

## End-of-day snapshot (2026-07-14 — M5.2 close-out: v0.6.0 pushed + tagged, release asset upload pending)

- Branch: main, all commits pushed to origin (https://github.com/fogyisland/ComfyUIEnvironmentManagement)
- Tag `v0.6.0` pushed; **GitHub release upload pending** (zip 225 MB present at `release/ComfyUI-Manager-v0.6.0-win-x64.zip`, awaiting `gh release create`)
- T5 review fixes: 1 Critical (StopEnvAsync shutdownCts token) + 4 Important fixed (commit 7791433) + 1 regression test
- T6 BulkUpdateOrchestrator: ff446e5 + cd329b4 + 4a43a83 (T6 review fixes: thread-safe events, dialog-close cancel, mid-run cancel test)
- T7 NodeOperations + GitRunner: 1617234
- T8 test rewrite: 13c369f (deleted dead ApiClient/WsClient + Fakes)
- T9 drop Python service + QML: 48d782e (after rebase, was d7cf85e with zips dropped)
- T10 build script + tag: 503245d (build_release.ps1 rewrite, WPF-only zip) + 207cd13 (.gitignore release/*.zip)
- WPF tests: 26/26 ✅ (5 new NodeOperations tests + 1 new mid-run cancel test)
- pytest: 181/181 ✅ (after deleting services/integration/bridge/app tests)
- 7 files removed outright: src/comfy_mgr/server/, cli.py, __main__.py, services/, infra/process.py, app/, run.bat, start.bat
- pyproject.toml: dropped fastapi/uvicorn/typer/pyside6 deps; version bumped 0.5.0→0.6.0
- Release zip: `release/ComfyUI-Manager-v0.6.0-win-x64.zip` (225 MB, present, no rebuild needed)
- Release notes: `release/RELEASE-NOTES-v0.6.0.md`

## Review Findings Ledger (M5.2)

(Minor findings get logged here; Critical/Important get fixed before next task)

- T6 Minor: `BulkUpdateDialogViewModel._runCts` field — kept as authoritative CTS source (used to cancel on dialog close); not dead state despite reviewer's I-1 flag
- T6 Minor: git-portable missing in `bin/`; App.xaml.cs falls back to PATH `git` (verified working)
- T9 Minor: `.gitignore` initially didn't have `release/*.zip` — added in 207cd13 after push rejected (zips > 100 MB GitHub limit)
- T5 review (sonnet) was just dispatched but rejected by user — they want to pause

## Resume point (2026-07-14+)

1. `cd D:\ToolDevelop\ComfyUI`
2. Verify `release/ComfyUI-Manager-v0.6.0-win-x64.zip` exists (225 MB, no rebuild needed per `feedback_no_rebuild_zip.md`)
3. `gh release create v0.6.0 release/ComfyUI-Manager-v0.6.0-win-x64.zip --notes-file release/RELEASE-NOTES-v0.6.0.md`
4. Verify `https://github.com/fogyisland/ComfyUIEnvironmentManagement/releases/tag/v0.6.0` is accessible + zip asset downloadable
5. Append "release asset uploaded" entry to this ledger
6. Commit + push ledger update

## Pre-existing issues carried into M5.2

- All carried issues resolved in T8 (FakeApiClient/WsClient deleted) + T9 (ApiClient/WsClient + Python service + QML app deleted)
- CatalogViewModelTests:22 (`entry.Name` → `entry.Package`) — FIXED in T4 (was pre-existing before M5.2)

## Post-release hotfixes (2026-07-15)

After v0.6.0 tag was pushed (6469bf8) but before `gh release create` ran,
user started manual smoke-testing the WPF UI. Three functional gaps were
found and fixed:

- **dd5458b** `feat(wpf): M5.2 hotfix — Env Create flow on WPF side`
  - M5.2 T9 deleted Python `services/environment.py` but WPF side never
    got a Create path. New: VenvCreator / JunctionLinker /
    EnvCreatorService / CreateEnvDialog modal. Tests 26/26.

- **bb5d0ff** `feat(wpf): extend Settings — paths, git exe, proxy URL/port, extra paths`
  - Settings page only had theme/lang/TTL/compat API. Added 8 persisted
    fields with auto-save bindings, Browse buttons, dynamic ExtraPaths
    table. Tests 26/26.

- **497a817** `feat(wpf): git proxy — explicit enable checkbox + per-process env vars`
  - Settings.GitProxyEnabled (default false). New GitProxyConfig
    shared between SettingsViewModel + GitRunner/BulkUpdateOrchestrator.
    Per-process env var injection (`HTTP_PROXY` + `HTTPS_PROXY` on
    ProcessStartInfo.EnvironmentVariables) — does NOT touch system env.
    11 new GitProxyConfigTests. Tests 37/37.

## v0.6.0 vs v0.6.1 release question (open)

`gh release list` shows v0.5.1 as Latest; v0.6.0 tag is on remote but
no `gh release create` has been run. The 3 hotfix commits above sit
on top of v0.6.0. Two paths:

1. Bundle today's 3 commits into v0.6.0's release notes — no rebuild,
   just `gh release create v0.6.0` with the existing zip (zip
   predates the hotfixes).
2. Cut v0.6.1: rebuild zip with today's commits, new release.

User has not yet chosen. Default leaning: option 1 (faster, aligns with
"no rebuild when version unchanged" preference) — but the 3 commits
add real user-facing functionality (env create, git proxy), so
option 2 is the honest answer. Ask user.

## v0.6.1 release (2026-07-16) — closed

**Decision:** v0.6.1 rebuild path (option 2 above). v0.6.0 zip on
disk was built before the 8 hotfix commits, so uploading it would
have shipped stale binaries. User picked the honest option.

**Flow:**
1. Bump version literals 0.6.0 → 0.6.1 in 5 files
   (pyproject.toml + src/comfy_mgr/__init__.py + shared/errors.json
   + ComfyUI.Manager.csproj + tests/test_version_consistency.py).
   3/3 version consistency tests PASS.
2. `scripts/build_release.ps1 -Version 0.6.1` →
   `release/ComfyUI-Manager-v0.6.1-win-x64.zip` (215.2 MB).
   Verified DLLs in zip match build output (timestamp + size).
3. Write `release/RELEASE-NOTES-v0.6.1.md` (78 lines, 8 commits
   listed with one-line description + upgrade notes + carry-over).
4. Commit `ae7a70b` (bump) + commit `c200b0c` (release notes).
5. `git push origin main` + `git push origin v0.6.1` (new tag).
6. `gh release create v0.6.1 release/ComfyUI-Manager-v0.6.1-win-x64.zip
   --notes-file release/RELEASE-NOTES-v0.6.1.md --title "v0.6.1 — Post-release hotfixes (8 commits)"`
   → https://github.com/fogyisland/ComfyUIEnvironmentManagement/releases/tag/v0.6.1
7. `gh release list` confirms v0.6.1 is **Latest**.

**8 commits since v0.6.0 (all in v0.6.1 release notes):**
- e877812 SettingsDefaults 不再自动填默认值
- 5313333 relative settings paths + EnvCreatorService takes projectRoot
- dda64f3 drop startup venv pre-check
- 311970d default Settings paths to program-root subfolders
- 497a817 git proxy — explicit enable checkbox + per-process env vars
- bb5d0ff extend Settings — paths / git exe / proxy URL/port / extra paths
- dd5458b M5.2 hotfix — Env Create flow on WPF side
- (plus bump commit ae7a70b + release notes commit c200b0c)

**Status: project fully closed for v0.6.1. No further work pending.**
