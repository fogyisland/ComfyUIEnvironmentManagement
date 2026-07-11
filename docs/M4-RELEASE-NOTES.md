# ComfyUI Manager v0.4.0 Release Notes

**Release date**: 2026-07-07
**Milestone**: M4 — UI 迁移到 WPF + C#

## 重大变更

UI 栈从 PySide6+QML 整体迁移到 .NET 8 WPF + C#。Python 后端(M0-M3 的 service / repo / DB / SQLite)完整保留。WPF 通过 REST + WebSocket 与本地 Python service 通信。

### 进程模型

- WPF 启动时拉 Python service 作**隐藏后台子进程**(`CreateNoWindow=true`)
- WPF 关闭 → Python service 3s 内 SIGTERM,超时 Kill
- 端口冲突检测:WPF 启动时检测 7800 占用 → 拒绝并提示

### 协议

- 51 个 REST endpoints(1:1 镜像 M3 Bridge Slot)
- 23 个 WebSocket event channels
- 响应统一 envelope:`{"ok": bool, "value": ...}` / `{"ok": false, "error": {"code", "message"}}`

### M3 Deferred Hooks 激活

- `compat_api_base_url` 默认值 + 失败降级
- `catalog_cache_ttl_minutes` UI 暴露
- `scanned_nodes.locked` UI + 锁状态阻止升级/降级/回滚
- `folder_rename` disable mode 真正生效(磁盘重命名)
- Semver 智能版本比较(替换 commit SHA 精确回滚之外的对比)
- NodeScanner `IMPORT_FALLBACK` 兜底(importlib 实际加载)

### M3 Review Ledger 清理

11 项 minor findings 全部 close(L1-L11)。

### Schema 升 v5

- `version_history.pkg_version`(Semver 解析后的版本号)
- `scanned_nodes.disable_mode`(`db_flag` | `folder_rename`)

## 测试覆盖

- Python:350+ 既有 + M4 新增 ~16 = ~366 tests
- WPF:~25 ViewModel + Infrastructure tests
- 集成:test_server_routes / test_ws_events / test_server_lifespan
- 冒烟:`docs/M4-SMOKE.md` 12 项手动清单 + `scripts/smoke.sh` 自动化

## 下载

`ComfyUI-Manager-v0.4.0-win-x64.zip`(约 70 MB)

解压后双击 `ComfyUI Manager.exe` 即可。Python 服务会自动后台启动,无控制台窗口。

## 升级路径(v0.3.x → v0.4.0)

- `%APPDATA%\ComfyUI-Manager\catalog.db` 沿用,DB schema 自动 v3 → v4 → v5 migrate
- `%APPDATA%\ComfyUI-Manager\settings.json` 沿用,新字段默认值
- `python/` + `bin/git-portable/` 随 zip 覆盖
- 用户的 env 目录不受影响

## 已知问题

无。

## 下一步(M5+ 评估)

- WPF UI 自动化测试(FlaUI)
- WebView2 嵌入 ComfyUI 主界面
- Service 多 WPF 实例共享
