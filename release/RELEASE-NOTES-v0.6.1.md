## v0.6.1 — Post-release hotfixes (8 commits)

**架构同 v0.6.0** (M5.2 WPF 完全独立, 无 Python control service)。
本次只修 v0.6.0 之后手动 smoke 发现的 8 个 WPF 端问题。

### 修复

- **Env Create 流程补齐** (`dd5458b`) — M5.2 删 Python `services/environment.py`
  后 WPF 端没 Create path, env 列表永远为空; 新增
  `VenvCreator` / `JunctionLinker` / `EnvCreatorService` / `CreateEnvDialog`,
  EnvironmentListViewModel 加 `CreateCommand`。

- **Settings 页字段扩展** (`bb5d0ff`) — 原只有 theme/lang/TTL/API URL,
  补 8 个字段 (paths / git exe / proxy URL/port / extra paths) + Browse 按钮 +
  ExtraPaths 表。SettingsViewModel 加 `PickFolder` / `PickFile`
  (Microsoft.Win32 .NET 8)。

- **Git 代理 — 显式 checkbox** (`497a817`) — 国内访问 GitHub 困难,
  代理是常用功能; 新增 `Infrastructure/GitProxyConfig.cs`,
  `Enabled` + `Url` + `Port` + `ApplyTo(psi)` 注入
  `HTTP_PROXY` / `HTTPS_PROXY` 到 `ProcessStartInfo.EnvironmentVariables`。
  **scope 严格 per-process** — 只影响本应用的 git 子进程, 不污染 WPF
  或系统环境。GitRunner / BulkUpdateOrchestrator 接受 `GitProxyConfig?`
  可选 ctor param。11 个新测试覆盖 enabled/disabled/empty URL/
  invalid port/scheme pass-through + JSON round-trip。

- **Settings paths 默认填子目录** (`311970d`) — 首次启动把 4 个 path
  字段默认填为程序目录下的子目录名 (template-python / ComfyUI / envs /
  global-nodes)。

- **删 WPF 启动时的 Venv 预检查 modal** (`dda64f3`) — 删
  `VenvVerifier.cs` (dead code, 调用方只有 App.xaml.cs), WPF
  启动不再触发 "python.exe 不存在" 弹窗。

- **Settings paths 改相对路径 + EnvCreatorService takes projectRoot**
  (`5313333`) — 不再把绝对路径写进 settings.json, 用相对子目录名;
  跨机器 / 跨盘符时 settings.json 不需重新生成。
  EnvCreatorService 接受 `projectRoot` 参数, 运行时把 settings.EnvsDir
  解析到绝对路径。

- **SettingsDefaults 不再自动填默认值** (`e877812`) —
  SettingsDefaults.Apply 只做绝对路径 → 相对路径迁移,
  空字段保持空 (不再 "凭空填" 默认子目录名)。
  EnvCreatorService 在 EnvsDir 为空时抛
  `CreateEnvException("ENV_ENVDIR_NOT_CONFIGURED")`, 提示用户去设置页填。

### 测试

- WPF: **44/44 PASS** (v0.6.0 的 26 + 11 GitProxyConfigTests + 7 SettingsDefaults 重写)
- pytest: 181+ passed (含新 3/3 version consistency 0.6.1 校验)

### 升级注意

- 直接覆盖 v0.6.0 即可, settings.json 路径字段会被自动迁移
  (绝对路径在 programRoot 下的会转相对, 其它保留)
- 首次启动若 EnvsDir 为空, 尝试 Create Env 时会报
  `ENV_ENVDIR_NOT_CONFIGURED` — 去设置页填一个目录再试

### 包含的 commits (since v0.6.0)

```
ae7a70b chore(release): bump to v0.6.1
e877812 feat(wpf): SettingsDefaults 不再自动填默认值,空路径由服务层校验
5313333 feat(wpf): relative settings paths + EnvCreatorService takes projectRoot
dda64f3 feat(wpf): relative settings paths + drop startup venv pre-check
311970d feat(wpf): default Settings paths to program-root subfolders
edb0314 docs(sdd): note 3 post-release hotfix commits + v0.6.0 vs v0.6.1 question
497a817 feat(wpf): git proxy — explicit enable checkbox + per-process env vars
bb5d0ff feat(wpf): extend Settings — paths, git exe, proxy URL/port, extra paths
dd5458b feat(wpf): M5.2 hotfix — Env Create flow on WPF side
```

### 已知 carry-over

- 4 个 pre-existing M4 `_on_push_sync` silent-drop WS integration test
  仍 fail (non-blocking, M5.2 已删 Python WS server, 这些 test
  现在无对应代码, 待 M6+ 清理)
- v0.6.0 已 released 但 zip 不含上述 hotfix — 本次 v0.6.1 修复该问题