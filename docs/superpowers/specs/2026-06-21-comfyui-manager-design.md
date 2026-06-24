# ComfyUI Manager 设计文档

**日期：** 2026-06-21
**状态：** 已确认 v1
**作者：** Claude (brainstorming session)

---

## 1. 项目背景与目标

### 1.1 问题

ComfyUI 实例众多，节点数量大。当前痛点：

- 多个 ComfyUI 实例的 venv 相互独立，但模型可以共用
- 节点之间的依赖、冲突缺乏系统化管理
- 创建新环境时不知道装哪些节点、装上是否会有冲突
- 缺乏一个统一管理工具

### 1.2 目标

构建一套 ComfyUI 管理桌面应用，提供：

1. **多环境管理**：创建/删除/克隆 ComfyUI 环境，环境间 venv 独立、节点可独立选择
2. **节点冲突分析**：在启用节点前检测潜在冲突，避免运行时崩溃
3. **模型共享**：通过 `extra_model_paths.yaml` 让所有环境共享模型目录
4. **节点集中管理**：全局节点仓库（catalog）+ 各环境按需启用
5. **现代化 UI**：PySide6 + QML，操作简洁、视觉友好；Material You 紫色主题；M1 同步上 i18n 框架（中/英双语）

### 1.3 非目标（本期指 M0+M1+M2 整个开发周期）

- 不实现节点代码沙箱执行（仍是静态分析）
- 不实现远程管理（M3+）
- 不对接社区网站（M3+）
- 不实现节点版本自动升降级

---

## 2. 架构总览

### 2.1 系统分层

```
┌─────────────────────────────────────────────────────────┐
│  UI 层 (QML)                                              │
│  - EnvironmentPanel   NodeSelector   ConflictGraph       │
│  - ProcessManager     LogViewer      SettingsPage        │
└─────────────────────┬───────────────────────────────────┘
                      │ QML 信号槽 + Python 后端
┌─────────────────────▼───────────────────────────────────┐
│  业务服务层 (Python)                                       │
│  EnvironmentService   NodeService   ConflictService      │
│  ProcessService       CatalogService                    │
└─────────────────────┬───────────────────────────────────┘
                      │
┌─────────────────────▼───────────────────────────────────┐
│  基础设施层 (Python)                                       │
│  GitManager  VenvManager  StaticAnalyzer                │
│  FileWatcher ConflictSource(本地) → ConflictSource(Web) │
└─────────────────────────────────────────────────────────┘
```

### 2.2 目录结构

```
D:\ComfyUI-Manager\
├─ app\
│  ├─ main.py                    # PySide6 入口
│  ├─ ui\                        # QML 文件 + Python QObject 桥接
│  │  ├─ qml\                    # .qml 视图
│  │  └─ bridge\                 # 与 QML 通信的 Python 类
│  ├─ services\                  # 业务服务
│  │  ├─ environment.py
│  │  ├─ node.py
│  │  ├─ conflict.py
│  │  ├─ process.py
│  │  └─ catalog.py
│  ├─ infra\                     # 基础设施
│  │  ├─ git.py                  # git clone/pull/worktree 封装
│  │  ├─ venv.py                 # venv 创建/管理
│  │  ├─ analyzer\               # 静态分析
│  │  │  ├─ requirements.py      # requirements.txt 解析
│  │  │  ├─ imports.py           # AST import 扫描
│  │  │  ├─ side_effects.py      # sys.path, os.environ 检测
│  │  │  └─ registry.py          # NODE_CLASS_MAPPINGS 冲突
│  │  ├─ sources\                # 冲突数据源（可扩展）
│  │  │  ├─ base.py              # ConflictSource 接口
│  │  │  ├─ local.py             # 本地分析源（本期）
│  │  │  └─ web.py               # 网站源（未来，stub）
│  │  └─ fs.py                   # 文件系统、junction、软链
│  └─ models\                    # 数据模型（dataclass）
│     ├─ environment.py
│     ├─ node.py
│     ├─ conflict.py
│     └─ catalog.py
├─ catalog\                      # 节点全局仓库（运行时数据）
│  ├─ nodes\                     # 各节点 git 仓库
│  └─ catalog.db                 # SQLite 元数据
├─ shared\                       # 共享模型
│  └─ models\
├─ envs\                         # 各环境
├─ tests\
└─ docs\superpowers\specs\       # 设计/计划文档
```

### 2.3 关键设计决策

| 决策 | 方案 | 理由 |
|-----|-----|-----|
| ComfyUI 源码 | 每个 env 独立目录，ComfyUI 核心用 junction 指向共享源；可切换为完全独立 | 默认共享节省空间和升级成本，提供切换灵活性 |
| 节点分发 | 全局 catalog 目录存 git repos，env 的 custom_nodes 用 junction 链接 | 单次克隆、多环境共享、升级一次生效 |
| venv | 每个 env 独立；Python 解释器路径由用户指定（任意 3.9+） | Python 依赖可能因节点选择不同；不绑定特定 Python 版本以兼容节点多样性 |
| 冲突数据源 | `ConflictSource` 抽象接口，本期仅 LocalSource | 为未来 WebSource 留扩展点，本期不实现 |
| 进程管理 | QProcess 包装 ComfyUI 主进程，记录 PID、端口、日志 | 8+ 环境需要稳定启停 |
| 存储 | SQLite (catalog.db) 存节点元数据 + 冲突缓存；路径**可配置**，默认 `appdata` | 轻量、易查询；用户可改到自定义路径（迁移时迁移文件） |
| 国际化 | M1 引入 Qt Linguist (tr()) + .ts/.qm，中/英双语齐上 | 避免 M3 重构代价大；本期 spec 阶段即明确 |
| 主题 | Qt Quick Controls Material 主题，默认紫色 (#6750A4) | 现代、年轻化、跟系统色兼容；预留主题切换扩展点 |
| 打包 | 同时提供 PyInstaller 与 Nuitka 两种打包脚本 | 用户自选；PyInstaller 成熟稳定，Nuitka 启动快体积小 |

### 2.4 环境与 catalog 布局

```
D:\ComfyUI-Manager\                       ← 项目根（用户可改）
├─ catalog\                               ← 节点仓库（git clones，**始终在项目根**）
│  └─ nodes\
│     ├─ ComfyUI-Impact-Pack\
│     ├─ ComfyUI-Inspire-Pack\
│     └─ ...

%APPDATA%\ComfyUI-Manager\                ← 跨项目根的元数据（**默认位置，可配置**）
├─ catalog.db                             ← 节点元数据、冲突图谱
├─ settings.json                          ← 用户偏好（db 路径、主题、i18n 等）
└─ logs\
   └─ app-YYYY-MM-DD.log

（用户可设置把 catalog.db 移回项目根 catalog\ 下；切换时迁移数据）
│
├─ shared\                      ← 大文件共享
│  ├─ ComfyUI\                  ← ComfyUI 源码（共享，junction 指向这里）
│  └─ models\                   ← 模型根目录（所有 env 共享）
│
└─ envs\                        ← 环境目录（每环境独立）
   ├─ env-sdxl-turbo\
   │  ├─ ComfyUI\               ← junction → ../../shared/ComfyUI（默认）
   │  ├─ venv\                  ← 独立 Python 环境（差异点 ①）
   │  ├─ custom_nodes\          ← 节点软链接集合（差异点 ②）
   │  │  ├─ ComfyUI-Impact-Pack → ../../catalog/nodes/ComfyUI-Impact-Pack
   │  │  └─ ComfyUI-Inspire-Pack → ../../catalog/nodes/ComfyUI-Inspire-Pack
   │  ├─ extra_model_paths.yaml → 指向 ../../shared/models
   │  └─ manager.json           ← 本环境节点清单 + 端口等配置
   ├─ env-flux\
   └─ env-anime\
```

---

## 3. 核心模块和接口

### 3.1 EnvironmentService（环境生命周期）

**职责：** 创建、删除、克隆环境；切换共享/独立布局；维护环境元数据

```python
class EnvironmentService:
    def create(
        name: str,
        layout: Literal["shared","independent"],
        python_path: Path,                # 用户指定的 Python 解释器（任意 3.9+）
        comfyui_source: Path | None = None # shared 布局时必填；independent 时可空（后续 git clone）
    ) -> Result[Environment]
    def delete(env_id: str, force: bool = False) -> Result[None]
    def clone(src_env_id: str, new_name: str) -> Result[Environment]
    def switch_layout(env_id: str, layout: Literal["shared","independent"]) -> Result[None]
    def list_all() -> list[Environment]
    def get(env_id: str) -> Environment | None
    def update_config(env_id: str, config: EnvironmentConfig) -> Result[None]
```

**Environment 数据模型：**

```python
@dataclass
class Environment:
    id: str
    name: str
    root_path: Path              # envs/env-sdxl/
    comfyui_layout: str         # "shared" | "independent"
    comfyui_source: Path        # shared 时指向共享源；independent 时同 root
    venv_path: Path
    python_executable: Path     # venv 内的 python.exe（用户创建时指定的解释器所创）
    custom_nodes_path: Path
    extra_model_paths_yaml: Path
    port: int                   # 启动端口
    enabled_node_ids: list[str] # 勾选的节点 ID
    status: Literal["stopped","running","error"]
    pid: int | None
```

### 3.2 NodeService + CatalogService（节点仓库）

**职责：** 从 GitHub 克隆节点到 catalog；维护节点元数据库；提供节点列表供 UI 勾选

```python
class CatalogService:
    def add_node(repo_url: str) -> Result[Node]            # 克隆到 catalog/nodes/
    def remove_node(node_id: str) -> Result[None]
    def update_node(node_id: str) -> Result[None]          # git pull
    def list_nodes() -> list[Node]
    def get_node(node_id: str) -> Node | None
    def search(query: str) -> list[Node]

class NodeService:
    def enable_in_env(env_id: str, node_id: str) -> Result[None]   # 建 junction
    def disable_in_env(env_id: str, node_id: str) -> Result[None]  # 删 junction
    def list_enabled(env_id: str) -> list[Node]
    def list_available(env_id: str) -> list[Node]                  # catalog 中所有
```

**Node 数据模型：**

```python
@dataclass
class Node:
    id: str                   # 由 repo URL 派生的稳定 ID
    name: str                 # 节点显示名（来自 repo 目录名）
    repo_url: str
    local_path: Path          # catalog/nodes/<name>/
    current_version: str     # git describe 或 latest tag
    description: str
    author: str
    metadata: NodeMetadata    # requirements/imports/side_effects 扫描结果
    last_analyzed_at: datetime

@dataclass
class NodeMetadata:
    requirements: list[RequirementSpec]   # 来自 requirements.txt
    python_imports: set[str]              # AST 提取的 import
    side_effects: list[SideEffect]        # sys.path 追加等
    node_class_keys: set[str]             # NODE_CLASS_MAPPINGS 中的 key
    install_script: str | None            # install.py 路径（若存在）
```

### 3.3 ConflictService + ConflictSource（冲突分析）

**职责：** 对一组节点做冲突分析；抽象数据源便于未来对接网站

```python
class ConflictService:
    def __init__(self, source: ConflictSource)
    def analyze(env_id: str, candidate_node_ids: list[str]) -> ConflictReport
    def refresh_cache() -> None                    # 重新扫描 catalog
    def get_cached_conflicts(node_ids: list[str]) -> list[Conflict]

# 数据源接口（关键扩展点）
class ConflictSource(ABC):
    def get_conflicts(self, nodes: list[Node]) -> list[Conflict]: ...

class LocalStaticSource(ConflictSource):     # 本期实现
    """基于 requirements.txt + AST + 注册表键分析"""

class WebCommunitySource(ConflictSource):    # 未来 stub
    """从社区网站拉取已知冲突（本期仅定义接口，不实现）"""
```

**Conflict 数据模型：**

```python
@dataclass
class Conflict:
    type: ConflictType           # PYTHON_DEP | PYTHON_IMPORT | SIDE_EFFECT |
                                 # REGISTRY_COLLISION | KNOWN_INCOMPAT
    severity: Literal["error","warning","info"]
    node_ids: list[str]          # 涉及的节点
    description: str             # 人类可读说明
    detail: dict                 # 结构化细节（版本号、import 名等）
    source: Literal["local","web"]
```

### 3.4 ProcessService（进程管理）

**职责：** 启动/停止 ComfyUI 主进程；端口分配；日志收集

```python
class ProcessService:
    def start(env_id: str) -> Result[ProcessHandle]
    def stop(env_id: str, timeout: float = 10.0) -> Result[None]
    def restart(env_id: str) -> Result[ProcessHandle]
    def get_status(env_id: str) -> ProcessStatus
    def list_running() -> list[ProcessStatus]
    def get_logs(env_id: str, tail: int = 200) -> str
    def follow_logs(env_id: str) -> Iterator[str]   # 异步流

@dataclass
class ProcessHandle:
    env_id: str
    pid: int
    port: int
    started_at: datetime
    log_file: Path
```

**端口分配策略：**

- 默认起始 8188，每个环境递增 1；允许用户手动指定
- 启动前检查端口占用，若冲突提示用户改

### 3.5 StaticAnalyzer（本地静态分析器）

按子模块组织，每子模块独立可测：

```python
class RequirementsAnalyzer:
    """解析 requirements.txt，提取包名和版本约束"""
    def parse(self, node_path: Path) -> list[RequirementSpec]

class ImportAnalyzer:
    """AST 扫描 .py 文件，提取 import 的模块名"""
    def extract(self, node_path: Path) -> set[str]

class SideEffectAnalyzer:
    """检测 sys.path.append、os.environ 修改、全局注册副作用"""
    def extract(self, node_path: Path) -> list[SideEffect]

class RegistryAnalyzer:
    """解析 NODE_CLASS_MAPPINGS，提取注册的节点类 key"""
    def extract(self, node_path: Path) -> set[str]
```

**SideEffect 类型：**

```python
@dataclass
class SideEffect:
    kind: Literal["sys_path_append","os_environ_set","global_register",
                  "module_patch","import_chain"]
    target: str          # 如 "sys.path" / "os.environ['HF_HOME']"
    source_file: str     # 在哪个文件
    line_no: int
    snippet: str         # 代码片段（截断）
```

### 3.6 持久化（SQLite）

**catalog.db 路径：**

- **默认位置：** `%APPDATA%\ComfyUI-Manager\catalog.db`（Windows 习惯，跨项目根）
- **可配置：** 用户可在 settings.json 中改到任意路径（如项目根 `catalog/catalog.db`）
- **首次启动：** 若 appdata 与项目根都无 catalog.db，弹窗让用户选；选择后写入 settings
- **路径迁移：** 切换路径时复制旧 db 到新位置，校验后再删除旧文件

**settings.json 结构：**

```json
{
  "catalog_db_path": "%APPDATA%/ComfyUI-Manager/catalog.db",
  "theme": "material_purple",
  "language": "zh_CN",
  "log_level": "INFO",
  "default_python_path": "C:/Python310/python.exe"
}
```

**catalog.db 表结构：**

```sql
CREATE TABLE nodes (
    id TEXT PRIMARY KEY,
    name TEXT NOT NULL,
    repo_url TEXT NOT NULL,
    local_path TEXT NOT NULL,
    current_version TEXT,
    description TEXT,
    author TEXT,
    metadata_json TEXT,          -- NodeMetadata 序列化
    last_analyzed_at TIMESTAMP
);

CREATE TABLE environments (
    id TEXT PRIMARY KEY,
    name TEXT NOT NULL UNIQUE,
    root_path TEXT NOT NULL,
    comfyui_layout TEXT NOT NULL,
    comfyui_source TEXT,
    venv_path TEXT,
    python_executable TEXT,             -- 该 env 实际使用的解释器
    custom_nodes_path TEXT,
    extra_model_paths_yaml TEXT,
    port INTEGER,
    enabled_node_ids_json TEXT,
    created_at TIMESTAMP,
    updated_at TIMESTAMP
);

CREATE TABLE conflicts_cache (
    id INTEGER PRIMARY KEY AUTOINCREMENT,
    node_ids_hash TEXT NOT NULL,    -- 节点集 hash
    conflicts_json TEXT NOT NULL,
    computed_at TIMESTAMP
);

CREATE TABLE known_incompat (
    node_id_a TEXT,
    node_id_b TEXT,
    severity TEXT,
    note TEXT,
    PRIMARY KEY (node_id_a, node_id_b)
);
```

### 3.7 UI 桥接（QML ↔ Python）

UI 通过 `Q_PROPERTY` + `Signal` 暴露，QML 绑定：

```python
class EnvironmentListModel(QAbstractListModel):
    """QML 中 ListView 的 model，显示所有环境及其状态"""

class NodeListModel(QAbstractListModel):
    """显示当前环境可选/已选节点，带冲突标记"""

class ConflictGraphModel(QObject):
    """冲突图数据，供 QML Canvas/GraphView 绘制"""
```

---

## 4. 关键流程

### 4.1 创建新环境

```
用户                UI                  EnvironmentService    VenvManager       FS
 │                  │                         │                  │              │
 │ 点击"新建环境"     │                         │                  │              │
 ├─────────────────►│                         │                  │              │
 │                  │ 弹出对话框               │                  │              │
 │                  │   (名称/布局/端口/       │                  │              │
 │                  │    Python解释器路径)     │                  │              │
 │ 填写表单并确认     │                         │                  │              │
 ├─────────────────►│                         │                  │              │
 │                  │ create(name, layout,    │                  │              │
 │                  │   python_path)          │                  │              │
 │                  ├────────────────────────►│                  │              │
 │                  │                         │ 校验 python_path  │              │
 │                  │                         │   版本≥3.9        │              │
 │                  │                         │ 检查名称唯一性       │              │
 │                  │                         │ 分配端口(8188+N)    │              │
 │                  │                         │ mkdir envs/<name>   │              │
 │                  │                         ├─────────────────────────────────►│
 │                  │                         │                  │              │
 │                  │                         │ 用指定 python     │              │
 │                  │                         │ 创建 venv          │              │
 │                  │                         ├─────────────────►│              │
 │                  │                         │                  │ <py> -m venv │
 │                  │                         │                  ├─────────────►│
 │                  │                         │                  │              │
 │                  │                         │ shared: 建 ComfyUI junction     │
 │                  │                         │ indep:  复制 ComfyUI 源码        │
 │                  │                         ├─────────────────────────────────►│
 │                  │                         │                  │              │
 │                  │                         │ 生成 extra_model_paths.yaml     │
 │                  │                         ├─────────────────────────────────►│
 │                  │                         │                  │              │
 │                  │                         │ INSERT environments 表          │
 │                  │                         │ (含 python_executable 字段)      │
 │                  │                         │                  │              │
 │                  │ ◄─────── Environment ───┤                  │              │
 │ ◄── 显示新环境 ──►│                         │                  │              │
```

**关键点：**

- 表单必填项：名称、布局、端口、Python 解释器路径
- 校验：python_path 必须存在且 `python --version` ≥ 3.9
- 校验：shared 布局时 comfyui_source 必须存在
- 创建 venv 异步执行（耗时 5-30s），UI 显示进度条
- 创建后自动触发 `pip install -r <ComfyUI>/requirements.txt`（用 venv 内 python）
- 默认空节点列表，等用户勾选

### 4.2 添加节点到 catalog

```
用户            UI              NodeService      GitManager      StaticAnalyzer    SQLite
 │              │                   │                │                 │              │
 │ "添加节点"    │                   │                │                 │              │
 ├─────────────►│                   │                │                 │              │
 │ 粘贴URL       │                   │                │                 │              │
 ├─────────────►│                   │                │                 │              │
 │              │ add_node(url)    │                │                 │              │
 │              ├──────────────────►│                │                 │              │
 │              │                   │ git clone      │                 │              │
 │              │                   ├───────────────►│                 │              │
 │              │                   │ ◄─── 完成 ──────┤                 │              │
 │              │                   │ 分析元数据      │                 │              │
 │              │                   ├──────────────────────────────────►│              │
 │              │                   │ ◄── NodeMetadata ──────────────────┤              │
 │              │                   │ INSERT nodes                                       │
 │              │                   ├──────────────────────────────────────────────────►│
 │              │ ◄─── Node ───────┤                                                  │
 │ ◄── 列表新增 ─►│                   │                │                 │              │
```

**关键点：**

- 克隆和分析都是后台任务，UI 显示进度
- 已有同 URL 节点时拒绝添加（提示先 update）
- 分析失败的节点仍入库，但 `metadata=null`，UI 标记"无法分析"

### 4.3 在环境中启用节点（含冲突检测）

```
用户              UI               NodeService     ConflictService   FS
 │                │                    │                │              │
 │ 勾选节点X       │                    │                │              │
 ├───────────────►│                    │                │              │
 │                │ enable(env, X)    │                │              │
 │                ├───────────────────►│                │              │
 │                │                    │ 已选节点 ∪ {X} │              │
 │                │                    │ analyze(env, [])│              │
 │                │                    ├───────────────►│              │
 │                │                    │                │              │
 │                │                    │                │ 查询缓存:
 │                │                    │                │   hash(已选∪X)
 │                │                    │                │   命中? 返回
 │                │                    │                │   未中 → 重算
 │                │                    │                │
 │                │                    │                │ LocalSource:
 │                │                    │                │   • requirements 依赖图
 │                │                    │                │   • imports 重名/版本冲突
 │                │                    │                │   • side_effect 互斥
 │                │                    │                │   • registry key 冲突
 │                │                    │                │
 │                │                    │ ◄─ ConflictReport ─┤
 │                │                    │                │
 │                │                    │ conflicts 为空?
 │                │                    │ ───── 是 ─────►│
 │                │                    │ 建 junction     │              │
 │                │                    ├──────────────────────────────►│
 │                │                    │ 更新 env.enabled_node_ids    │
 │                │                    │ 更新冲突缓存                  │
 │                │ ◄── 成功 + 报告 ───┤                │
 │ ◄── 列表更新 ──►│                    │                │
 │
 │ 勾选节点Y（与 X 冲突）             │
 ├───────────────►│                    │                │
 │                │ enable(env, Y)    │                │
 │                ├───────────────────►│                │
 │                │                    │ analyze → 发现冲突 │
 │                │                    │ ──── 是 ───────►│
 │                │                    │ 不写 junction    │
 │                │                    │ UI 标记 ⚠       │
 │                │ ◄── 拒绝 + 报告 ───┤                │
 │ ◄── 显示警告 ──►│                    │                │
```

**关键点：**

- 冲突分析在启用前进行，冲突则阻止启用并展示原因
- 用户可在 UI 上选"强制启用"（写入 ignored_conflicts 表），但本期不实现，留为扩展点
- 冲突结果按 `hash(node_ids)` 缓存到 `conflicts_cache` 表，避免重复计算

### 4.4 启动 ComfyUI 环境

```
用户           UI            ProcessService    VenvManager      FS
 │             │                 │                │              │
 │ 点击"启动"   │                 │                │              │
 ├────────────►│                 │                │              │
 │             │ start(env)     │                │              │
 │             ├───────────────►│                │              │
 │             │                 │ 检查 status=stopped           │
 │             │                 │ 检查端口空闲    │              │
 │             │                 │ 检查 venv 存在  │              │
 │             │                 │ 检查 enabled_node_ids 全部有 junction │
 │             │                 │                │              │
 │             │                 │ QProcess 启动:  │              │
 │             │                 │   <venv>/python.exe            │
 │             │                 │     ComfyUI/main.py            │
 │             │                 │     --port <port>              │
 │             │                 │     --listen 0.0.0.0           │
 │             │                 │     --disable-auto-launch      │
 │             │                 ├───────────────►│              │
 │             │                 │                │              │
 │             │                 │ 监听 stdout/stderr → log file  │
 │             │                 ├──────────────────────────────►│
 │             │                 │                │              │
 │             │                 │ 探测启动成功: 扫日志"Starting server"│
 │             │ ◄── ProcessHandle ─┤              │              │
 │ ◄── 状态:运行 ─►│                │                │              │
```

**关键点：**

- 启动命令使用环境的 venv 解释器，确保依赖隔离
- `extra_model_paths.yaml` 通过环境变量 `COMFYUI_ARGS` 或启动参数传递（按 ComfyUI 版本支持）
- 日志同时写文件（持久化）和管道（实时 UI 显示）
- 启动失败时（10s 内无 "Starting server"）UI 显示错误日志片段

### 4.5 关闭环境

```
ProcessService    QProcess           OS
 │                │                   │
 stop(env, 10s)   │                   │
 ├───────────────►│                   │
 │ terminate()    │                   │
 ├───────────────►│                   │
 │ 等待 5s        │                   │
 │ kill() 若仍在   │                   │
 ├───────────────►│                   │
 │                │ 进程退出          │
 │                ├──────────────────►│
 │ 端口回收       │                   │
 │ 更新 status    │                   │
```

**关键点：**

- 优雅终止 → 强杀 两步
- 端口立即释放给其他环境复用

### 4.6 catalog 刷新（更新节点 + 重扫元数据）

```
触发：用户点击"刷新节点" / 定时任务 / 启动时
NodeService.refresh_catalog()
├─ 遍历 nodes 表
├─ 对每个 node: git pull
├─ 解析失败的节点保留旧 metadata，标记 warning
├─ 重新执行 StaticAnalyzer 全部 4 子分析
├─ UPSERT nodes 表（覆盖 metadata）
├─ 清空 conflicts_cache（节点集未变时缓存仍有效，但简化策略：全清）
└─ 发出 catalogUpdated 信号 → UI 刷新
```

### 4.7 异步与取消约定

| 操作 | 异步 | 可取消 |
|------|------|--------|
| 创建 venv | ✅ | ❌（中断会留半成品） |
| git clone | ✅ | ✅ |
| pip install | ✅ | ✅ |
| 静态分析 | ✅（首次可阻塞）| ✅ |
| 冲突分析 | ❌（毫秒级）| N/A |
| 启动 ComfyUI | ✅（启动探测）| ✅ |
| 关闭 ComfyUI | ✅（带超时）| ✅ |

UI 取消通过 `QFuture.cancel()`（PySide6）实现；后台任务用 `QThreadPool` 调度。

---

## 5. 错误处理与边界情况

### 5.1 错误分类与处理原则

| 错误级别 | 含义 | 处理方式 |
|---------|------|---------|
| **FATAL** | 环境无法继续运行（如磁盘满、数据库损坏） | 弹窗告警，停止当前操作，保留现场日志 |
| **ERROR** | 单次操作失败（如 git clone 失败、端口占用） | UI 弹窗+可重试，不影响其他环境 |
| **WARN** | 操作成功但有副作用（如某个节点元数据扫描失败） | UI 标记黄色感叹号，可后续修复 |
| **INFO** | 正常流程日志 | 仅写日志文件 |

**统一错误处理约定：**

服务层方法不抛 `Exception` 给 UI，包装为 `Result` 类型：

```python
@dataclass
class Result(Generic[T]):
    ok: bool
    value: T | None
    error: ServiceError | None

@dataclass
class ServiceError:
    code: str                # 机器可读错误码
    message: str             # 用户可读消息
    detail: dict | None      # 调试详情
    recoverable: bool        # 是否可重试
```

UI 接收 `Result`，按 `error.code` 决定提示文案和后续操作。所有 `Result` 同时写 `app.log`（带 stack trace 仅 ERROR/FATAL）。

### 5.2 各模块错误处理细则

#### EnvironmentService

| 场景 | 错误码 | 处理 |
|-----|-------|-----|
| 名称已存在 | `ENV_NAME_DUPLICATE` | 提示用户改名 |
| 路径已有非空内容 | `ENV_PATH_NOT_EMPTY` | 拒绝创建，给出占位文件列表 |
| venv 创建失败（Python 缺失） | `VENV_PYTHON_MISSING` | 引导用户装 Python 3.10+ 或指定其他 Python |
| 端口被占 | `ENV_PORT_IN_USE` | 自动分配下一个空闲端口，提示用户确认 |
| shared 布局但共享 ComfyUI 源不存在 | `COMFYUI_SOURCE_MISSING` | 拒绝创建，引导用户先指定 ComfyUI 源 |
| junction 创建失败（权限不足） | `FS_JUNCTION_FAILED` | 建议改用 independent 布局 |

#### NodeService / CatalogService

| 场景 | 错误码 | 处理 |
|-----|-------|-----|
| git clone 网络失败 | `GIT_CLONE_FAILED` | 显示错误，保留重试按钮 |
| 仓库已存在（重复添加） | `NODE_ALREADY_EXISTS` | 提示先 update |
| git pull 冲突（本地修改） | `GIT_PULL_CONFLICT` | 备份本地修改，强制更新 |
| 节点目录被外部删除 | `NODE_PATH_MISSING` | 标记 orphan，从 catalog 移除或标记需重新克隆 |
| requirements.txt 解析失败 | `REQ_PARSE_FAILED` | 节点仍可用，但 conflicts 中忽略 Python 依赖 |
| 节点无 `__init__.py` 或非 ComfyUI 节点 | `NODE_INVALID` | 标记不可用，仍入库但 UI 灰显 |

#### ConflictService

| 场景 | 错误码 | 处理 |
|-----|-------|-----|
| LocalSource 抛错（不应发生） | `CONFLICT_INTERNAL` | 返回空冲突列表 + 日志 ERROR |
| 缓存损坏 | `CACHE_CORRUPT` | 重算并覆盖 |
| WebSource 不可用（未来） | `WEB_SOURCE_DOWN` | 降级到仅 LocalSource，UI 提示 |

#### ProcessService

| 场景 | 错误码 | 处理 |
|-----|-------|-----|
| venv 不存在 | `VENV_MISSING` | 引导先创建 venv |
| 启动超时（无 "Starting server"） | `STARTUP_TIMEOUT` | 自动 kill，显示日志最后 50 行 |
| 进程意外退出 | `PROCESS_CRASHED` | status=error，保留日志，UI 标记红色 |
| 端口仍占用（stop 后未释放） | `PORT_STILL_BOUND` | 等待 5s 重试，超时后报错给用户 |

### 5.3 边界情况与恢复策略

**1. catalog 节点被外部修改/删除**

- 启动时扫描 `catalog/nodes/`，对比数据库
- DB 有但 FS 无 → 标记 `status=missing`，UI 提供"重新克隆"
- FS 有但 DB 无 → 提示用户"是否添加到 catalog？"

**2. junction 失效（如目标节点被删）**

- 启动环境前自检所有 `enabled_node_ids` 的 junction
- 发现悬空 junction → 标记 warning，提供"重新链接"或"从该环境移除"

**3. venv 损坏**

- 检测：尝试 `python -c "import sys; sys.exit(0)"`，失败则标记 corrupt
- 处理：UI 提供"重建 venv"按钮（保留 custom_nodes，只重建 Python 环境）

**4. 数据库迁移**

- 启动时检查 `schema_version`
- 不匹配时执行迁移脚本（向上兼容）
- 迁移失败保留旧 db 文件为 `catalog.db.bak.<timestamp>`

**5. 并发安全**

- SQLite 用 WAL 模式，单写多读
- 同一环境并发操作（同时点启动+停止）由 UI 层互斥，按钮 disable
- 跨环境无锁

**6. ComfyUI 版本兼容**

- 元数据存 `comfyui_required_version`（若节点声明）
- 创建环境时选择 ComfyUI 版本；后续启动检查一致性

**7. 大型 catalog 的性能**

- 单次扫描 100+ 节点，AST 解析可能耗时
- 解决：首次全量扫描入库；增量只对新增/更新的节点扫描
- 静态分析在独立线程池执行，限制并发 4

---

## 6. 日志

**应用日志位置：** `%APPDATA%\ComfyUI-Manager\logs\app-YYYY-MM-DD.log`

**格式：**

```
2026-06-21 14:32:11 INFO  [EnvironmentService] create env=env-sdxl layout=shared port=8188 py=C:/Python310/python.exe
2026-06-21 14:32:15 ERROR [NodeService] git clone failed url=https://github.com/... err=connection timeout
2026-06-21 14:32:15 WARN  [StaticAnalyzer] requirements.txt parse failed node=ComfyUI-Impact-Pack
```

**日志级别：**

- 默认 INFO
- `--debug` 启动时输出 DEBUG（含 AST 节点详情）
- ERROR/FATAL 强制输出 stack trace

**进程日志单独存放：** `envs/<env>/logs/comfyui-YYYY-MM-DD.log`

---

## 7. 测试策略

### 7.1 单元测试（pytest）

按模块拆分，目标覆盖率 ≥80%：

| 模块 | 测试重点 |
|-----|---------|
| `StaticAnalyzer/*` | 各种 requirements.txt 写法、import 形式、side_effects 检测 |
| `ConflictService` | 给定已知节点组合，验证冲突结果 |
| `VenvManager` | mock subprocess，验证参数 |
| `GitManager` | mock git 命令，验证调用顺序 |
| `FS` | junction 创建/删除（用 tmp 目录） |
| `models/*` | dataclass 序列化、SQLite round-trip |

**关键测试用例：**

```python
def test_requirements_analyzer_handles_pinned_versions():
    reqs = analyzer.parse(fixture("requirements-pinned.txt"))
    assert reqs == [RequirementSpec("torch", "==2.1.0"), ...]

def test_requirements_analyzer_handles_range_conflicts():
    # 节点 A 要求 torch<2.0，节点 B 要求 torch>=2.1
    conflicts = conflict_service.analyze([node_a, node_b])
    assert any(c.type == PYTHON_DEP and c.severity == "error" for c in conflicts)

def test_import_analyzer_detects_conditional_imports():
    imports = analyzer.extract(fixture("conditional_import.py"))
    assert "torch" in imports and "onnxruntime" in imports

def test_side_effect_detects_sys_path_modification():
    effects = analyzer.extract(fixture("syspath_modify.py"))
    assert any(e.kind == "sys_path_append" for e in effects)

def test_registry_detects_duplicate_node_class_key():
    # 两个节点都注册 "KSampler" key
    conflicts = conflict_service.analyze([node_a, node_b])
    assert any(c.type == REGISTRY_COLLISION for c in conflicts)
```

### 7.2 集成测试

- **环境生命周期**：创建 → 启动 → 停止 → 删除 全流程（用临时目录）
- **节点启停冲突**：启用冲突节点应被阻止，启用无冲突节点应成功
- **多环境隔离**：env A 启用的节点不影响 env B

集成测试用真实文件系统（pytest tmp_path），但 git clone 用 fixture 仓库（避免网络依赖）。

### 7.3 UI 测试

- 关键交互用 `pytest-qt` 验证
- 主流程：创建环境 → 添加节点 → 启用 → 启动 ComfyUI → 停止

### 7.4 端到端冒烟测试

手动测试清单（每次发版前跑）：

1. 安装全新应用 → 创建第一个环境
2. 导入已存在的 ComfyUI 环境
3. catalog 添加 10 个常见节点
4. 创建 3 个环境，每个启用不同节点子集
5. 同时启动 3 个环境，确认端口无冲突
6. 故意启用两个已知冲突节点，确认被阻止
7. 关闭应用后重启，所有状态恢复正确

---

## 8. 安全考虑

- **不自动执行节点代码**：扫描时只做 AST，不 `import` 或 `exec`
- **Git clone 限制**：仅允许 `https://github.com/` 开头（本期）；未来可加 allowlist
- **Junction 目标校验**：建 junction 前确认目标在 catalog/nodes/ 内，防止恶意路径
- **敏感信息**：端口、路径写日志时脱敏（无 token、密码）

---

## 9. 里程碑

### 9.1 整体迭代规划

整个系统分 **4 个里程碑**，每个里程碑交付一个可独立使用的版本：

```
M0 (2-3 周)          M1 (3-4 周)            M2 (2-3 周)           M3 (后续)
   │                    │                      │                     │
   ▼                    ▼                      ▼                     ▼
┌─────────┐         ┌──────────┐          ┌──────────┐         ┌─────────────┐
│ CLI 内核 │ ──────► │ GUI 基础 │ ──────►  │ 冲突分析 │ ──────► │ 远程管理/   │
│ (无 UI)  │         │ + 环境   │          │ + 节点   │         │ 网站对接等   │
└─────────┘         └──────────┘          └──────────┘         └─────────────┘
```

### 9.2 M0 - CLI 内核（验证架构可行性）

**目标：** 用 CLI 验证后端架构（目录布局、venv、junction、catalog、SQLite、settings 路径配置）能否跑通，不写 UI。

**交付：**

- 项目骨架（目录结构、依赖、pytest 配置）
- `GitManager`、`VenvManager`、`FS` 模块（junction）
- `CatalogService`（SQLite 建表 + 增删改查）
- `SettingsService`（appdata 下的 settings.json，含 catalog_db_path 路径解析、迁移）
- CLI 命令：
  ```bash
  comfy-mgr env create --name env1 --layout shared --port 8188 --python C:/Python310/python.exe
  comfy-mgr env list
  comfy-mgr env start env1
  comfy-mgr env stop env1
  comfy-mgr catalog add https://github.com/ltdrdata/ComfyUI-Impact-Pack
  comfy-mgr catalog list
  comfy-mgr settings show
  comfy-mgr settings set catalog_db_path D:/data/catalog.db
  ```
- 单元测试：FS、GitManager（mock）、VenvManager（mock）、CatalogService、SettingsService

**不做：** GUI、QML、静态分析器、冲突检测、日志美化、i18n（CLI 阶段不需）

**完成标准：** 能用 CLI 完成"创建环境（含 Python 路径） → 添加节点 → 启动 ComfyUI → 停止"全流程；settings 路径切换功能可用。

### 9.3 M1 - GUI 基础 + 环境管理（最小可用 UI）

**目标：** 第一个可见可用的桌面应用，能管理环境。

**交付：**

- PySide6 + QML 主窗口
- Material You 紫色主题（Qt Quick Controls Material）
- **i18n 框架就位：** Qt Linguist `.ts` + `.qm`，中/英双语齐上；新建 QML 字符串全部走 `qsTr()`，Python 文案走 `tr()`
- 左侧环境列表 + 右侧"环境详情"页
- 创建/删除/克隆环境对话框（表单含 Python 解释器选择）
- 启动/停止按钮（带状态指示）
- 进程日志查看器
- 节点 catalog 列表（只读）+ 添加/删除
- `EnvironmentService`、`NodeService`、`ProcessService` 实现
- QML ↔ Python 桥接
- 设置页面（数据库位置、主题、语言、默认 Python 路径）
- **打包脚本：** 同时提供 PyInstaller（`build_pyinstaller.spec`）和 Nuitka（`build_nuitka.py`）；CI 出两套 .exe

**不做：** 冲突检测、节点勾选/启用的 UI、节点详情面板

**完成标准：** 用 GUI 能完成 CLI 能做的所有事，体验明显优于 CLI；切换中/英界面无残留硬编码字符串；两套打包脚本均能产出可运行 .exe。

### 9.4 M2 - 冲突分析 + 节点启用（核心价值）

**目标：** 实现本系统的核心差异化功能 —— 节点冲突分析。

**交付：**

- `StaticAnalyzer` 全部 4 个子模块（requirements/imports/side_effects/registry）
- `ConflictService` + `LocalStaticSource`
- 节点启用 UI（每环境独立勾选节点）
- 实时冲突警告（勾选时即时反馈）
- 冲突详情弹窗
- 已知冲突表（`known_incompat`，手动维护）
- 节点元数据刷新命令

**不做：** 远程管理、网站对接、冲突可视化图谱、强制启用

**完成标准：** 启用任意两个已知冲突节点时被阻止并给出明确原因。

### 9.5 M3 - 远程管理与网站对接（扩展期）

**目标：** 把工具从单机工具升级为可协作、可远程的方案。

**交付（候选）：**

- 远程环境管理（SSH 目标主机）
- `WebCommunitySource` 实现：拉取社区已知冲突
- 冲突图可视化（Canvas/GraphView）
- 环境导出/导入（打包整个 env 到 zip）
- 自动备份（按 cron 备份 env 配置）
- 多用户/权限（如果需要）
- 国际化（中/英）

### 9.6 MVP 定义（M1 完成时）

**最小可用版本的验收清单：**

- 能创建、删除、克隆环境
- 能选择 shared / independent 布局
- 能从 GitHub URL 添加节点到 catalog
- 能在 catalog 中删除、更新节点
- 能启动/停止环境的 ComfyUI 进程
- 能实时查看进程日志
- 端口分配不冲突
- 应用重启后所有状态正确恢复
- 至少 3 个环境可同时存在并独立运行

**明确不在 MVP 内：** 节点冲突检测、节点在环境内的勾选启用、远程管理

### 9.7 风险与备选方案

| 风险 | 影响 | 备选方案 |
|-----|------|---------|
| Windows junction 在某些环境权限受限 | shared 布局不可用 | 默认改成 independent，shared 作为高级选项 |
| ComfyUI 启动参数不支持指定 custom_nodes 路径 | 无法完全共享 | 已规避：每个 env 独立 custom_nodes 目录 |
| 节点 AST 分析漏掉动态 import | 漏报冲突 | 提示用户"动态 import 可能漏检"；保守给出警告而非阻断 |
| pip install 慢导致 venv 创建耗时 | UI 卡顿 | 进度条 + 取消；后台线程 |
| 50+ 节点时 catalog 刷新慢 | UX 卡顿 | 增量扫描 + 后台刷新 |

---

## 10. 关键决策（已确认）

| # | 决策项 | 最终方案 | 影响 |
|---|--------|---------|------|
| 1 | 数据库位置 | **可配置**：默认 `%APPDATA%\ComfyUI-Manager\catalog.db`，用户在 settings 中可改 | M0 加 `SettingsService`；M1 设置页面提供切换 UI |
| 2 | Python 版本 | **任意路径**：用户创建环境时指定任意 Python 解释器（≥3.9） | M0 CLI 加 `--python` 参数；M1 表单加路径选择 |
| 3 | 国际化 | **M1 上 i18n 框架**：Qt Linguist + `.ts/.qm`，中/英双语齐上 | M0 无；M1 全部新写字符串走 `qsTr()`/`tr()` |
| 4 | 品牌色 | **Material You 紫色**（Qt Quick Controls Material） | M0 无；M1 QML 主题设置 |
| 5 | 打包 | **同时提供 PyInstaller 和 Nuitka 两种脚本** | M2 发布前产出两套 .exe，用户自选 |

**决策落地的设计变更（散落各章）：**

- `EnvironmentService.create()` 新增 `python_path: Path` 参数；`Environment` 模型加 `python_executable` 字段；SQLite `environments` 表加 `python_executable` 列
- 新增 `SettingsService`：管理 `settings.json`（catalog_db_path、theme、language、default_python_path、log_level），含路径迁移
- 日志位置改为 `%APPDATA%\ComfyUI-Manager\logs\`
- 9.2 M0 增补 `--python` 参数和 `settings show/set` 命令
- 9.3 M1 增补 Material 主题、i18n 框架、设置页面、两套打包脚本

**明确不在本期范围：** 节点版本自动升降级、节点沙箱执行、远程管理、社区网站对接。

---

**附录 A. 术语表**

| 术语 | 含义 |
|-----|------|
| venv | Python 虚拟环境，独立的 Python 解释器和包 |
| custom_nodes | ComfyUI 自定义节点目录 |
| catalog | 全局节点仓库，统一管理所有可用节点 |
| junction | Windows 目录链接（reparse point），类似 symlink |
| 已知冲突 | 社区/官方报告的节点不兼容关系 |
| LocalSource | 基于节点源码的本地静态分析 |
| WebSource | 基于社区网站数据的冲突分析（未来） |

**附录 B. 参考资料**

- ComfyUI 官方文档：https://docs.comfy.org/
- ComfyUI Manager 项目：https://github.com/ltdrdata/ComfyUI-Manager
- PySide6 文档：https://doc.qt.io/qtforpython-6/
- QML 文档：https://doc.qt.io/qt-6/qmlapplications.html