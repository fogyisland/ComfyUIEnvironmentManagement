## v0.6.2 — Bundle git-portable + template path defaults

**v0.6.1 zip 不含 git-portable**(原始 build 时 `bin/` 是空的),本次补上 + 顺手优化 SettingsDefaults。

### 修复

- **Bundle git-portable 进 zip** (`f1734e9` + `787703c`) — 新增
  `scripts/fetch_git_portable.ps1`:从 `git-for-windows` releases
  下载 MinGit-*-64-bit.zip (官方 portable git, ~37 MB → 89.5 MB 解压),
  落到 `bin/git-portable/`,幂等(已存在 + --version 通过则跳过)。
  `scripts/build_release.ps1` 的 `[5/6]` 步骤从 `if present, copy`
  改成 `ensure + copy`:缺失自动 fetch,确保 release zip 永远含 git.exe。
  User 不再需要单独装 git。

- **SettingsDefaults 选择性默认值** (`77fb63c`) — 区分两类 path:
  - **template paths** (`TemplatePythonDir` / `TemplateComfyuiDir`):
    空字段填默认 `python` / `ComfyUI`(package root 下的子目录,
    `python/` 已有 portable Python;`ComfyUI` 留给 shared 源 clone)
  - **user-configured paths** (`EnvsDir` / `GlobalNodesDir`):
    默认保持空(用户主动管理,服务层在使用时报 `ENV_ENVDIR_NOT_CONFIGURED`)
  Apply 拆成两个 helper:`Resolve`(空填默认 + 迁移)
  和 `MigrateOnly`(只迁移,不填默认)。

### 包含的 commits since v0.6.1

```
564c481 chore(release): bump to v0.6.2
787703c fix(scripts): fetcher 改用 try/catch + 文件大小校验
f1734e9 feat(scripts): git-portable fetcher + auto-fetch on release build
77fb63c feat(wpf): template paths 默认填 package root 子目录名
```

### 升级注意

- 直接覆盖 v0.6.1 即可
- bin/git-portable/cmd/git.exe 是新加的(~89.5 MB),zip 解压后
  第一次启动会用 bundled git 而非 PATH
- 如果你之前的 settings.json 里 EnvsDir = "envs"(旧版默认值),
  不会被自动清除;需要手动去设置页清掉,下次 Create Env 才不报错
  (因为新规则是 EnvsDir 默认空 + 服务层校验)
- 如果 settings.json 里 TemplatePythonDir = "template-python"(旧版默认值),
  也不会自动改成 "python";需要手动改一下,否则 venv 创建时会找错路径

### 测试

- WPF: **45/45 PASS**(8 SettingsDefaults tests,含选择性默认)
- pytest: 181+ passed(含 3/3 version consistency 0.6.2 校验)

### 已知 carry-over

- 4 个 pre-existing M4 `_on_push_sync` silent-drop WS integration test
  仍 fail (non-blocking, M5.2 已删 Python WS server, 这些 test 现在
  无对应代码, 待 M6+ 清理)