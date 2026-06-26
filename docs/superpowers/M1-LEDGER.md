# M1 GUI + 环境管理 — Subagent Progress Ledger

## 任务总览
- Plan: `docs/superpowers/plans/2026-06-25-m1-gui.md` (commit 73ec974)
- BASE: 8790723 (M1 spec)
- 26 tasks total
- 执行方式: Subagent-Driven
- 控制器: Claude Opus 4.7 (主 session)

## 任务执行记录

### Task 1: pyproject + PySide6 + qapp fixture
- Status: DONE_WITH_CONCERNS
- Commits: 9a15d58
- Concerns:
  - requires-python 改为 >=3.10,<3.13（原 >=3.9，因 pyside6 兼容性）
  - MINGW shell 中文 garbled（功能正常，仅显示问题）
- Reviewer: 待派发

- Fix commits: 86723d8 (drop unused QGuiApplication + add qapp smoke test)
- Final status: COMPLETE (105 passed + 2 skipped)

### Task 2: schema v2 + process_state 表
- Status: DONE
- Commits: 00dca62 + 33b14a5 (fix)
- Test count: 107 + 2 skipped

### Task 3: settings theme_mode 字段
- Status: DONE (Approved)
- Commits: c4b5a20
- Brief bug: T3.2 第 3 个测试路径写错，implementer 修复（settings.json 在 ComfyUI-Manager/ 子目录）
- Test count: 110 + 2 skipped

### Task 4: BaseBridge + AppContext
- Status: DONE (Approved after fix)
- Commits: 42cc322 + 851cfde (fix: remove stub qtbot conftest)
- Concerns resolved: pytest-qt now properly installed, stub removed
- Brief bug: T4.4 test `key="val"` 用了 _invoke 不支持的 **kwargs, implementer 静默修复
- Test count: 113 + 2 skipped

### Task 5: SettingsBridge
- Status: DONE (Approved with brief-bug fix)
- Commits: 3a91223 + 735cf3f (newline fix)
- Brief bug: T5.1 setValue 用 _invoke 调 SettingsService.set，但 set 返回 None 不是 Result。Implementer 内联 try/except 修复
- Test count: 117 + 2 skipped

### Task 6: EnvironmentBridge
- Status: DONE (Approved with brief-bug fix)
- Commits: afa8ddd
- Brief bug: T6.1 createEnv impl 漏发 envCreated + envListChanged signal，tests 期望发，implementer 修复
- Test count: 123 + 2 skipped

### Task 7: ProcessState model + Repo
- Status: DONE (Approved)
- Commits: 79ba46c
- Brief bug: T7.2 测试插入 process_state 缺父级 environments 行，违反 FK，implementer 加 _insert_env helper 修复
- Test count: 128 + 2 skipped (但收集总数显示 124+11=135，所以 T7 实测 124 通过)

### Task 8: QProcessBackend + ProcessService rewrite
- Status: DONE (Approved after fix)
- Commits: c7146a5 + 1f75f78 (fix: save-failure propagate + buffer clear)
- Brief bugs found + fixed:
  - Test fixture 未插 parent environments 行，process_state FK violation — 加 fixture insert 修复（与 T7 同模式）
  - brief `_on_finished` buffer flush no-op bug — 修复（改用 setattr 清 attribute）
  - `ProcessService.start()` silently swallows `save()` Result — 修复（rollback backend + return PROCESS_STATE_SAVE_FAILED）+ 1 regression test
- Test count: 134 + 2 skipped (11 new + 1 regression)

### Task 9: ProcessBridge + LogViewer.qml + StatusIndicator.qml
- Status: DONE (Approved with concerns)
- Commits: 0b9cb94
- Brief bugs found: 无
- Concerns raised (minor, 非阻塞):
  - `_env_resolver` 未在 __init__ 初始化（truthy guard 兜底，AppContext 立即 set_env_resolver）
  - `logLines` Property 文档注释说"按时间倒序"但实际按 dict 顺序（brief verbatim，harless）
- AppContext update: M0 → T8 ProcessService 签名，import ProcessStateRepo，移除 stale comment
- QML files reference Theme singleton (T16 会创建) — 预期
- Test count: 140 + 2 skipped (6 new in test_process_bridge.py)

### Task 10: EnvironmentPage + EnvironmentDetailPanel QML
- Status: DONE (Approved)
- Commits: 6873605
- Brief bugs found: 无需修复
  - `Component.onCompleted: envBridge.envListChanged` no-op（brief verbatim，harmless dead code）
  - `searchField` declared but not wired（brief 未要求 filter 逻辑）
- Forward deps: Theme (T16), CreateEnvDialog (T12) — 预期
- Test count: 140 + 2 skipped (无新增 Python)

### Task 11: CreateEnvDialog.qml
- Status: DONE (Approved)
- Commits: 80a0ba0
- Brief bugs found: 无
- Forward deps: Comp.FormField/PathField (后续 task), Theme (T16) — 预期
- Test count: 140 + 2 skipped (无新增 Python)

### Task 12: CatalogBridge + CatalogPage
- Status: DONE (Approved with concerns)
- Commits: 6578551
- Brief bugs found: 无
- Concerns raised (minor, 非阻塞):
  - `ListView.view.width` 在 Qt 6 已 deprecated（brief verbatim）
  - `import "../components" as Comp` unused（harmless YAGNI nit）
  - `listNodes` Slot 未被 QML 使用（brief 要求，kept verbatim）
- Test count: 144 + 2 skipped (4 new in test_catalog_bridge.py)

### Task 13: NodeBridge (M1 minimal)
- Status: DONE (Approved)
- Commits: 2da2df5
- Brief bugs found: 无
- Test count: 147 + 2 skipped (3 new in test_node_bridge.py)
- AppContext DI 不变（import 路径一致）

### Task 14: TorchBridge
- Status: DONE (Approved)
- Commits: cb2be92
- Brief bugs found: 无
- AppContext cleanup: TorchHelper stub class 移除，`torch_helper`/`cuda` 属性移除，`TorchBridge(environment=, pytorch=)` 正确连线
- 移除 stale "T4 已知偏差" docstring
- Test count: 151 + 2 skipped (4 new in test_torch_bridge.py)

### Task 15: i18n setup + main.py bootstrap
- Status: DONE (Approved with concerns)
- Commits: ece154d
- Brief bugs found: 无
- Concerns raised (minor, 非阻塞):
  - `QGuiApplication` 和 `QLocale` imports unused (kept verbatim)
  - `<!DOCTYPE TS>` uppercase is valid
  - Runtime 失败直到 T16 创建 Main.qml — 预期
- Test count: 151 + 2 skipped (无新增 Python)
- 4 files: zh_CN.ts (162), en_US.ts (162), i18n.py (24), main.py (16→44)

### Task 16: Theme.qml singleton + qmldir
- Status: DONE (Approved)
- Commits: 0262ba1
- Brief bugs found: 无
- 13 Material You palette keys per mode，defensive styleHints guards
- Test count: 151 + 2 skipped (无新增 Python)
- 2 files: Theme.qml (56), qmldir (1)

### Task 17: FormField + PathField + ConfirmDialog
- Status: DONE (Approved with concerns)
- Commits: 1e12d75
- Brief bugs found: 无（concerns 都 inherit 自 brief）
- Concerns raised (minor, brief-mandated, 非阻塞):
  - FormField `default property alias dataField` no-op（ColumnLayout 自带 data default）
  - PathField `Qt.createQmlObject` per click（inefficient, leak risk）
  - PathField `nameFilters` string concat 无 quoting
  - ConfirmDialog `property var onConfirm/onCancel` 非 idiomatic
  - 3 files 缺 trailing newline
- Test count: 151 + 2 skipped (无新增 Python)

### Task 18: ErrorBanner + error wiring
- Status: DONE (Approved with concerns)
- Commits: 24a736f
- Brief bugs found: 无
- Concerns raised (minor, brief-mandated, 非阻塞):
  - ErrorBanner per-page 而非 global in Main.qml（跨 page error 不显示）
  - Connections 无 null guards（bridges 可能 null if props not bound yet）
  - 两个 Connections duplicate handler
- Test count: 151 + 2 skipped (无新增 Python)

### Task 19: SettingsPage.qml
- Status: DONE (Approved with concerns)
- Commits: cb8fcb2
- Brief bugs found: 无
- Concerns raised (minor, brief-mandated, 非阻塞):
  - ComboBox model entries for 主色调/语言/日志级别 缺 qsTr() wrapping
  - File 缺 trailing newline
- 6 config fields all wired: 数据库路径/主题模式/主色调/语言/日志级别/默认 Python
- Test count: 151 + 2 skipped (无新增 Python)

### Task 20: Main.qml + qmldir
- Status: DONE (Approved)
- Commits: 1967edd
- Brief bugs found: 无
- ApplicationWindow 完整结构：header ToolBar / Drawer / StackLayout / 6 Connections / globalError
- qmldir 加 module 声明
- Test count: 151 + 2 skipped (无新增 Python)

## T21-T26 续执行 2026-06-26

### Task 21: process_state 启动恢复
- Status: DONE (Approved)
- Commits: 69fcadd
- Brief bugs found: 无（brief-mandated limitation 已 inline comment 标注）
- Test count: 151 + 2 skipped (无新增 Python)

### Task 22: scripts/build_zip.py
- Status: DONE (Approved with concerns)
- Commits: 2889eac
- Zip output: 0.1 MB, 58 files
- Concerns raised (minor, brief-mandated, 非阻塞):
  - Unused imports: os, shutil, sys
  - Missing trailing newline
- Test count: 151 + 2 skipped

### Task 23: start.bat + README + update_translations.bat
- Status: DONE (Approved)
- Commits: 9ded6ae
- README overwrote M0 CLI README per brief（acceptable）
- Test count: 151 + 2 skipped

### Task 24: MANUAL_SMOKE.md
- Status: DONE (Approved)
- Commits: 8d506cb
- 8 sections × ~3 items each = 32 个 checkbox items
- Test count: 151 + 2 skipped

### Task 25: 全套测试 + 整体代码 review
- Status: DONE
- 151 passed + 2 skipped (60.93s)
- imports OK
- chore: ignore dist/ build output (commit f674e45)

### Task 26: Whole-branch review + final merge
- Whole-branch review (commit 8790723..f674e45): 31 commits, 301KB diff
- Reviewer found 3 CRITICAL bugs:
  1. AppContext sets `bridge_sink` instead of `_bridge_sink` — live log streaming broken
  2. EnvironmentDetailPanel LogViewer never updates (`logsFor` is Slot, no notify)
  3. Duplicate ErrorBanner in EnvironmentPage + global in Main.qml
- Reviewer found 2 IMPORTANT:
  4. Recovery shows stale "running" for dead PIDs
  5. i18n coverage gap (52 qsTr calls vs 31 .ts entries)
- Fix subagent (Opus): all 5 fixed in atomic commit `44656f5`
  - C#1: Reordered AppContext — `process_bridge` constructed before `process`, `bridge_sink` passed in constructor
  - C#2: Added `logVersion` Property with notify=processLogLine + QML dependency in binding
  - C#3: Removed local ErrorBanner + Connections from EnvironmentPage.qml
  - I#4: Extracted `recover_persisted_processes(ctx)` with `os.kill(pid, 0)` liveness probe
  - I#5: Regenerated .ts files via `pyside6-lupdate`, 49 entries
- Test count after fixes: 157 + 2 skipped (4 new regression tests)
- Status: READY FOR TAG v0.1.0

## ✅ M1 完成 2026-06-26
- 26/26 tasks 完成
- HEAD: 44656f5
- Test count: 157 passed + 2 skipped
- 全部 3 critical + 2 important findings 已修复
- Branch: main (单分支开发，无 feature branch)
- 下一步: tag v0.1.0

