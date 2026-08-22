# ComfyUIManagement v1.0.0 — 首个正式发布

从 v0.6.22.x 演进而来,首个以 **ComfyUIManagement** 品牌命名的 1.0 版本。

## 重大变化

- **品牌:** `ComfyUI Manager` → **`ComfyUIManagement`**(zip 与目录命名变化,内部 product namespace 保持 `ComfyUI.Manager` 不变)
- **顶层目录结构重构:** 子目录统一 PascalCase 跟其它顶层目录一致(同 Python 命名约定):
  - `asset/` → `assets/`(icon / splash / 收款码)
  - `data/` → `Data/`(catalog-cache.db)
  - `logs/` → `Logs/`(应用日志 + per-env 操作日志)
  - `bin/` → `Embeded/`(git-portable)
  - `python/` → `Python/`(portable Python)
  - 卫星资源 DLL 移至 `languages/<culture>/`(原默认散在 `<exeDir>/<culture>/`,`AppDomain.AssemblyResolve` 钩子重定向查找)
  - 旧 settings.json 里写过的旧子目录名(`envs` / `local-nodes` / `workflows` / `models` 等)首次启动时自动迁移到 PascalCase,无需手工改
- **侧栏调整:** "工作流库" 与 "模型市场" 在 v1.0.0 暂时禁用(灰色 + ToolTip "将在后续版本提供"),其余 7 项保留
- **首次启动向导:** 全新 3 步配置向导(安装根目录 → Python 解释器 → 确认),仅在首次启动(安装根目录下 `.manager/.first-run-complete` sentinel 不存在)触发,完成后写 sentinel 不再重复;启动期 Splash `Topmost=false` 让位,避免盖在 wizard 上让用户误以为卡死
- **打包形式:** 绿色版 zip(无安装器),解压即用,自带 `uninstall.bat` + `install-start-menu.bat` 辅助脚本

## 运行时资源(已包含)

- 内置 portable Python(`Python/`)
- 内置 git(`Embeded/git-portable/`)
- 内置 ComfyUI 源模板(`ComfyUITemplate/`,v1.0.0+ 从 `ComfyUI/` 重命名,避免跟 per-env 安装的 ComfyUI 混淆)
- 多语言资源 DLL(`languages/<culture>/`,14 种 culture 含 `zh-CN` / `zh-Hans` / `zh-Hant` / `en-US` 等)
- 预填充节点详情缓存(`Data/catalog-cache.db`,约 5000+ 节点 + GitHub releases),首启即用
- 应用图标与启动图(`assets/`)

## 默认配置

- 所有目录默认指向所选安装根目录的子目录(`Envs/` / `Nodes/` / `LocalNodes/` / `Workflow/` / `Models/` 等,PascalCase)
- 所有 API Key 字段为空字符串(CivitAI / ModelScope / HuggingFace),用户按需在 Settings 填写
- 10 个 curated 常用节点已 seed(`Settings.CommonNodes`),首启即可勾选安装

## 已知限制

- 工作流市场 / 模型市场:UI 入口灰显,功能将在后续版本提供

## 下载

- `release/ComfyUIManagement-v1.0.0-win-x64.zip`