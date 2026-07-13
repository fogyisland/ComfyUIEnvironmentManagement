# ComfyUI Manager v0.5.0 Release Notes

**Release date**: 2026-07-12
**Milestone**: M5 — 节点批量更新 + Carry-over 收尾

## 新增功能

### 跨 env × 节点批量 git pull 更新

痛点:10 env × 20+ 节点 = 200+ 次单节点手动 git pull。

- WPF 侧边栏新增 "批量更新" 入口 → 打开 BulkUpdateDialog
- 网格显示 env × 节点组合,顶部 "全选" checkbox
- 一键 Start → 后台**串行** `git pull`(per spec §4.1),WS `bulk_update` channel 实时推送每行结果(progress / started / completed / cancelled / failed)
- 失败策略 best-effort:dirty tree / git lock / 版本锁定 → skipped,不阻断后续
- Cancel 在 (env, node) 边界停(不会中断 mid-`git pull`),`cancelled_at_checkpoint` 持久化
- 关闭 dialog 不取消 bulk;下次打开可恢复进度(RefreshAsync)
- 历史 in-memory(per service lifetime)

## 技术债清理 (M4 carry-over)

| ID | 任务 | 状态 |
|---|---|---|
| CO-1 | 删 `src/comfy_mgr/app_context.py` re-export shim,9 import sites 改用 `app.app_context` | ✅ 7f44157 |
| CO-2 | `test_app_context.py` camelCase `logsFor` → snake_case `logs_for` | ✅ 0b209b0 |
| CO-3 | `test_catalog_http_client.py` 分页 mock shape fix(加 `"total"` 字段) | ✅ 582dd71 |
| CO-4 | `test_catalog.py` + `test_environment_service.py` pytest-mock env unblock | ✅ env only |
| CO-5 | `/healthz` / `/version` smoke 路由 verify-only close | ✅ dda1705 |

## REST endpoint delta (新增 3 个)

- `POST /api/v1/bulk-update/start` — 启动批量更新,返回 `bulk_id`
- `POST /api/v1/bulk-update/{id}/cancel` — 取消进行中的批量更新
- `GET  /api/v1/bulk-update/{id}` — 查询状态(summary + per-row)

所有响应统一 envelope `{"ok": bool, "value": ...}` / `{"ok": false, "error": {"code", "message"}}`。

## WebSocket channel delta (新增 1 个)

- `bulk_update` — 事件:`started` / `progress` / `completed` / `cancelled` / `failed`
  - payload 字段:`bulk_id` / `total` / `succeeded` / `skipped` / `failed` / `rows[]` / `latency_ms` / `cancelled_at_checkpoint`

## Bug fixes

- `7649b5a` — WPF `MainViewModel.OpenBulkUpdate` WS handler 补 `bulk_update.failed` 分支:设 `ErrorMessage` + 翻 `Mode=Summary`,避免 laten deadlock(整分支 review 1 Important finding)

## Tests

- Python: 465 passed(M4 base + M5 新增 11),2 skipped
- WPF: 16/16(M4 base 12 + M5 新增 4)
- Pre-existing M4 `_on_push_sync` silent-drop bug 影响 4 个 WS integration tests,不在 M5 scope,tracked

## Commits

19 commits since `v0.4.0`(base `ca40dc2`)。详见 `git log v0.4.0..HEAD`。
