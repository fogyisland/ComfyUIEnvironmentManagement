# ComfyUI Manager - M1 GUI + 环境管理 设计文档

**日期：** 2026-06-24
**状态：** 待用户确认 v1
**作者：** Claude (brainstorming session)
**基于：** master spec `2026-06-21-comfyui-manager-design.md` 第 9.3 节（M1 范围）

---

## 1. 背景与目标

M0 CLI 内核已完成（104 passed + 2 skipped）。M1 把 CLI 包装成 PySide6 + QML 桌面应用，**让用户能用 GUI 做 CLI 能做的所有事**（环境 CRUD、启动/停止、catalog 管理、设置），并加 i18n 和主题切换。

**用户决策（M1 brainstorming 2026-06-24，已通过 AskUserQuestion 确认）：**
- 范围：master spec 9.3 全做（8 大块）
- Qt：PySide6 / Qt 6
- 窗口：NavigationDrawer + Stack 页面
- 桥接：Service + Bridge 1:1
- 进程：QProcess 实时信号（重写 M0 的 ProcessService）
- 主题：Material You 紫（#6750A4）+ 浅/深切换
- 测试：pytest-qt 测 Bridge，QML 不单测
- 代码组织：补 `app/`，保留 `src/comfy_mgr/`
- 打包：zip 绿色版（M1 不做 PyInstaller/Nuitka，跟 Python portable zip 策略一致）

**M1 不做（spec 9.3 明确）：**
- 冲突检测（StaticAnalyzer / ConflictService）— M2
- 节点勾选/启用的 UI — M2
- 节点详情面板 — M2
- 远程管理 — M3
- 打包脚本（PyInstaller/Nuitka）— 改为 zip 绿色版

---

## 2. 架构与分层

```
┌─────────────────────────────────────────────────────────┐
│ QML 层 (app/qml/*.qml)                                    │
│   - 纯声明式 UI，无业务逻辑                                  │
│   - 通过 ContextProperty 拿到 Bridge 实例                   │
│   - 用 Signal 接收状态、Property 读、Invokable 调          │
└──────────────────────┬──────────────────────────────────┘
                       │ Qt signal/slot
┌──────────────────────▼──────────────────────────────────┐
│ Bridge 层 (app/bridge/*.py)                                │
│   - 每个 Service 一个 Bridge QObject                        │
│   - 持有 Service 引用，转换 Result → Signal + dict           │
│   - Q_PROPERTY 暴露状态、Q_INVOKABLE 暴露动作                │
│   - 单测用 pytest-qt                                        │
└──────────────────────┬──────────────────────────────────┘
                       │ 直接调用
┌──────────────────────▼──────────────────────────────────┐
│ Service 层 (src/comfy_mgr/services/*.py) 【M0 已完成】     │
│   - 接口不变，返回 Result[T]                                │
└──────────────────────┬──────────────────────────────────┘
                       │
┌──────────────────────▼──────────────────────────────────┐
│ Infra 层 (src/comfy_mgr/infra/*.py) 【M0 已完成】         │
│   - ProcessService M1 切到 QProcess                        │
│   - 其他不动                                              │
└─────────────────────────────────────────────────────────┘
```

**关键约束：**
- M0 的 Service 接口 100% 不变（`Result[T]` 不动）
- Bridge 是新加的薄层，每个对应一个 Service
- QML 不直接拿 Service，只拿 Bridge

---

## 3. App 目录结构

```
D:\ToolDevelop\ComfyUI\
├─ src\comfy_mgr\              【M0 已有，保留】
│  ├─ cli.py                   【M1 保留，GUI 启动可调】
│  ├─ settings.py
│  ├─ paths.py
│  ├─ result.py
│  ├─ db\
│  ├─ infra\                   【M1: process.py 切 QProcess】
│  ├─ models\
│  └─ services\
│
├─ app\                        【M1 新增】
│  ├─ main.py                  # PySide6 入口（QApplication + QQmlApplicationEngine）
│  ├─ bridge\                  # Bridge QObject
│  │  ├─ __init__.py
│  │  ├─ environment_bridge.py
│  │  ├─ catalog_bridge.py
│  │  ├─ node_bridge.py
│  │  ├─ process_bridge.py
│  │  ├─ settings_bridge.py
│  │  └─ torch_bridge.py
│  ├─ qml\
│  │  ├─ Main.qml
│  │  ├─ Theme.qml (单例)
│  │  ├─ components\
│  │  │  ├─ StatusIndicator.qml
│  │  │  ├─ LogViewer.qml
│  │  │  ├─ FormField.qml
│  │  │  ├─ ConfirmDialog.qml
│  │  │  ├─ ErrorBanner.qml
│  │  │  └─ PathField.qml
│  │  ├─ pages\
│  │  │  ├─ EnvironmentPage.qml
│  │  │  ├─ EnvironmentDetailPanel.qml
│  │  │  ├─ CreateEnvDialog.qml
│  │  │  ├─ CatalogPage.qml
│  │  │  └─ SettingsPage.qml
│  │  └─ i18n\
│  │     ├─ comfyui_manager_zh_CN.ts
│  │     └─ comfyui_manager_en_US.ts
│  └─ resources\
│     └─ icons\
│
├─ tests\                      【M0 + M1 新增】
│  ├─ ... (M0 已有)
│  ├─ infra\                   【M1: test_process.py 重写】
│  └─ bridge\                  【M1 新增】
│     ├─ test_environment_bridge.py
│     ├─ test_process_bridge.py
│     └─ ...
│
├─ scripts\                    【M1 新增】
│  ├─ update_translations.bat
│  └─ build_zip.py             # 打 zip 绿色版
│
├─ pyproject.toml              【M1 加 PySide6 依赖 + entry_points】
├─ start.bat                   【M1 新增：用户启动入口】
└─ docs\superpowers\specs\2026-06-24-m1-gui-design.md  (本文件)
```

**pyproject.toml 关键改动：**
```toml
[project]
dependencies = [
    "typer>=0.12.0",
    "pyyaml>=6.0",
    "pyside6>=6.7.0",       # M1 新增
]

[project.scripts]
comfy-mgr = "comfy_mgr.cli:app"           # M0 CLI
comfy-mgr-gui = "app.main:main"            # M1 GUI

[tool.poetry.packages]
include = [
    "comfy_mgr",           # M0 已有
    "app",                 # M1 新增
]
```

---

## 4. QML 结构

```
Main.qml (ApplicationWindow + NavigationDrawer)
│
├── Header (顶部栏)
│   ├── AppTitle ("ComfyUI Manager")
│   └── LanguageSwitch (zh_CN / en_US)
│
├── NavigationDrawer (Material 标准抽屉)
│   ├── DrawerAction: "环境管理" → page: "environments"
│   ├── DrawerAction: "节点目录" → page: "catalog"
│   └── DrawerAction: "设置" → page: "settings"
│
└── StackLayout (主内容，page 切换)
    │
    ├── EnvironmentPage.qml
    │   ├── SplitView (横向)
    │   │   ├── ListView (左 30%) — 环境列表
    │   │   │   └── delegate: EnvironmentRow
    │   │   └── StackLayout (右 70%) — 详情
    │   │       ├── EmptyState (未选)
    │   │       └── EnvironmentDetailPanel.qml
    │   │           ├── EnvInfoCard
    │   │           ├── ActionRow (启动/停止/删除)
    │   │           └── LogSection
    │   │               ├── StatusIndicator.qml
    │   │               └── LogViewer.qml
    │   └── FAB ("+ 新建环境") → CreateEnvDialog.qml
    │
    ├── CatalogPage.qml
    │   ├── HeaderBar (+ 添加按钮 + 搜索框)
    │   └── ListView (只读 + 添加/删除)
    │
    └── SettingsPage.qml
        └── FormField 列表
            ├── 数据库路径 (含"迁移"按钮)
            ├── 主题 (material_purple/material_blue)
            ├── 主题模式 (浅色/深色/跟随系统)
            ├── 语言 (zh_CN/en_US)
            ├── 默认 Python 路径
            └── 日志级别
```

**导航规则：**
- 启动默认 page = "environments"，未选环境时右侧显示 EmptyState
- NavigationDrawer 选中项高亮 + Material ripple
- 切换 page 时保留各 page 状态

**绑定策略：**
- Page 通过 `require()` 拿到对应 Bridge 实例
- ListView 用 `model: bridge.envList`
- 动作按钮通过 `bridge.createEnv(...)` 调 invokable
- 异步结果通过 Signal 通知

---

## 5. Bridge 接口契约

**模式：** 每个 Service 一个 Bridge QObject，3 类暴露方式（Q_PROPERTY / Q_INVOKABLE / Signal）

### EnvironmentBridge
- **Property:** `envList: list[dict]`
- **Invokable:** `createEnv(name, layout, python, comfyuiSource, port=8188) → dict`
- **Invokable:** `deleteEnv(name, force=False) → dict`
- **Invokable:** `cloneEnv(srcName, newName) → dict`
- **Invokable:** `listEnvs() → list[dict]`
- **Signal:** `envCreated(envId)`, `envDeleted(envId)`, `errorOccurred(code, message)`

### ProcessBridge
- **Property:** `runningEnvs: list[str]`, `logLines: list[str]`（最近 N 行）
- **Invokable:** `startEnv(name) → dict`, `stopEnv(name, timeout=10) → dict`, `getStatus(name) → dict`
- **Signal:** `processStarted(envId, pid, port)`, `processStopped(envId)`, `processLogLine(envId, line)`, `errorOccurred(code, message)`

### CatalogBridge
- **Property:** `nodeList: list[dict]`
- **Invokable:** `addNode(url) → dict`, `removeNode(nodeId) → dict`, `listNodes() → list[dict]`
- **Signal:** `nodeAdded(nodeId)`, `nodeRemoved(nodeId)`, `errorOccurred(code, message)`

### SettingsBridge
- **Property:** `current: dict` (所有设置项)
- **Invokable:** `setValue(key, value) → dict`, `migrateDbPath(newPath) → dict`, `reload() → dict`
- **Signal:** `settingsChanged(key)`, `themeModeChanged(mode)`, `errorOccurred(code, message)`

### TorchBridge
- **Property:** `currentGpuInfo: dict`, `suggestedCuVersions: list[str]`
- **Invokable:** `detectCuda() → dict`, `initEnvTorch(envName, cu=None) → dict`
- **Signal:** `cudaDetected(infoDict)`, `torchConfigWritten(envId)`, `errorOccurred(code, message)`

### 全局错误总线
- 所有 Bridge 共享 `errorOccurred(code, message)` Signal
- QML 端订阅一次，ErrorBanner.qml 统一弹
- 错误消息走 `qsTr()` 自动 i18n（用 code 作为 i18n key）

### Bridge 内部统一模式
```python
def _invoke(self, fn, *args) -> dict:
    """统一封装：调 Service，Result → dict，错误发 errorOccurred 信号"""
    result = fn(*args)
    if result.ok:
        return {"ok": True, "value": _to_qml(result.value)}
    msg = self.tr(result.error.message)  # tr() 让 Qt 翻译系统能抓
    self.errorOccurred.emit(result.error.code, msg)
    return {"ok": False, "error": {"code": result.error.code, "message": msg}}
```

### 数据转换
- `Environment` dataclass → `dict`（QML 友好 camelCase key）
- `_env_to_dict(env) -> dict` 统一函数

---

## 6. 进程管理（ProcessService 切 QProcess）

### M0 问题 → M1 解决方案

| M0 问题 | M1 解决方案 |
|---------|------------|
| Windows 缺 `CREATE_NO_WINDOW` 旗标 | QProcess 默认不开 console |
| `log_fh` 未 close | QProcess 自己管理 fd |
| `extra_model_paths_yaml` 未透传 | `QProcess.start()` 加 `--extra-model-paths-config` |
| `stop()` 不 verify 进程真退 | `waitForFinished(timeout)`，超时返回 PROCESS_STOP_TIMEOUT |

### 新架构
```python
class QProcessBackend:
    """私有，单进程实例。包装 QProcess，信号驱动。"""
    line_received = Signal(str, str)  # env_id, line

    def start(self, env: Environment) -> Result[ProcessHandle]: ...
    def stop(self, timeout: float) -> Result[None]: ...

class ProcessService:
    """公共接口，与 M0 一致。"""
    def __init__(self, conn, log_dir, bridge_sink):
        self._backends: dict[str, QProcessBackend] = {}
        self._bridge_sink = bridge_sink  # 推 Signal 到 ProcessBridge
```

### 日志双轨（实时 + 落盘）
- QProcess 信号 → 实时推给 Bridge → QML LogViewer 追加
- 同时每行 append 到 `<env_root>/logs/<env_id>.log`
- UI 实时看 + 重启 GUI 后能查历史

### 持久化进程状态
- M0 `_procs` 内存 dict → SQLite `process_state` 表
- 字段：`env_id PK, pid INTEGER, port INTEGER, started_at TIMESTAMP`
- GUI 重启后能列出哪些 env 是 running 的

### 新错误码（M1）
- `PROCESS_STOP_TIMEOUT` — stop 超时
- `PROCESS_NOT_RUNNING` — stop 一个没启动的 env
- `PROCESS_ALREADY_RUNNING` (M0) / `PROCESS_START_FAILED` (M0) 保留

### Schema v2 migration（M1 加表）
```sql
CREATE TABLE IF NOT EXISTS process_state (
    env_id TEXT PRIMARY KEY,
    pid INTEGER NOT NULL,
    port INTEGER NOT NULL,
    started_at TIMESTAMP NOT NULL,
    FOREIGN KEY (env_id) REFERENCES environments(id) ON DELETE CASCADE
);
```

---

## 7. i18n + 设置页

### i18n 工作流
```
QML/Python 源码 (qsTr() / self.tr())
        │
        ▼
pyside6-lupdate → .ts 源文件
        │
        ▼
Qt Linguist / AI 翻译
        │
        ▼
pyside6-lrelease → .qm 二进制
        │
        ▼
QTranslator 加载 → qsTr() 自动查 .qm
```

**QML 字符串：** 全部 `qsTr("...")`
**Python 字符串：** `QObject` 子类内 `self.tr("...")`
**翻译文件：** `app/qml/i18n/comfyui_manager_zh_CN.ts` + `_en_US.ts`
**运行时切换：** LanguageSwitch 按钮 → settings.language → SettingsBridge signal → main.py 重载 QTranslator + `engine.retranslate()`

### 设置字段（SettingsPage.qml）
| Key | 类型 | 默认 | UI 控件 |
|-----|------|------|---------|
| `catalog_db_path` | str | None (= appdata) | PathField + 浏览 + "迁移"按钮 |
| `theme` | str | "material_purple" | 下拉（主色调） |
| `theme_mode` | str | "system" | 下拉（light/dark/system） |
| `language` | str | "zh_CN" | 下拉（zh_CN/en_US） |
| `log_level` | str | "INFO" | 下拉（DEBUG/INFO/WARNING/ERROR） |
| `default_python_path` | str | None | PathField + 浏览 |

**交互：**
- 字段 onChange → `bridge.setValue(key, value)` → 写 SQLite + 发 signal
- 主题模式切换：signal → Theme.qml `mode` property 更新 → UI 即时切换
- 语言切换：signal → main.py 重新加载 QTranslator

---

## 8. 主题（Material You 紫 + 浅/深切换）

### 双调色板

**Light：**
```
primary:          #6750A4
onPrimary:        #FFFFFF
primaryContainer: #EADDFF
onPrimaryContainer:#21005D
secondary:        #625B71
background:       #FFFBFE
onBackground:     #1C1B1F
surface:          #FFFBFE
surfaceVariant:   #E7E0EC
onSurface:        #1C1B1F
error:            #B3261E
outline:          #79747E
```

**Dark：**
```
primary:          #D0BCFF
onPrimary:        #381E72
primaryContainer: #4F378B
onPrimaryContainer:#EADDFF
secondary:        #CCC2DC
background:       #1C1B1F
onBackground:     #E6E1E5
surface:          #1C1B1F
surfaceVariant:   #49454F
onSurface:        #E6E1E5
error:            #F2B8B5
outline:          #938F99
```

### Theme.qml 单例
```qml
pragma Singleton
QtObject {
    readonly property var light: ({...})
    readonly property var dark: ({...})
    property string mode: "light"  // 由 SettingsBridge 注入
    function color(name) {
        return (mode === "dark" ? dark : light)[name]
    }
    readonly property int spacing: 8
    readonly property int spacingLarge: 16
    readonly property int radius: 12
}
```

### 主题模式取值
- `light`：强制浅色
- `dark`：强制深色
- `system`：跟随 Windows 系统（`Qt.application.styleHints.colorScheme`）

### 字体
- Windows: "Microsoft YaHei UI" (中) / "Segoe UI" (英)
- 跟随系统 locale

---

## 9. 测试策略

### 分层

```
手动 GUI 冒烟（用户机器，5 条主流程）
        ▲
pytest-qt 测 Bridge（mock Service，验证 Signal/Property）
        ▲
pytest 测 Service/Infra（M0 风格，ProcessService 重写）
```

### pytest-qt 关键 pattern
```python
def test_env_bridge_emits_env_created(qtbot, mocker):
    mock_service = mocker.MagicMock()
    mock_service.create.return_value = Result.ok(_fake_env("e1"))
    bridge = EnvironmentBridge(mock_service)
    with qtbot.waitSignal(bridge.envCreated, timeout=1000) as blocker:
        result = bridge.createEnv("e1", "shared", "C:/python.exe", "D:/ComfyUI")
    assert result["ok"]
    assert blocker.args == ["e1"]
```

### QML 不单测
- QML 视为粘合层，code review + 手动冒烟
- 重要 UI 逻辑写到 Bridge 的 Q_PROPERTY，QML 端不写逻辑

### conftest.py 加 QApplication fixture
```python
@pytest.fixture(scope="session")
def qapp():
    app = QApplication.instance() or QApplication(sys.argv)
    yield app
```

### 目标
- M0：104 passed + 2 skipped
- M1：~140+ passed（+30+ bridge tests）+ 2 skipped
- 新增 `tests/bridge/`、`tests/infra/test_process.py` 重写

---

## 10. zip 绿色版发布

### 目录结构
```
comfyui-manager-v0.1.0-win64.zip
├─ app\...                     # GUI 代码
├─ src\comfy_mgr\...           # 服务层
├─ catalog\                    # 空目录
├─ logs\                       # 空目录
├─ start.bat                   # 启动脚本
├─ pyproject.toml
├─ poetry.lock
├─ README.md
└─ LICENSE
```

### start.bat
```bat
@echo off
chcp 65001 > nul
where python > nul 2>&1 || (
    echo [ERROR] Python not in PATH
    pause & exit /b 1
)
python -c "import PySide6" 2>nul || poetry install
poetry run comfy-mgr-gui
```

### scripts/build_zip.py
用户手动跑 `python scripts/build_zip.py 0.1.0` 出 zip。排除 `.git`、`__pycache__`、`tests`、`docs`、`.superpowers`、`dist`。

### 卸载
直接删除整个目录，无注册表残留。

---

## 11. 错误处理

### 错误码分类（M0 + M1 合并）

| 模块 | 错误码 |
|------|--------|
| 环境 | `ENV_NOT_FOUND`, `ENV_RUNNING`, `ENV_NAME_DUPLICATE`, `ENV_PATH_NOT_EMPTY`, `ENV_SAVE_FAILED`, `COMFYUI_SOURCE_MISSING` |
| 节点 | `NODE_NOT_FOUND`, `NODE_ALREADY_EXISTS`, `NODE_SAVE_FAILED` |
| 进程 | `PROCESS_ALREADY_RUNNING`, `PROCESS_START_FAILED`, `PROCESS_STOP_FAILED`, `PROCESS_STOP_TIMEOUT` (M1), `PROCESS_NOT_RUNNING` (M1), `PROCESS_LOG_FAILED` |
| Venv | `VENV_PYTHON_MISSING`, `VENV_CREATE_FAILED`, `VENV_PIP_FAILED`, `VENV_VERSION_FAILED` |
| Git | `GIT_CLONE_FAILED`, `GIT_PULL_FAILED` |
| FS | `FS_JUNCTION_FAILED`, `FS_PLATFORM_UNSUPPORTED`, `FS_COPY_FAILED`, `FS_MKDIR_FAILED` |
| CUDA | `CUDA_DETECT_FAILED` |
| Torch | `PYTORCH_INSTALL_FAILED` |

### Bridge 错误流
1. Service 返回 `Result.fail(ServiceError(code, message))`
2. Bridge `_invoke` 包装：
   - 翻译 message（`self.tr(msg)`）
   - 发 `errorOccurred(code, msg)` signal
   - 返回 `{"ok": False, "error": {"code": code, "message": msg}}`
3. QML 端：ErrorBanner.qml 订阅全局 errorOccurred，弹出

### 错误消息 i18n
- `code` 作为 i18n key，前端查表显示本地化文案
- `message` 是 fallback（已用 `tr()` 包装）

---

## 12. 验收清单

### 功能性
- [ ] 启动 `comfy-mgr-gui` 能看到 NavigationDrawer + 环境管理页
- [ ] 能在 GUI 创建环境（弹对话框 → 填表单 → 创建）
- [ ] 能启动/停止环境，状态实时更新
- [ ] 进程日志实时显示（QProcess 信号驱动）
- [ ] 能删除/克隆环境
- [ ] 能添加/删除 catalog 节点
- [ ] 设置页能切换主题（浅/深/跟随系统），UI 即时变化
- [ ] 设置页能切换语言（中/英），UI 即时翻译
- [ ] 设置页能迁移 DB 路径，自动重启服务

### 工程性
- [ ] `poetry run pytest` ≥ 140 passed + 2 skipped
- [ ] pytest-qt 跑 Bridge 测试，CI 可用
- [ ] `python scripts/build_zip.py 0.1.0` 能出 zip
- [ ] 解压 zip + 双击 start.bat 能启动 GUI（手动验证）
- [ ] i18n：qsTr() 覆盖率 100%（新写的 QML/Python 字符串）
- [ ] 错误码 → UI 提示：所有 26 个错误码有本地化文案

### M0 回归
- [ ] M0 CLI 仍可用：`poetry run comfy-mgr` 走 CLI
- [ ] 104 个 M0 测试全过
- [ ] schema v2 migration 不破坏现有 v1 数据

---

## 13. 关键决策表

| 决策 | 选择 | 备选 | 理由 |
|------|------|------|------|
| Qt 版本 | PySide6 / Qt 6 | PySide2 / Qt 5 | 现代 API、Material 原生、Windows 11 适配 |
| 窗口布局 | NavigationDrawer + Stack | Sidebar+Tabs / Hamburger | Material 标准、桌面/触控都能用 |
| 桥接 | Service + Bridge 1:1 | 直接暴露 Service / BridgeManager | 清晰边界、可单测 |
| 进程日志 | QProcess 实时信号 | 文件 + 定时 tail | 实时无延迟、Qt 原生 |
| 主题切换 | 浅/深/系统三档 | 只有 light | 用户要求 |
| 打包 | zip 绿色版 | PyInstaller/Nuitka .exe | 解压即用、无需安装、跟 portable Python 策略一致 |
| 测试 | pytest-qt 测 Bridge | QtQuickTest 测 QML | pytest 生态、CI 友好 |
| 代码组织 | 补 app/，保留 comfy_mgr/ | 全部迁 app/ | 不重构 M0、Service 复用最大 |

---

## 14. 风险表

| 风险 | 概率 | 影响 | 缓解 |
|------|------|------|------|
| pytest-qt 在 Windows CI 上 QApplication 启动失败 | 中 | 中 | headless mode + offscreen platform plugin |
| QProcess 在 Windows 上控制台窗口闪烁 | 低 | 低 | Material flags 默认不开 console |
| zip 绿色版体积过大（PySide6 依赖） | 中 | 低 | README 注明首次启动需 `poetry install`（1-3 分钟） |
| i18n 切换后部分字符串未翻译 | 中 | 中 | `update_translations.bat` CI 检查 100% 覆盖 |
| ProcessService M1 重写引入回归 | 中 | 高 | M0 测试 + Bridge 测试双重覆盖 |
| schema v2 迁移破坏现有 DB | 低 | 高 | 用 `CREATE TABLE IF NOT EXISTS` 幂等迁移 |
| 浅/深切换时部分自定义组件不响应 | 中 | 中 | Theme.qml 单例统一管理，避免硬编码颜色 |

---

## 15. 术语表

- **Bridge**：PySide6 QObject 子类，包装 Service 供 QML 调用
- **Service**：业务逻辑层（src/comfy_mgr/services/），返回 `Result[T]`
- **QProcess**：Qt 进程管理类，替代 subprocess（信号驱动）
- **NavigationDrawer**：Material Design 侧边抽屉导航
- **qsTr()**：QML 的 i18n 字符串标记函数
- **tr()**：Python QObject 的 i18n 字符串标记方法
- **lupdate / lrelease**：Qt Linguist 工具链，提取/编译翻译文件
- **Material You**：Google 2021+ 设计语言，M3 调色板规范
- **zip 绿色版**：解压即用、无需安装器的发布形式（portable distribution）

---

## 16. 后续里程碑衔接

- **M2**：加 StaticAnalyzer + ConflictService + 节点勾选 UI + 冲突详情面板
  - Bridge 新增 `ConflictBridge`
  - EnvironmentDetailPanel 加"节点"标签页
- **M3**：远程管理 + WebCommunitySource + 冲突图可视化
- **M2 前**：若 zip 绿色版体积成问题，重新评估 PyInstaller 单文件方案

---

## 17. M1 计划规模预估

预计 M1 plan 包含 **25-32 个 task**：
- 基础设施（4-5）：PySide6 依赖、main.py、QQmlEngine、Theme.qml、qmldir
- Bridge 层（6-7）：5 个 Service 各 1 个 Bridge + QApplication fixture + 错误总线
- QML 组件（6-8）：Main.qml + 5 个 components + Theme
- QML 页面（5-6）：3 个 pages + EnvironmentDetailPanel + CreateEnvDialog
- 进程管理（3-4）：QProcessBackend + ProcessService 重写 + schema v2 migration + 测试重写
- i18n（2-3）：.ts 提取 + 翻译 + 编译脚本
- 设置 + 主题（2-3）：SettingsPage UI + theme_mode 切换逻辑
- 打包 + 验收（2-3）：build_zip.py + start.bat + 手动冒烟 checklist