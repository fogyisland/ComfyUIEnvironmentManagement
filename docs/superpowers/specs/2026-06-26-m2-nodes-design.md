# ComfyUI Manager - M2 节点管理 + 冲突检测 设计文档

**日期：** 2026-06-26
**状态：** 待用户确认 v1
**作者：** Claude (brainstorming session)
**基于：** M1 spec `2026-06-24-m1-gui-design.md` 第 9.3 节（M2 范围延后清单）

---

## 1. 背景与目标

M0 + M1 已落地（v0.1.0 已发布到 GitHub）：CLI 内核 104+2 测试 + PySide6 GUI 26 task 157+2 测试。M1 完成了 env CRUD、QProcess 实时日志、catalog (pip) 管理、设置页。

**M1 spec 9.3 延后清单（M2 必做）：**
- 冲突检测（StaticAnalyzer / ConflictService）
- 节点勾选/启用的 UI
- 节点详情面板

**用户决策（M2 brainstorming 2026-06-26，已通过 AskUserQuestion 确认）：**

| 决策点 | 选择 |
|---|---|
| 范围 | 严格按 M1 延后清单 (3 块) |
| 冲突粒度 | 包 + 类,静态 import (不启 venv) |
| 启用/禁用机制 | DB 标志位 (默认) + 文件夹重命名 (设置里切换) |
| 详情面板数据 | 本地优先, 在线按需 (GitHub) |
| 扫描触发 | 懒扫描 (打开 EnvDetailPage 时) + 缓存 |
| 冲突 UI | 顶部 badge + 三动作 (禁用/详情/忽略) |
| 物理结构 | 每个 env 独立 `custom_nodes/`,不共享不硬链 |

**M2 不做（延后到 M3+）：**
- 在线节点目录浏览（GitHub PR / 社区节点仓库）
- 节点版本管理 / 升级 / 回滚
- 节点依赖自动解析（`missing_dep` 类型留 schema 占位）
- 远程管理（web 控制）
- 打包脚本重写

---

## 2. 架构与分层

```
┌─────────────────────────────────────────────────────────┐
│ QML 层 (app/qml/*.qml)                                  │
│  EnvironmentDetailPanel.qml (M1 改造)                   │
│  ├─ ConflictPanel.qml (M2 NEW)                          │
│  ├─ NodeListItem.qml (M2 NEW)                           │
│  └─ NodeDetailPanel.qml (M2 NEW)                        │
└────────────────────────┬────────────────────────────────┘
                         │ Bridge (Q_PROPERTY + Slot)
┌────────────────────────┴────────────────────────────────┐
│ Bridge 层 (app/bridge/)                                 │
│  NodeBridge.qml (M2 NEW)                                │
│  ├─ Property: nodeList / conflictList / busy            │
│  ├─ Slot: requestScan / setDisabled / fetchRemote       │
│  └─ Signal: nodesChanged / conflictsChanged            │
└────────────────────────┬────────────────────────────────┘
                         │ 直接调用 (返回 Result[T])
┌────────────────────────┴────────────────────────────────┐
│ Service 层 (src/comfy_mgr/services/)                    │
│  NodeService (M2 NEW)  ──→ 扫/启停/CRUD                 │
│  ConflictService (M2 NEW) ──→ 冲突检测                   │
│  NodeMetaService (M2 NEW) ──→ 在线元数据                 │
│  + EventBus (AppContext 内) 协调 nodesChanged          │
└────────────────────────┬────────────────────────────────┘
                         │
┌────────────────────────┴────────────────────────────────┐
│ Infra 层 (src/comfy_mgr/infra/)                        │
│  NodeScanner (M2 NEW)  ──→ AST 解析 class_mappings     │
│  GitHubClient (M2 NEW) ──→ 拉在线元数据                  │
│  DB schema v3 (M2 增量)                                 │
└─────────────────────────────────────────────────────────┘
```

**M0 + M1 不动约束：**
- M0 Service 接口签名 100% 不变
- M1 Bridge 接口只增不改（`EnvironmentBridge` / `ProcessBridge` 等）
- M1 UI 组件不重写，只在 `EnvironmentDetailPanel.qml` 里嵌入新区域

> ⚠️ **M0 已存在 `nodes` 表**(M0 设计:git catalog 模式,`id/name/repo_url/local_path/...`)+ M0 的 `NodeService`(junction enable/disable)。
> M2 实际命名调整为:
> - M2 新表 → `scanned_nodes`(避免与 M0 `nodes` 冲突)
> - M2 新模型 → `ScannedNode`
> - M2 新服务 → `ScannedNodeService`(避免与 M0 `NodeService` 冲突)
> - M1 的 `NodeBridge` 在原文件上**扩展**新 slot,不动旧 slot
>
> 详见 M2 plan Task 1 + Task 12。

---

## 3. App 目录结构（M2 增量）

```
D:\ToolDevelop\ComfyUI\
├─ src\comfy_mgr\                        【M0 + M1 不动】
│  ├─ services\
│  │  ├─ node_service.py                 【M2 NEW】
│  │  ├─ conflict_service.py             【M2 NEW】
│  │  └─ node_meta_service.py            【M2 NEW】
│  └─ infra\
│     ├─ node_scanner.py                 【M2 NEW】
│     ├─ github_client.py                【M2 NEW】
│     └─ event_bus.py                    【M2 NEW】
│
├─ app\                                  【M1 不动 + M2 增量】
│  ├─ bridge\
│  │  └─ node_bridge.py                  【M2 NEW】
│  ├─ qml\
│  │  ├─ pages\
│  │  │  └─ EnvironmentDetailPanel.qml   【M1 改造】
│  │  └─ components\
│  │     ├─ ConflictPanel.qml            【M2 NEW】
│  │     ├─ NodeListItem.qml             【M2 NEW】
│  │     ├─ NodeDetailPanel.qml          【M2 NEW】
│  │     └─ NodeScanBusy.qml             【M2 NEW】
│  └─ app_context.py                     【M1 改造：加 node/conflict/meta 服务 + bus】
│
├─ tests\                                【M0 + M1 不动 + M2 增量】
│  ├─ services\
│  │  ├─ test_node_service.py            【M2 NEW】
│  │  ├─ test_conflict_service.py        【M2 NEW】
│  │  └─ test_node_meta_service.py       【M2 NEW】
│  ├─ infra\
│  │  ├─ test_node_scanner.py            【M2 NEW】
│  │  └─ test_github_client.py           【M2 NEW】
│  └─ bridge\
│     └─ test_node_bridge.py             【M2 NEW】
│
├─ docs\superpowers\
│  ├─ specs\2026-06-26-m2-nodes-design.md  (本文件)
│  └─ plans\2026-06-26-m2-nodes.md          【M2 plan,M2 阶段写】
│
├─ pyproject.toml                        【M2 不变依赖】
└─ start.bat                             【M1 不动】
```

**pyproject.toml 不变：**
- M2 不引入新依赖（`ast` 是标准库,`urllib.request` 是标准库）

---

## 4. 数据模型（schema v3）

### 4.1 三张新表

```sql
-- ============================================================
-- 1) nodes:每个 env 的 custom_nodes/ 里的每个子目录 = 一行
-- ============================================================
CREATE TABLE IF NOT EXISTS nodes (
    id              TEXT PRIMARY KEY,            -- uuid
    env_id          TEXT NOT NULL
                    REFERENCES environments(id) ON DELETE CASCADE,
    package         TEXT NOT NULL,               -- "ComfyUI-Impact-Pack"
    package_path    TEXT NOT NULL,               -- 绝对路径,unique per env
    version         TEXT,                        -- 来自 pyproject / __init__
    author          TEXT,                        -- 来自 pyproject
    description     TEXT,                        -- pyproject description
    class_mappings  TEXT NOT NULL DEFAULT '[]',  -- JSON: ["KSampler", ...]
    status          TEXT NOT NULL DEFAULT 'enabled'
                    CHECK(status IN ('enabled','disabled')),
    scan_meta       TEXT NOT NULL DEFAULT '{}',  -- JSON: {source, warnings}
    last_scanned_at TEXT,                        -- ISO8601
    UNIQUE(env_id, package)
);

CREATE INDEX IF NOT EXISTS idx_nodes_env ON nodes(env_id);
CREATE INDEX IF NOT EXISTS idx_nodes_status ON nodes(env_id, status);

-- ============================================================
-- 2) node_meta_cache:在线元数据缓存,按 package 名跨 env 共享
-- ============================================================
CREATE TABLE IF NOT EXISTS node_meta_cache (
    package         TEXT PRIMARY KEY,            -- 全局唯一(跨 env 共享)
    github_url      TEXT,
    stars           INTEGER,
    last_commit     TEXT,
    homepage        TEXT,
    fetched_at      TEXT NOT NULL,               -- ISO8601
    fetch_error     TEXT                         -- 上次失败原因(成功为 NULL)
);

-- ============================================================
-- 3) node_conflicts:冲突检测结果(ConflictService 写)
-- ============================================================
CREATE TABLE IF NOT EXISTS node_conflicts (
    id              TEXT PRIMARY KEY,            -- uuid
    env_id          TEXT NOT NULL
                    REFERENCES environments(id) ON DELETE CASCADE,
    conflict_type   TEXT NOT NULL
                    CHECK(conflict_type IN
                          ('duplicate_class','version_mismatch','missing_dep')),
    node_ids        TEXT NOT NULL,               -- JSON: [node_id1, ...] sorted
    detail          TEXT NOT NULL,               -- JSON: 详情
    detected_at     TEXT NOT NULL,
    resolved_at     TEXT,                        -- NULL = 未解决
    ignored         INTEGER NOT NULL DEFAULT 0
);

CREATE INDEX IF NOT EXISTS idx_conflicts_active
    ON node_conflicts(env_id) WHERE resolved_at IS NULL;
```

### 4.2 关键决定

- `node_meta_cache.package` 是 **PK 而非 UNIQUE(env_id, package)** — 同一 PyPI/GitHub 包的元数据跨 env 复用,缓存命中率高
- `node_conflicts` 用**部分索引**让活跃冲突查询走索引
- `node_ids` **排序后存** — 同样冲突重算后 `id` 一致(幂等)
- 冲突**不删行** — `resolved_at` / `ignored=1` 标记,审计需要
- `class_mappings` 存 JSON 字符串(不是单独的 `node_classes` 表) — 节点类只用于冲突检测,不单独查询,内嵌即可

### 4.3 schema migration v3

M2 在 M1 的 v2 schema 基础上**只新增三张表**(都用 `CREATE TABLE IF NOT EXISTS`)。
不动 `environments` / `processes` / `settings` 等已存在表。
DB 版本号:仍是 v2(因为没有破坏性变更,只是新增);如未来加版本号字段再升 v3。

---

## 5. NodeScanner：AST 静态 import

### 5.1 三层降级策略

```python
from enum import Enum
import ast
from pathlib import Path

class ScanSource(str, Enum):
    AST_CLEAN    = "ast_clean"     # 字面量 dict,完整提取
    AST_DYNAMIC  = "ast_dynamic"   # 含 **展开 / 函数调用,放弃
    NOT_FOUND    = "not_found"     # 没找到 NODE_CLASS_MAPPINGS
    PARSE_ERROR  = "parse_error"   # 语法错

def extract_class_mappings(init_py: Path) -> tuple[list[str], ScanSource, list[str]]:
    """返回 (class_mappings, source, warnings)"""
    warnings = []
    try:
        src = init_py.read_text(encoding="utf-8", errors="ignore")
    except OSError as e:
        return [], ScanSource.NOT_FOUND, [f"read_failed: {e}"]

    try:
        tree = ast.parse(src)
    except SyntaxError as e:
        return [], ScanSource.PARSE_ERROR, [f"syntax_error: {e.lineno}"]

    for node in ast.walk(tree):
        if not isinstance(node, ast.Assign):
            continue
        for target in node.targets:
            if not (isinstance(target, ast.Name)
                    and target.id == "NODE_CLASS_MAPPINGS"):
                continue
            # 命中 NODE_CLASS_MAPPINGS = ...
            if isinstance(node.value, ast.Dict):
                # 字面量 dict:{ "Key": Cls, ... }
                keys = [k.value for k in node.value.keys
                        if isinstance(k, ast.Constant)
                        and isinstance(k.value, str)]
                return keys, ScanSource.AST_CLEAN, warnings
            else:
                # 其他形式(函数调用、** 展开、变量引用)
                return [], ScanSource.AST_DYNAMIC, ["dynamic_mappings"]

    return [], ScanSource.NOT_FOUND, ["mappings_not_found"]
```

### 5.2 行为表

| 输入形式 | 提取结果 | scan_meta.source | scan_meta.warnings |
|---|---|---|---|
| `NODE_CLASS_MAPPINGS = {"A": A, "B": B}` | `["A","B"]` | `ast_clean` | `[]` |
| `NODE_CLASS_MAPPINGS = {**BASE, "A": A}` | `[]` | `ast_dynamic` | `["dynamic_mappings"]` |
| `NODE_CLASS_MAPPINGS = build_mappings()` | `[]` | `ast_dynamic` | `["dynamic_mappings"]` |
| 没有 `NODE_CLASS_MAPPINGS` | `[]` | `not_found` | `["mappings_not_found"]` |
| `__init__.py` 语法错 | `[]` | `parse_error` | `["syntax_error: 5"]` |
| 文件不存在 / 读不了 | `[]` | `not_found` | `["read_failed: ..."]` |

### 5.3 不做的事（YAGNI）

- **不**启 venv、**不** subprocess、**不** `exec()` 字符串
- **不**尝试展开 `{**X, "Y": Z}` 里的 `X`(需要再 parse 整文件,易错)
- **不**做解析超时(单文件毫秒级)
- **不**递归进子目录(只看 `custom_nodes/<pkg>/__init__.py`,子目录忽略)

---

## 6. NodeService

### 6.1 接口

```python
class NodeService:
    def __init__(self, db: Connection, env_service: EnvironmentService,
                 bus: EventBus, scanner: NodeScanner):
        ...

    def scan(self, env_id: str) -> Result[list[Node]]:
        """扫描 env 的 custom_nodes/,upsert 到 nodes 表,emit nodesChanged"""

    def list_nodes(self, env_id: str, *,
                   include_disabled: bool = True) -> Result[list[Node]]:
        """列出 env 下所有节点"""

    def get(self, node_id: str) -> Result[Node]:
        """按 id 查节点"""

    def set_disabled(self, node_id: str, disabled: bool) -> Result[Node]:
        """修改 status;folder_rename 模式下同时重命名目录"""

    def toggle_disabled(self, node_id: str) -> Result[Node]:
        """set_disabled 的便捷封装"""
```

### 6.2 scan 流程

> **注**:以下代码块中 `_placeholder_node` / `_parse_pyproject` / `_now_iso` 是模块内私有 helper,`NodeService` 同文件定义,plan 阶段在 task 里给出完整实现。

```python
def scan(self, env_id: str) -> Result[list[Node]]:
    env = self._env_service.get(env_id).value
    custom_nodes_dir = Path(env.root) / "custom_nodes"

    # 1) 确保目录存在(M1 没建,M2 兜底)
    if not custom_nodes_dir.exists():
        custom_nodes_dir.mkdir(parents=True, exist_ok=True)

    # 2) 列出所有子目录
    pkg_dirs = [p for p in custom_nodes_dir.iterdir()
                if p.is_dir() and not p.name.startswith((".", "_"))]

    # 3) 逐个解析
    nodes = []
    for pkg_dir in pkg_dirs:
        node = self._scan_one_pkg(env_id, pkg_dir)
        if node.is_ok:
            nodes.append(node.value)
        else:
            # 整包失败也建一个 placeholder,带 warnings
            nodes.append(_placeholder_node(env_id, pkg_dir, node.error.message))

    # 4) upsert 到 DB
    for n in nodes:
        self._db.execute("""INSERT INTO nodes (...) VALUES (...)
            ON CONFLICT(env_id, package) DO UPDATE SET ...""", n.to_row())

    # 5) emit
    self._bus.emit("nodesChanged", env_id)
    return Result.ok(nodes)

def _scan_one_pkg(self, env_id: str, pkg_dir: Path) -> Result[Node]:
    meta = _parse_pyproject(pkg_dir)            # name, version, author, desc
    classes, source, warnings = self._scanner.extract_class_mappings(
        pkg_dir / "__init__.py"
    )
    scan_meta = {"source": source.value, "warnings": warnings}
    return Result.ok(Node(
        id=uuid4(), env_id=env_id, package=meta.name or pkg_dir.name,
        package_path=str(pkg_dir), version=meta.version,
        author=meta.author, description=meta.description,
        class_mappings=classes, status="enabled",
        scan_meta=json.dumps(scan_meta),
        last_scanned_at=_now_iso(),
    ))
```

### 6.3 set_disabled 流程

```python
def set_disabled(self, node_id: str, disabled: bool) -> Result[Node]:
    node = self.get(node_id).value
    new_status = "disabled" if disabled else "enabled"

    with self._db.transaction():
        self._db.execute(
            "UPDATE nodes SET status=? WHERE id=?", new_status, node_id
        )

        # folder_rename 模式下同步重命名
        if self._settings.get("node_disable_mode") == "folder_rename":
            self._apply_folder_rename(node, new_status)

    self._bus.emit("nodesChanged", node.env_id)
    return self.get(node_id)

def _apply_folder_rename(self, node: Node, new_status: str):
    p = Path(node.package_path)
    if new_status == "disabled" and not p.name.endswith(".disabled"):
        target = p.with_name(p.name + ".disabled")
        try:
            p.rename(target)
            self._db.execute("UPDATE nodes SET package_path=? WHERE id=?",
                             str(target), node.id)
        except OSError as e:
            # 回退到 db_flag,记 warning
            self._append_warning(node.id, f"rename_failed: {e}")
```

### 6.4 EventBus

```python
# src/comfy_mgr/infra/event_bus.py
class EventBus:
    """进程内轻量事件总线,同步 emit,无队列。"""
    def __init__(self):
        self._handlers: dict[str, list[Callable]] = {}

    def on(self, event: str, handler: Callable):
        self._handlers.setdefault(event, []).append(handler)

    def emit(self, event: str, *args, **kwargs):
        for h in self._handlers.get(event, []):
            h(*args, **kwargs)

    def off(self, event: str, handler: Callable):
        if event in self._handlers:
            self._handlers[event].remove(handler)
```

单例挂在 `AppContext.bus` 上。ConflictService 在 `__init__` 里 `bus.on("nodesChanged", self._recompute)`。

---

## 7. ConflictService

### 7.1 接口

```python
class ConflictService:
    def __init__(self, db: Connection, node_service: NodeService,
                 bus: EventBus):
        node_service.bus.on("nodesChanged", self._on_nodes_changed)

    def detect(self, env_id: str) -> Result[list[Conflict]]:
        """算冲突,写 node_conflicts 表,返回当前活跃冲突列表"""

    def list_active(self, env_id: str) -> Result[list[Conflict]]:
        """只读查询,resolved_at IS NULL AND ignored=0"""

    def resolve(self, conflict_id: str) -> Result[None]:
        """标记 resolved_at = now"""

    def ignore(self, conflict_id: str) -> Result[None]:
        """标记 ignored = 1 + resolved_at = now"""
```

### 7.2 检测算法

```python
def detect(self, env_id: str) -> Result[list[Conflict]]:
    # 1) 读 enabled 节点
    nodes = self._db.query(
        "SELECT * FROM nodes WHERE env_id=? AND status='enabled'", env_id
    )

    conflicts = []

    # 2) duplicate_class:多个 enabled 节点提供同一 class
    class_to_nodes: dict[str, list[str]] = defaultdict(list)
    for n in nodes:
        for cls in json.loads(n.class_mappings):
            class_to_nodes[cls].append(n.id)
    for cls, ids in class_to_nodes.items():
        if len(ids) > 1:
            conflicts.append(Conflict(
                id=uuid4(), env_id=env_id,
                conflict_type="duplicate_class",
                node_ids=sorted(ids),  # 排序保证幂等
                detail=json.dumps({"class": cls}),
                detected_at=_now_iso(),
            ))

    # 3) version_mismatch:同 package 多个目录注册(防御性,几乎不会发生)
    by_pkg: dict[str, list[str]] = defaultdict(list)
    for n in nodes:
        by_pkg[n.package].append(n.id)
    for pkg, ids in by_pkg.items():
        if len(ids) > 1:
            conflicts.append(Conflict(
                id=uuid4(), env_id=env_id,
                conflict_type="version_mismatch",
                node_ids=sorted(ids),
                detail=json.dumps({"package": pkg}),
                detected_at=_now_iso(),
            ))

    # 4) 写表:清旧活跃 + 写新
    with self._db.transaction():
        # 旧的 resolved_at IS NULL 全部软删(标记 resolved)
        self._db.execute(
            "UPDATE node_conflicts SET resolved_at=? "
            "WHERE env_id=? AND resolved_at IS NULL",
            _now_iso(), env_id
        )
        for c in conflicts:
            self._db.execute("""INSERT INTO node_conflicts (...) VALUES (...)""",
                             c.to_row())

    return Result.ok(conflicts)
```

### 7.3 触发时机

| 事件 | 触发 |
|---|---|
| `nodesChanged(env_id)` | 自动 `_recompute(env_id)` |
| `set_disabled` 完成后 | 同上(发 `nodesChanged`) |
| 用户在 UI 点"重算冲突" | 显式 `detect(env_id)` |

---

## 8. NodeMetaService

### 8.1 接口

```python
class NodeMetaService:
    def __init__(self, db: Connection, http: GitHubClient,
                 cache_ttl_seconds: int = 3600):
        ...

    def get_cached(self, package: str) -> Result[NodeMeta | None]:
        """只读缓存,1h 内直接返"""

    def fetch(self, package: str) -> Result[NodeMeta]:
        """强制刷新,从 GitHub 拉 + 写缓存"""

    def get_or_fetch(self, package: str) -> Result[NodeMeta]:
        """缓存有效返缓存,否则 fetch"""
```

### 8.2 GitHubClient

```python
class GitHubClient:
    """urllib 实现,无新依赖。"""
    def get_repo_meta(self, owner: str, repo: str) -> Result[dict]:
        url = f"https://api.github.com/repos/{owner}/{repo}"
        try:
            with urllib.request.urlopen(url, timeout=10) as resp:
                data = json.loads(resp.read().decode("utf-8"))
            return Result.ok({
                "stars": data.get("stargazers_count"),
                "last_commit": data.get("pushed_at"),
                "homepage": data.get("homepage"),
                "github_url": data.get("html_url"),
            })
        except (URLError, HTTPError, json.JSONDecodeError) as e:
            return Result.fail(ServiceError("META_FETCH_FAILED", str(e)))
```

**重试策略：**
- 失败不重试(单次,简单)
- 失败写 `node_meta_cache.fetch_error`,UI 显示友好提示
- 不暴露 token(anonymous API,60 req/h 限制够用)

### 8.3 owner/repo 怎么来?

- 暂不在 M2 做"识别 GitHub 源" — `github_url` 由用户在 EnvDetailPanel 里**手动填**或从本地 `pyproject.toml` 的 `[project.urls] Homepage` 提取
- 简单粗暴,M3 再做自动识别

---

## 9. AppContext 改造

```python
# app/app_context.py (M1 基础上加 4 行)
class AppContext:
    def __init__(self, ...):
        # M0 + M1 已有
        self.db = ...
        self.env_service = ...
        self.process_service = ...
        self.settings = ...
        # ... 其他 service

        # M2 NEW
        self.bus = EventBus()
        self.scanner = NodeScanner()
        self.github_client = GitHubClient()
        self.node_service = NodeService(self.db, self.env_service,
                                        self.bus, self.scanner)
        self.conflict_service = ConflictService(self.db, self.node_service,
                                                 self.bus)
        self.node_meta_service = NodeMetaService(
            self.db, self.github_client,
            cache_ttl_seconds=self.settings.get("meta_cache_ttl", 3600)
        )

        # 一次性迁移:补建 custom_nodes/
        self._migrate_create_custom_nodes_dirs()

    def _migrate_create_custom_nodes_dirs(self):
        """M1 没建 custom_nodes/,M2 启动时补建空目录。"""
        for env in self.env_service.list_all():
            cn = Path(env.root) / "custom_nodes"
            cn.mkdir(parents=True, exist_ok=True)
```

---

## 10. NodeBridge (QML 桥接)

```python
class NodeBridge(QObject):
    # Property
    nodeListChanged = Signal()
    conflictListChanged = Signal()
    busyChanged = Signal()

    nodeList = Property("QVariantList", _get_node_list,
                        notify=nodeListChanged)
    conflictList = Property("QVariantList", _get_conflict_list,
                            notify=conflictListChanged)
    busy = Property(bool, _get_busy, notify=busyChanged)

    # Slot
    @Slot(str, result="QVariantMap")
    def requestScan(self, env_id: str) -> dict:
        return self._invoke(self._service.scan, env_id)

    @Slot(str, bool, result="QVariantMap")
    def setDisabled(self, node_id: str, disabled: bool) -> dict:
        return self._invoke(self._service.set_disabled, node_id, disabled)

    @Slot(str, result="QVariantMap")
    def resolveConflict(self, conflict_id: str) -> dict:
        return self._invoke(self._conflict_service.resolve, conflict_id)

    @Slot(str, result="QVariantMap")
    def ignoreConflict(self, conflict_id: str) -> dict:
        return self._invoke(self._conflict_service.ignore, conflict_id)

    @Slot(str, result="QVariantMap")
    def fetchRemoteMeta(self, package: str) -> dict:
        return self._invoke(self._meta_service.fetch, package)

    @Slot(str, result="QVariantMap")
    def getNodeDetail(self, node_id: str) -> dict:
        """返回 {local: {...}, remote: {...} | null}"""
```

**信号流:**
- `node_service.bus.on("nodesChanged", lambda env: self.nodeListChanged.emit())`
- `node_service.bus.on("nodesChanged", lambda env: self.conflictListChanged.emit())`
- 同一信号驱动两个 Property 重读,简单

---

## 11. QML 组件

### 11.1 新增组件

| 文件 | 作用 | 关键 Property |
|---|---|---|
| `qml/components/ConflictPanel.qml` | 顶部冲突列表(可折叠) | `conflicts: nodeBridge.conflictList` |
| `qml/components/NodeListItem.qml` | 节点卡片 | `node: {...}`, `status: enabled/disabled`, `warning: bool` |
| `qml/components/NodeDetailPanel.qml` | 节点详情抽屉 | `local`, `remote`, `fetching` |
| `qml/components/NodeScanBusy.qml` | 扫描进度条 | `busy: nodeBridge.busy` |

### 11.2 EnvironmentDetailPanel 改造

在 M1 现有的 `EnvironmentDetailPanel.qml` 里,**在"已装 pip 包"和"日志"之间**插入新区域:

```qml
// M1 已有
ColumnLayout {
    EnvInfoCard { ... }
    ActionRow { ... }   // 启动/停止/删除

    // M1 已有
    CatalogList { ... } // 已装 pip 包

    // ============ M2 NEW ============
    ConflictPanel {
        visible: conflictList.length > 0
        conflicts: nodeBridge.conflictList
        onResolve: (conflictId) => nodeBridge.resolveConflict(conflictId)
        onDisableOne: (nodeId) => nodeBridge.setDisabled(nodeId, true)
        onIgnore: (conflictId) => nodeBridge.ignoreConflict(conflictId)
    }

    NodeScanBusy { busy: nodeBridge.busy }

    Label { text: qsTr("自定义节点 (%1)").arg(nodeList.length) }
    Button { text: qsTr("重新扫描"); onClicked: nodeBridge.requestScan(envId) }

    ListView {
        model: nodeBridge.nodeList
        delegate: NodeListItem {
            node: modelData
            onClicked: nodeDetailPanel.open(modelData)
        }
    }

    NodeDetailPanel {
        id: nodeDetailPanel
        // lazy fetch remote on open
    }
    // ============ M2 END ============

    LogSection { ... }  // M1 已有,不动
}
```

### 11.3 NodeDetailPanel 数据流

```qml
// NodeDetailPanel.qml
Drawer {
    id: root
    property var node
    property var local: ({})
    property var remote: null
    property bool fetching: false

    function open(nodeData) {
        node = nodeData
        local = {
            version: node.version,
            description: node.description,
            classMappings: JSON.parse(node.class_mappings || "[]"),
            warnings: JSON.parse(node.scan_meta || "{}").warnings || []
        }
        remote = null  // 远程信息默认不拉
        // 触发本地详情
        var result = nodeBridge.getNodeDetail(node.id)
        if (result.ok) local = result.value.local
    }

    Button {
        text: qsTr("查看远程信息")
        onClicked: {
            fetching = true
            var r = nodeBridge.fetchRemoteMeta(node.package)
            fetching = false
            if (r.ok) remote = r.value
        }
    }
}
```

---

## 12. Settings 增量

M1 `SettingsPage.qml` 加一个分组"M2":

| Key | 类型 | 默认 | UI |
|---|---|---|---|
| `node_disable_mode` | str | `db_flag` | 下拉:db_flag / folder_rename |
| `meta_cache_ttl` | int | `3600` | **M2 不暴露 UI**;M2 只在 AppContext 启动时读默认值 3600 注入 `NodeMetaService`,修改需用户改 settings.json。M3+ 再决定是否加 UI 控件 |

**`folder_rename` 模式额外说明：**
- UI 在节点卡片 hover 提示:"此模式下,禁用会重命名目录为 `<pkg>.disabled`,ComfyUI 启动时跳过"
- 切换模式**不重扫**,只是影响 `set_disabled` 行为
- 重命名失败自动回退 `db_flag` + 写 warning

---

## 13. M1 迁移

| 迁移点 | 触发时机 | 行为 |
|---|---|---|
| 补建 `custom_nodes/` 目录 | AppContext `__init__` 启动时 | 对每个 env 跑一遍 `mkdir(parents=True, exist_ok=True)` |
| `node_disable_mode` 设置 | 首次启动 | 自动写默认值 `db_flag` |
| `meta_cache_ttl` 设置 | 首次启动 | 自动写默认值 `3600` |
| settings 表加新 key | 同上(沿用 M1 模式) | |

**不迁移的:**
- 不动已有 env 的 `process_state` / catalog 等
- 不重扫已有 env 的节点(下次打开 EnvDetailPage 触发懒扫描)

---

## 14. i18n

### 14.1 新增字符串(M2 估 30-40 条)

**QML 端（qsTr）：**
- 节点列表标题:"自定义节点 (%1)"
- "重新扫描"、"禁用"、"启用"、"查看详情"、"忽略"
- "查看远程信息"、"刷新远程信息"、"在浏览器中打开"
- "X 个冲突"、"无可用远程信息"
- ConflictPanel 角标文案
- NodeDetailPanel 字段标签(版本、作者、描述、类映射)
- scan_meta warnings 中文映射

**Python 端（self.tr）：**
- 错误消息模板("无法读取 %s"、"扫描 %s 失败")

### 14.2 翻译流程

沿用 M1 工作流：
```
pyside6-lupdate app/ -ts app/qml/i18n/comfyui_manager_zh_CN.ts
       ↓
手填 / AI 翻译
       ↓
pyside6-lrelease app/qml/i18n/*.ts -qm app/qml/i18n/
```

新增 30-40 条,目标 100% qsTr / tr 覆盖率(同 M1)。

---

## 15. 错误处理

### 15.1 新增错误码(M2)

| 错误码 | 触发场景 | i18n key |
|---|---|---|
| `NODE_NOT_FOUND` | 节点 id 不存在 | `节点不存在` |
| `NODE_SCAN_FAILED` | 整包扫描异常(权限等) | `扫描节点失败: %s` |
| `NODE_RENAME_FAILED` | folder_rename 模式失败 | `重命名目录失败,已回退` |
| `CONFLICT_NOT_FOUND` | conflict id 不存在 | `冲突不存在` |
| `META_FETCH_FAILED` | GitHub API 拉失败 | `获取远程信息失败: %s` |
| `META_NO_URL` | package 没有 github_url | `该节点未配置 GitHub 仓库` |

### 15.2 错误处理矩阵

| 场景 | 行为 |
|---|---|
| env 没 `custom_nodes/` | mkdir 后扫到 0 节点, 不报错 |
| `__init__.py` 语法错 | scan_meta.warnings +=, 节点照常入库 (class_mappings=[]) |
| 整个包扫失败(权限) | 跳过该子目录, 写 placeholder node, warnings 记录 |
| 冲突计算时 DB 锁 | 短重试 3 次, 失败 → ErrorBanner, 不阻塞 scan |
| 在线 fetch 失败 | 缓存旧值, 写 fetch_error, UI 提示, 不阻塞面板 |
| 模式切换 folder_rename 失败 | 回退到 db_flag, warnings 记录 |
| 节点卡片显示 ⚠️ 角标(有 warnings) | hover 提示具体 warning |

### 15.3 不做的错误处理（YAGNI）

- AST 解析超时(单文件毫秒级, 不需要)
- 并发 scan 锁(单 user 操作, 串行即可)
- 节点文件被外部删除的实时检测(下次 scan 才发现)
- 在线 fetch 重试(单次, 简单)

---

## 16. 测试策略

### 16.1 分层

```
手动 GUI 冒烟（用户机器，5 条主流程）
        ▲
pytest-qt 测 Bridge（mock Service, 验证 Signal/Property）
        ▲
pytest 测 Service/Infra（NodeScanner / NodeService / ConflictService / GitHubClient）
```

### 16.2 新增测试清单（~30 个）

| 文件 | 测试数 | 覆盖 |
|---|---|---|
| `tests/infra/test_node_scanner.py` | 8 | AST_CLEAN 各种 dict 形式 / AST_DYNAMIC 函数调用 / NOT_FOUND / PARSE_ERROR / 读失败 / warnings 正确性 |
| `tests/services/test_node_service.py` | 6 | scan 流程 / set_disabled / folder_rename 模式 / placeholder node / 持久化 / 重复 scan 幂等 |
| `tests/services/test_conflict_service.py` | 5 | duplicate_class 检测 / version_mismatch / disabled 不参与 / 幂等性 / resolve + ignore |
| `tests/services/test_node_meta_service.py` | 4 | 缓存命中 / 强制刷新 / fetch 失败 / 1h TTL |
| `tests/infra/test_github_client.py` | 3 | 200 OK 解析 / 404 / 网络错(用 urllib 替身) |
| `tests/bridge/test_node_bridge.py` | 4 | requestScan emit / setDisabled / fetchRemoteMeta / 错误总线 |
| **小计** | **~30** | |

### 16.3 fixture 复用

`tests/conftest.py` 加：
```python
@pytest.fixture
def fake_env_with_nodes(db_conn, env_service, tmp_path) -> dict:
    """
    返回 {"env_id": str, "env_root": Path},env 已注册到 DB,
    custom_nodes/ 下放了 5 个不同形式的 fake 包。
    用于 NodeService.scan / ConflictService.detect 的端到端测试。
    """
    env_root = tmp_path / "env"
    (env_root / "custom_nodes").mkdir(parents=True)
    env = env_service.create(
        name="test_env", layout="shared", root=str(env_root),
        python_path="C:/python.exe", comfyui_source="",
    ).value
    env_id = env.id

    # 干净的字面量 dict
    (env_root / "custom_nodes" / "pkg_clean").mkdir()
    (env_root / "custom_nodes" / "pkg_clean" / "__init__.py").write_text(
        'NODE_CLASS_MAPPINGS = {"A": A, "B": B}\n'
    )

    # 动态(函数调用)
    (env_root / "custom_nodes" / "pkg_dynamic").mkdir()
    (env_root / "custom_nodes" / "pkg_dynamic" / "__init__.py").write_text(
        'NODE_CLASS_MAPPINGS = build_mappings()\n'
    )

    # 语法错
    (env_root / "custom_nodes" / "pkg_broken").mkdir()
    (env_root / "custom_nodes" / "pkg_broken" / "__init__.py").write_text(
        'def x(:\n'  # 语法错
    )

    # 没 NODE_CLASS_MAPPINGS
    (env_root / "custom_nodes" / "pkg_empty").mkdir()
    (env_root / "custom_nodes" / "pkg_empty" / "__init__.py").write_text(
        'X = 1\n'
    )

    # 跟 pkg_clean 同 class(冲突测试)
    (env_root / "custom_nodes" / "pkg_clash").mkdir()
    (env_root / "custom_nodes" / "pkg_clash" / "__init__.py").write_text(
        'NODE_CLASS_MAPPINGS = {"A": ClsA, "C": ClsC}\n'
    )

    return {"env_id": env_id, "env_root": env_root}
```

### 16.4 目标

- M0+M1 baseline：**157 passed + 2 skipped**
- M2 目标：**~185+ passed + 2 skipped**（新增 ~30）

---

## 17. 验收清单

### 17.1 功能性

- [ ] 启动 GUI,打开 EnvDetailPage, 自动触发 scan, 节点列表出现
- [ ] 节点卡片显示名字、版本、状态、⚠️ 角标(有 warning 时)
- [ ] 点击节点, 打开 NodeDetailPanel, 显示本地元数据
- [ ] NodeDetailPanel "查看远程信息" 按钮可拉 GitHub 数据
- [ ] 两个节点有同 class 时, ConflictPanel 出现 1 条 duplicate_class
- [ ] ConflictPanel "禁用其中之一" 可消除冲突
- [ ] ConflictPanel "忽略" 可灰显该条
- [ ] 设置页切换 `node_disable_mode=folder_rename`, 再禁用节点, 目录变 `<pkg>.disabled`
- [ ] folder_rename 失败时, 自动回退 db_flag + warning
- [ ] 关闭重开 GUI, 节点和冲突状态保留(从 DB 读)

### 17.2 工程性

- [ ] `poetry run pytest` ≥ 185 passed + 2 skipped
- [ ] pytest-qt 跑 NodeBridge 测试, CI 可用
- [ ] schema v3 迁移幂等(多次启动不报错)
- [ ] i18n:qsTr() 覆盖率 100%(M2 新增的字符串)
- [ ] M1 全测试不退化(157+2 全过)
- [ ] M0 CLI 仍可用

### 17.3 M1 回归

- [ ] 启动 / 停止 env 仍正常
- [ ] catalog 增删仍正常
- [ ] 设置页主题 / 语言切换仍正常
- [ ] M1 改造点(`AppContext` 加服务)不破坏现有 Bridge

---

## 18. 关键决策表

| 决策 | 选择 | 备选 | 理由 |
|---|---|---|---|
| 范围 | 严格 M1 延后 3 块 | +依赖树/搜索/版本 | YAGNI, 先跑起来 |
| 物理结构 | 每 env 独立 `custom_nodes/` | 共享 store + 硬链/软链 | 隔离性, 用户多 env 用例是测不同版本 |
| 冲突粒度 | 包 + 类 | 仅包 | 类冲突是 ComfyUI 生态最痛点 |
| import 方式 | AST 三层降级 | subprocess 启 venv | 用户要求静态; 安全 + 简单 |
| 启用/禁用默认 | DB 标志位 | folder_rename | 非破坏, 跟 git 友好 |
| folder_rename | 设置里 opt-in | 默认开 | 默认行为安全 |
| 扫描触发 | 懒(打开 EnvDetailPage) | 启动预扫 | 用户要求; 启动快 |
| 详情数据源 | 本地优先 + 在线 lazy | 预取 / 纯本地 | 离线可用 + 用户主动 |
| 协调机制 | EventBus 同步 | 直接调用 / Qt Signal | 简单, 同步足够 |
| meta cache PK | `package`(跨 env) | `UNIQUE(env_id, package)` | 缓存命中率高 |
| 冲突存储 | 不删行, `resolved_at` / `ignored` | 硬删 | 审计需要 |
| 节点类存储 | 嵌 nodes.class_mappings JSON | 单独 `node_classes` 表 | 只用于冲突, 不单独查询 |
| AST_DYNAMIC 处理 | 放弃 + warning | 展开 `{**X, "Y": Z}` | YAGNI; 5% 边角, 不投入 |
| 在线 fetch 重试 | 不重试 | 指数退避 | 简单; UI 提示用户手动重试 |
| 测试目标 | ~30 个新 test | 50+ | 覆盖核心, 不为测试而测试 |

---

## 19. 风险表

| 风险 | 概率 | 影响 | 缓解 |
|---|---|---|---|
| AST 解析在复杂包(多层 import)漏抓真实 class | 中 | 中 | UI 上 ⚠️ 角标 + warning 文案明确, 用户可手动看 |
| 用户在 ComfyUI 启动时把禁用节点也加载了 | 高 | 中 | M2 文档明确, M3+ 加 uninstall / pre-launch hook |
| folder_rename 模式破坏用户的 git 状态 | 中 | 中 | 默认 db_flag, opt-in 才用; 文档警告 |
| 启动时扫 50+ 包慢(>2s) | 低 | 低 | 懒扫描只触发一次, 不在启动; 后续 cache |
| GitHub API 限流(60/h anonymous) | 中 | 低 | 1h TTL + 手动按钮; UI 提示 |
| pytest-qt 在 CI 上 QApplication 起不来 | 中 | 中 | headless + offscreen(沿用 M1 方案) |
| M1 env 数据没 `custom_nodes/`, scan 0 节点 | 高 | 低 | 启动时 `mkdir` 兜底 |
| EventBus 同步 emit 在 emit 中再 emit 嵌套 | 低 | 中 | 服务内不 emit 自己的 listeners, 单测覆盖 |
| 节点数据被外部 mv 走, DB 残留 | 中 | 低 | 下次 scan 重新对齐; 暂不做实时监听 |

---

## 20. 术语表

- **Node / 节点**：ComfyUI 的 custom_node 包, 对应 `custom_nodes/<pkg>/` 一个子目录
- **Package**：节点的目录名(也是 PyPI/GitHub 包名, 可能一致可能不一致)
- **class_mapping**：ComfyUI 节点类名到 Python 类的映射(`NODE_CLASS_MAPPINGS` 字典的 key)
- **Conflict / 冲突**：两个或以上 enabled 节点提供同一 class, 或同 package 多目录注册
- **Scan**：扫 env 的 `custom_nodes/` 解析每个子目录, 写 nodes 表
- **Lazy scan**：打开 EnvDetailPage 才触发 scan, 结果缓存
- **EventBus**：进程内轻量事件总线, 用于服务间协调
- **AST static import**：用 `ast` 模块 parse `__init__.py`, 提取字面量 dict, 不实际执行 Python
- **db_flag 模式**：禁用 = 改 DB status, 不动文件系统
- **folder_rename 模式**：禁用 = 重命名目录为 `<pkg>.disabled`, ComfyUI 启动时跳过

---

## 21. 后续里程碑衔接

**M3 候选功能（基于 M2 基础）：**
- 节点版本管理（升级 / 回滚 / 版本锁定）
- 节点依赖自动解析（`missing_dep` 类型启用）
- 在线节点目录浏览（社区节点仓库）
- 远程管理（web 控制台）
- 节点文件监听（实时发现外部变化）
- AST_DYNAMIC 兜底（解析 `{**X, "Y": Z}`）

**M2 不影响 M1 用户的兼容性：**
- M1 的 env / catalog / process 全部不动
- M2 新表都是 IF NOT EXISTS, M1 数据 0 迁移
- AppContext 构造顺序微调, 但对外接口不变

---

## 22. M2 计划规模预估

预计 M2 plan 包含 **20-28 个 task**：

| 块 | 任务数 | 内容 |
|---|---|---|
| 数据层（2-3） | schema v3 迁移脚本 / 新增 3 表 / EventBus 单例 | 3 |
| 基础设施（3-4） | NodeScanner / GitHubClient / _parse_pyproject / _placeholder_node | 4 |
| Service 层（4-5） | NodeService.scan / set_disabled / ConflictService.detect/resolve/ignore / NodeMetaService | 5 |
| Bridge 层（2-3） | NodeBridge + 3 property / 5 slot / 错误总线 | 3 |
| AppContext（1） | 加 4 服务 + bus + 一次性 mkdir 迁移 | 1 |
| QML 组件（4-5） | ConflictPanel / NodeListItem / NodeDetailPanel / NodeScanBusy / EnvDetailPanel 改造 | 5 |
| 设置（1-2） | node_disable_mode 下拉 + meta_cache_ttl 默认值 | 2 |
| i18n（2） | pyside6-lupdate 抽 .ts + 翻译 30-40 条 | 2 |
| 测试（5-6） | 6 个测试文件 + fake_env_with_nodes fixture + 30 个 test | 5 |

总计：**~30 个 task**,比 M1 略少(因为没有 M1 那种 PySide6 基础设施 + Theme.qml 一次性投入)。

---

## 23. 与 M1 spec 的关系

- M1 spec 9.3 第 3-5 项延后清单:✅ M2 全部覆盖
- M1 spec 16 提到 M2 加 `ConflictBridge`:✅ 但 M2 改名为 `NodeBridge`(一个 bridge 管所有节点相关,ConflictBridge 太碎)
- M1 spec 9.3 第 3 项"节点勾选/启用的 UI":✅ NodeListItem + ConflictPanel 三动作
- M1 spec 9.3 第 4 项"节点详情面板":✅ NodeDetailPanel

**M1 spec 16 提到的"节点"标签页设计被 M2 推翻为嵌入式区域**,原因:EnvDetailPanel 已经是 split-view,加 tab 切换会增加认知负担;嵌入式更符合 M1 "详情页 = 一个 env 的所有信息" 的语义。如果用户更倾向 tab 化,M2 plan 阶段可调。

---

**Spec 结束。**
