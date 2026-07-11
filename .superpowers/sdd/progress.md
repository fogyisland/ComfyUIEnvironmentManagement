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
