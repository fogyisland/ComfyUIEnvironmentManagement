# v0.6.15 Spec: 本地节点菜单(下载到本地 + 从本地安装到 env)

> **For agentic workers:** This is a design spec. Read once before writing the implementation plan; do not modify without user approval.

## 1. Goal

解决 v0.6.7.9 + v0.6.10 之后用户仍要"先把节点 git clone 到本地 → 再手动复制到 env custom_nodes"的两步麻烦:

1. **下载入口不明**:用户必须从 Catalog 页 / env-list 行内"安装节点"按钮走安装对话框,无法"先下到本地备着再说"。v0.6.7.9 已加 `Settings.LocalNodeDirectory` + `NodeOperations.DownloadAsync(localDir, ...)` + `Source="download"` 的 ScannedNode,但**没有 UI 入口**让用户主动调它 → 该功能装机量近 0。
2. **复制路径手动**:用户下载到本地后,要把节点复制到 env 的 `custom_nodes/` 还得手动 cp / 文件管理器。"已下载"和"已安装"两个状态之间的桥是断的。
3. **多 env 共享本地节点**:同一个本地节点常希望装进多个 env(开发 + 生产),目前要走多次 git clone,N× 网络时间。

**本 spec 要做的**:
- 侧栏新 tab **"本地节点"** 进入,展示 `LocalNodeDirectory` 下的所有节点卡(card)
- 每张卡片给操作:① **复制到 env**(选 env 弹 env picker → 复制目录到 env 的 `custom_nodes/`)② **删除本地节点**(清目录 + ScannedNode row)
- 卡片显示 **跨 env 安装状态**:badge "已装: env-A, env-B" (从 `scanned_nodes` WHERE `Source="download"` OR `Source="github"` 查;来源是本地节点的去重)
- Catalog 页 + env-list 行内"安装节点"按钮都加 **"下载到本地"** 入口(复用现有 `DownloadAsync`)

**用户原话**:
> "增加一个本地节点菜单,我们通过节点目录点击下载就下载到节点目录中。我们也可以在节点目录中直接点击安装安装选择对应的环境进行安装"
>
> "下载到本地之后,可以从本地装到任意 env"

**Non-goals(本 spec 不做)**:
- 不实现"已经装进 env 的节点反向同步回本地"(等用户要"我换电脑能直接装回来")
- 不实现"跨机器同步本地节点"(云端、云盘加密都不做)
- 不实现"本地节点版本管理"(git pull / checkout tag 等完全由 user 自己在本地目录操作)
- 不实现"安装时自动解决冲突"(目录已存在 → 弹"已存在,覆盖?" 二次确认)
- 不 bump version / 不发 release zip(per hotfix 偏好)
- 不复用 `InstallDialog` 第二步 dialog(那 dialog 假设来源是 git clone,跟"从本地复制"路径无关)

## 2. Background

### 2.1 v0.6.7.9 现状(可复用基础)

- **`Settings.LocalNodeDirectory`**(`src-wpf/ComfyUI.Manager/Settings.cs`):default `<projectRoot>/local-nodes/`,Settings tab 已 wired CheckBox + Browse 按钮。
- **`NodeOperations.DownloadAsync(localDir, nodeId, repoUrl, targetTag?, ct)`**(`Services/NodeOperations.cs:240`):git clone `repoUrl` → `localDir/nodeId/`,可选 `checkout targetTag`,然后 `_nodeRepo.Upsert(new ScannedNode { EnvId="", Source="download", ... })`。EnvId="" 是 sentinel,标记非 env-specific。
- **`NodeOperations.InstallAsync(envId, nodeId, repoUrl, ...)`**(`Services/NodeOperations.cs:88`):git clone 到 env `custom_nodes/`,**只走 git** — 复制路径未实现。
- **`ScannedNode`**:行 `Source` 字段 `"github"`(env 装的) / `"download"`(本地下载的),`EnvId` 字段 env 装的是 env_id,本地下载是 `""`。
- **`NodeRepository.ListByEnv(envId)`**:返回 env 装的所有节点(按 `Source` 字段不区分)。
- **侧栏 sidebar**(`MainWindow.xaml`):RadioButtons 6 个 tab — 主页 / 环境 / 节点目录 / 设置 / 批量更新 / 系统状态。`节点目录` 实际是 `CatalogView`(从 catalog 列表选入口),v0.6.7.9 后本地节点没独立 tab。

### 2.2 用户预期

- **侧栏 1-click 入口**:点 "本地节点" tab 立即看到本地所有节点卡片,扫盘 < 500ms。
- **复制到 env 一气呵成**:点卡片 → 弹 env picker → 选 env → 复制完成 → 卡片 badge 追加 env 名。全程 modal 进度可看。
- **状态自洽**:本地节点 = `LocalNodeDirectory` 下有物理目录 + `scanned_nodes` 表有 `Source="download"` 行(两个独立校验)。列表展示时:`Source="download"` OR 物理目录存在 → 显示。主键 = `nodeId`(同 DownloadAsync 的写入路径)。
- **删除本地节点**:按钮 + 二次确认 → 删目录 + DELETE `scanned_nodes` WHERE `EnvId="" AND Source="download" AND Id=?`。**不影响**已装到 env 的 row(`EnvId` 非空,不会被删到)。

### 2.3 用户已选设计决策

| 决策项 | 选项 | 选定 |
|--------|------|------|
| 装到 env 用什么模式 | A.复制到 env / B.junction 软链 / C.git clone(重下) | **A.复制** |
| 下载入口放哪 | A.侧栏新 tab / B.嵌入 Catalog 页 / C.嵌入 env-list 行内 | **A.侧栏新 tab + B.Catalog 页加 1 列** |
| 安装状态展示 | A.badge "已装: env-A, env-B" / B.每行 状态按钮 / C.只显示"已装" | **A.badge** |

## 3. Design

### 3.1 架构(组件 + 职责)

| 组件 | 类型 | 职责 |
|------|------|------|
| `Services/LocalNodeService.cs` | **新** (~120 行) | `ListAsync()` 扫 `LocalNodeDirectory` 子目录 + join `scanned_nodes` Source="download" → `List<LocalNodeInfo>`;`DeleteAsync(nodeId)` 删目录 + row |
| `Services/LocalNodeCopyInstaller.cs` | **新** (~80 行) | `InstallAsync(envId, sourcePath, nodeId, ct)` `Directory.Copy` + TargetDir 存在 → Fail;写 ScannedNode `Source="github"`,`EnvId=<envId>`;rollback on fail |
| `Models/LocalNodeInfo.cs` | **新** (~30 行) | record `NodeId` / `DisplayName` / `HasPhysicalDir` / `IsInDb` / `InstalledEnvIds` (cross-env list) / `HeadSha` / `InstallDate` |
| `ViewModels/LocalNodeListViewModel.cs` | **新** (~150 行) | `Items` ObservableCollection + `SelectedItem` + `InstallCommand` (param LocalNodeInfo) + `DeleteCommand` + `RefreshCommand` |
| `ViewModels/LocalNodeListItem.cs` | **新** (~50 行) | INPC wrapper: `Info` + `InstalledEnvNames` (string 拼) + `BadgeText` + `BadgeKind` |
| `ViewModels/EnvPickerDialogViewModel.cs` | **新** (~60 行) | `EnvList` ObservableCollection + `SelectedEnv` + `OkCommand` |
| `Views/EnvPickerDialog.xaml` + `.xaml.cs` | **新** (~80 行) | ListBox env + Ok/Cancel 按钮,`Show(title, envs) → EnvInfo?` |
| `Views/LocalNodeListView.xaml` + `.xaml.cs` | **新** (~150 行) | 卡片布局 + 每行 BadgeBlock + 2 按钮(复制到 env / 删除) |
| `ViewModels/CatalogViewModel.cs` | **修改** | 每行加 "下载到本地" 按钮(复用 `DownloadAsync`);已下载节点行显示 "已本地下载" badge |
| `App.xaml.cs` | **修改** | DI 注册 `LocalNodeService` / `LocalNodeCopyInstaller` |
| `MainWindow.xaml` | **修改** | sidebar 加 RadioButton "本地节点" + ContentControl 绑定 `LocalNodeListView` |
| `MainViewModel.cs` | **修改** | 加 `LocalNodeListVM` property + 加载 |

**关键约束(从 v0.6.9.2 lesson):**
- 任何 WPF `Setter` 引用 DynamicResource(Theme.xaml 资源)→ **必须用 property-element 写法**(`<Setter.Property><DynamicResource .../></Setter.Property>`),不能 `Setter Value="{DynamicResource ...}"` — v0.6.9.2 教训会导致 "无法找到资源" XAML 异常
- 新加的 UserControl / Window 跑 STA-test 验 headless load
- DB 列不动 — `scanned_nodes` 已有 `Source` / `EnvId` / `Id` 字段够用

### 3.2 数据模型

```csharp
// Models/LocalNodeInfo.cs
public sealed record LocalNodeInfo
{
    public required string NodeId { get; init; }
    public string DisplayName { get; init; } = "";   // 包名 / 目录名 fallback
    public string? HeadSha { get; init; }             // 物理目录 head SHA (git rev-parse HEAD)
    public DateTime? InstallDate { get; init; }      // ScannedNode.InstallDate 字段
    public bool HasPhysicalDir { get; init; }        // 磁盘上 <LocalNodeDir>/<NodeId> 存在
    public bool IsInDb { get; init; }                // scanned_nodes 有 Source="download" row
    public IReadOnlyList<string> InstalledEnvIds { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> InstalledEnvNames { get; init; } = Array.Empty<string>();
}
```

**`InstalledEnvIds` / `InstalledEnvNames` 来源**:
- Step A:`SELECT pkg.name FROM env_id_or_name` — 单 SQL: `SELECT package, env_id FROM scanned_nodes WHERE package = @nodeId` 不够,因 `Source="download"` 行的 `Id` = `nodeId` 但 env 装的 `Source="github"` 行 `Id` 不同于 `nodeId`(env 装的也是 git clone,目录名 = `nodeId` 是巧合 — ComfyUI 节点目录约定)
- **策略**:本地节点 `nodeId` 跟 env 装的 `nodeId` **不保证一致** — env 装的是 `git clone <repoUrl>` 后用 `repoUrl` 末段作目录名(`Path.GetFileName(repoUrl.TrimEnd('/').Replace('.git',''))`),本地下载也一样。但 `package` 字段在 `scanned_nodes` 都是 `nodeId`(`NodeOperations` 写库的 `Package = Path.GetFileName(repoUrl)`)
- **修法**:跨 env 查 `scanned_nodes` 用 `package = ?` 不用 `id = ?`:
  ```sql
  SELECT env_id, id FROM scanned_nodes
  WHERE package = @nodeId
    AND env_id != ''
    AND source = 'github'
  ```
- `InstalledEnvNames` 再 join `environments` 表 `WHERE env_id IN (...)` 拿 display name

### 3.3 入口位置

#### 3.3.1 主入口:侧栏新 tab "本地节点"

`MainWindow.xaml` sidebar 加 RadioButton `Content="本地节点"` + GroupName="MainView" + Command binding `MainViewModel.LocalNodeListCommand`。

`MainViewModel` 加 `LocalNodeListVM` property + `LocalNodeListCommand` 切到该 view + ContentControl 绑定到 `LocalNodeListView`(已 wired 其他 tab 同 pattern)。

#### 3.3.2 次入口:Catalog 卡片加 "下载到本地" 按钮

`CatalogView.xaml` 每行操作列已经在 v0.6.7.9 加了 "下载" 按钮(直接调 `DownloadAsync`),但**只在下载成功时写 ScannedNode**。本 spec 要加:
- 已下载的 entry 在卡片上加 badge "已本地下载"(直接 ignore,跟 v0.6.7.9 行为一致 — 状态从 `scanned_nodes` 查)
- 已下载的 entry "下载" 按钮变 disabled + 文案 "已下载"

#### 3.3.3 副入口:env-list 行内不直接调(避免再加按钮)

env-list 行内操作列已经 10 按钮(v0.6.10.2 后),再加第 11 按钮会让卡片翻倍。**不**在 env-list 加 下载入口,用户从 Catalog 走。或从侧栏 "本地节点" tab 先下载。

### 3.4 Install flow(本地节点 → env)

```
[User clicks "复制到 env" on local-node card]
        ↓
EnvPickerDialog.Show(title="安装 <nodeId> 到哪个 env?", envs=...)
        ↓
User selects env-A → clicks OK
        ↓
LocalNodeCopyInstaller.InstallAsync(envId=<env-A>, sourcePath=<LocalNodeDir>/<nodeId>, nodeId=<nodeId>, ct)
        ↓
1. Check env exists (env_repo.Get(envId))
        ↓
2. Compute targetDir = <env.CustomNodesPath>/<nodeId>
        ↓
3. If targetDir exists → Fail("目录已存在: <path>")
        ↓
4. Directory.Copy(sourcePath, targetDir, recursive: true)
        ↓
5. Try read head sha from targetDir (git rev-parse HEAD → ScannedNode.Version)
        ↓
6. _nodeRepo.Upsert(new ScannedNode {
       Id = nodeId,
       EnvId = envId,
       Package = nodeId,
       Source = "github",
       Version = headSha,
       Status = "enabled",
       InstallDate = DateTime.UtcNow,
   })
        ↓
7. Return Success
        ↓
On exception:
   - TryDelete(targetDir)
   - Return Failure(msg)
        ↓
[LocalNodeListViewModel receives Success]
        ↓
Update LocalNodeInfo.InstalledEnvIds += envId
   - Re-fetch env name from env_repo
   - Update BadgeText in INPC
        ↓
[User sees badge "已装: env-A" on the card]
```

### 3.5 Delete flow(本地节点)

```
[User clicks "删除" on local-node card]
        ↓
ConfirmDialog: "确认删除本地节点 <nodeId>? 已装到 env 的副本不删。"
        ↓
User clicks OK
        ↓
LocalNodeService.DeleteAsync(nodeId, ct)
        ↓
1. Check nodeId exists in physical dir
   - If !Directory.Exists(LocalNodeDir/<nodeId>) AND !IsInDb → Fail("本地节点不存在")
   - If only IsInDb (orphaned DB row) → just delete row
   - If both → delete both
        ↓
2. if Directory.Exists(LocalNodeDir/<nodeId>) → TryDelete recursive
        ↓
3. DELETE FROM scanned_nodes WHERE EnvId == '' AND Source == 'download' AND Id == nodeId
        ↓
4. Return Success
        ↓
[LocalNodeListViewModel receives Success]
        ↓
Remove item from Items OR re-fetch list
```

**关键不变量**:只删 `Source="download"` + `EnvId=""` 的行(env 装的 `Source="github"` 不受影响,因为 `WHERE EnvId == ''` 过滤)。

### 3.6 错误处理

| 失败模式 | 处理 |
|----------|------|
| `LocalNodeDirectory` 未配置 | `LocalNodeService.ListAsync()` 返 `IReadOnlyList<LocalNodeInfo>` empty + `LocalNodeListViewModel.IsEmpty=true` + empty-state card "请在 Settings 配置本地节点目录" |
| `LocalNodeDirectory` 路径不存在 | `Directory.CreateDirectory(localDir)` 兜底 + 返 empty list |
| 物理目录存在但 DB 无 row | `IsInDb=false HasPhysicalDir=true` → 卡片仍显示 + "下载"按钮 enable(但实际应禁用 — **修法**:这种"孤儿目录"在设置里给按钮 "重新入库" 走 `_nodeRepo.Upsert(Source="download")` 路径) |
| 复制到 env 目标存在 | `Fail("目录已存在: <path>")` + UI 弹 ErrorBanner。**不自动覆盖**(用户原话"目录已存在"是合理的) |
| 复制过程中磁盘满 / 权限 | `Directory.Copy` throws → catch → `TryDelete(targetDir)` → `Fail(msg)` |
| 复制后写 ScannedNode 失败(DB locked) | 同上 → rollback → `Fail(msg)` |
| git rev-parse 失败(SHA 拿不到) | `Version = ""` + 不抛(允许本地目录非 git 仓库) |
| 用户取消(cancel token) | `OperationCanceledException` 全程 propagate → UI dismiss dialog |
| 并发点击同一卡片 2 次 | `LocalNodeListViewModel.Dictionary<NodeId, BusyKind>` mutex + `IsBusy(nodeId)` gate + 按钮 `IsEnabled = !IsBusy(nodeId)`(v0.6.5.22 同款 pattern) |

### 3.7 测试 + 验证

**新增测试 (~30,baseline 1071 → 期望 ~1101 PASS / 0 FAIL / 1 SKIP):**

| Test 类 | 数量 | 关键覆盖 |
|---------|------|----------|
| `LocalNodeServiceTests`(新) | 8 | `ListAsync` 空目录;单目录无 DB row;单目录 + DB row;多目录 + 跨 env 装记录;`DeleteAsync` 删目录 + row;删孤儿 DB row;并发 `ListAsync` 不死锁 |
| `LocalNodeCopyInstallerTests`(新) | 6 | 成功路径(目录复制 + ScannedNode 写 + SHA 抓);目标已存在 → Fail;源目录 missing → Fail;env not found → Fail;复制中途异常 → rollback;并发同 node → 互斥 |
| `LocalNodeListViewModelTests`(新) | 5 | `Refresh` 加载 + 显示 badge;`InstallCommand` 弹 env picker + 调 installer + 刷新 badge;`DeleteCommand` 二次确认 + 删 + 移除 item;env picker 取消 → 不动 badge;busy mutex 拦第二次点 |
| `EnvPickerDialogViewModelTests`(新) | 3 | env list bound;选中 + OK → 返回 env;Cancel → 返回 null |
| `EnvPickerDialogLoadTests`(新,STA) | 1 | headless load 无 XAML 异常 |
| `LocalNodeListViewLoadTests`(新,STA) | 1 | headless load 无 XAML 异常 |
| `CatalogViewIntegrationTests`(扩展) | 2 | 新加 "下载" 按钮 wired CallDownloadCommand;已下载 entry 按钮 disabled(读 `IsInLocalNodeDb`) |
| `AppStartupTests`(扩展) | 1 | DI 注入 `LocalNodeService` + `LocalNodeCopyInstaller` 不为 null |

**测试设施:**
- `FakeNodeOps` 扩展:`InstallFromLocalAsync(envId, sourcePath, nodeId, ct)` mock + `CapturedCopySource` 字段
- `LocalNodeServiceTestHelper`:`EmptyLocalNodeDir()` / `WithLocalNodeDir(...subdirs)` / `WithScannedNodeRow(...)`
- `FakeEnvPickerDialog` override `Show(...)` → 返 `EnvInfo` 或 `null`,不用真 WPF dialog(test seam)
- 并发测试用 `Task.WhenAll(t1, t2)` 验证 mutex 短路

**集成 + 回归:**
- 端到端集成测试:创 test 目录 + DB row → `LocalNodeCopyInstaller` 跑 → 验目标目录 + DB row
- **1071 existing tests 必须继续 PASS**(增量修改,无 breaking)
- **回归 v0.6.7.9**:`DownloadAsync` 路径仍能跑(用户从 Catalog "下载" 按钮)
- **回归 v0.6.5.22**:`EnvironmentListViewModel` busy mutex 模式直接复用,不重写

### 3.8 迁移路径

**Schema 迁移**:**无 — `scanned_nodes` 表已有 `Source` / `EnvId` / `Id` 字段,直接用。**

**首次 v0.6.15 用户桌面 staging 验:**
1. 旧 v0.6.14 staging 启动 → DI 注册 `LocalNodeService` + `LocalNodeCopyInstaller` 失败 → 启动崩溃 → **fail-fast 设计:** `App.xaml.cs` 用 `try { ... } catch { showErrorAndExit(); }` 包注册 + 启动管线
2. 旧 DB 用现状:`scanned_nodes` 旧 `Source="download"` 行(**v0.6.7.9 用户实测已下载过**) → `LocalNodeService.ListAsync` 第一次扫盘自动 join 这批 row,badge 正确显示
3. 用户操作:点侧栏 "本地节点" → 看到已有下载列表 → 选一个 → 点 "复制到 env" → 弹 env picker → 选 env → 复制完成 → 卡片 badge "已装: env-A"

**降级路径**(用户回到 v0.6.14):
- v0.6.14 不识 `LocalNodeService` / `LocalNodeCopyInstaller` 类 → 编译失败 → 用户必须 v0.6.15 旧 binaries 保留,**不需要降级补丁**
- DB 无 schema 变更,旧 v0.6.14 启动不报错,但 `LocalNodeService` 不存在 → App.xaml.cs DI 缺失 → 跟首次迁移同样的 fail-fast

## 4. Carry-forward (deferred, not in scope)

以下不在本 spec,留 v0.6.16+:

- **C1**: 下载到本地节点支持 github CLI / gh release 二选一(目前只 git clone)
- **C2**: 本地节点支持 git pull 升级(按钮 "升级" 调 `git pull` 在 `LocalNodeDir/<nodeId>`,可能冲突)
- **C3**: 本地节点导出 manifest(JSON 列出本地全部 nodeId + sha)做跨机器迁移
- **C4**: "已在 env 安装 → 反向同步到本地" 按钮(env 装的从 git clone 一份到 LocalNodeDir)
- **C5**: 本地节点支持符号链接 / hardlink 而非 Directory.Copy(GB 量级节点复制慢)
- **C6**: 全局默认 ModelsDirectory 已 v0.6.10 实现,本地节点默认目录类似延续 v0.6.7.9 行为(不动)
- **C7**: 本地节点视图加搜索/分页(节点少时 YAGNI,几百个本地节点再考虑)

## 5. Open questions

无 — 3 个原 open question 全部通过方案 A 决议(本 spec):

| 原 Q | 决议 |
|------|------|
| 装到 env 用什么模式? | **A.复制**(`Directory.Copy` recursive,本 spec 选) |
| 下载入口放哪? | **A.侧栏新 tab + B.Catalog 页加 1 列** |
| 安装状态展示? | **A.badge "已装: env-A, env-B"** |

## 6. 验收标准 (Acceptance Criteria)

- [ ] **AC-1**: 侧栏多 "本地节点" tab,点入立即看到本地节点列表(空目录 + empty-state card 提示)
- [ ] **AC-2**: 本地节点卡片显示:节点名 / HEAD SHA / 安装时间 / 跨 env badge "已装: env-A, env-B"
- [ ] **AC-3**: 卡片 "复制到 env" 按钮 → 弹 env picker → 选 env → 复制目录到 env `custom_nodes/` 完成 → 卡片 badge 追加 env 名
- [ ] **AC-4**: 卡片 "删除" 按钮 → 二次确认 → 删目录 + 删 `scanned_nodes` Source="download" EnvId="" row → 已装 env 副本不受影响
- [ ] **AC-5**: Catalog 页每行 "下载" 按钮 → 已下载节点 disabled + 文案 "已下载" + 状态展示
- [ ] **AC-6**: 跨 env 状态查询走 `scanned_nodes WHERE package = ? AND env_id != '' AND source = 'github'`,badge 准确
- [ ] **AC-7**: 复制过程中异常 → rollback(目录删 + 不写 ScannedNode)+ 弹 ErrorBanner
- [ ] **AC-8**: 本地节点 + 跨 env 操作全程不抛 unhandled exception,失败路径全部走 `Fail(...)` + UI 提示
- [ ] **AC-9**: 1071 existing tests + ~30 new tests = ~1101 PASS / 0 FAIL / 1 SKIP(1 pre-existing flake 不回归)
- [ ] **AC-10**: GUI smoke 桌面 staging 验 6 步:
  1. **启动**:打开侧栏 "本地节点" → 看到 empty-state "请先下载" + "请在 Settings 配置本地节点目录" 提示
  2. **下载**:切到 Catalog 页 → 选 1 个 entry → 点 "下载" → 进度出现 → 完成后切回 "本地节点" tab → 看到新卡片
  3. **复制到 env**:点新卡片 "复制到 env" → 弹 env picker → 选 env-A → 进度出现 → 完成后 badge "已装: env-A"
  4. **跨 env 复制**:同一节点再 "复制到 env" → 选 env-B → 完成后 badge "已装: env-A, env-B"
  5. **删除本地**:点 "删除" → 二次确认 → OK → 卡片消失 + env-A/env-B 的副本不受影响(env-list 行内节点未变)
  6. **降级检查**:v0.6.14 老二进制仍能跑(无 schema 变更,只缺服务注册 → 直接编译失败,不需要降级)

(前提:`Settings.LocalNodeDirectory` 已设,本地节点状态从 `scanned_nodes` 查,无新 schema 依赖)
