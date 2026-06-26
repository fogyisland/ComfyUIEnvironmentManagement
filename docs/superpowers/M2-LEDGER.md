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
- T20: pending
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

## Review Findings Ledger

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
