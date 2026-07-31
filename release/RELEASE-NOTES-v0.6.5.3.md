## v0.6.5.3 — 基础环境:多版本 PyTorch 选择

基础环境部署(BED)页从 v0.6.5.2 的"latest stable 5 profile + 1 nightly cu126"
扩成"顶部下拉选 torch 版本,ListBox 跟随刷 profile":

- **顶部 ComboBox** 在 `BaseEnvView` 顶部(profile ListBox 上方),列出
  PyTorch 全部 stable 版本 + 一个虚拟 nightly 项。
- 选中 stable 版本后,ListBox 立即刷出 5 个 stable profile
  (cu118 / cu121 / cu126 / cpu,4 个变体)+ version-aware 的
  `TorchVersion` 字符串,`BuildPipArgs` 会 pin `torch=={version}`。
- 选中 nightly 后,ListBox 只显示 1 个 nightly cu126 profile。
- **下拉默认** latest stable(按 `ReleaseDate` 倒序排)。
- **user override 优先**:若 `%APPDATA%/ComfyUI-Manager/base_env_profiles.json`
  存在,VM 走文件优先路径且隐藏 ComboBox(`IsUserOverrideActive` 控制可见性)。

---

### 1) 数据源:PyPI JSON + pytorch.org HTML 联合

| 字段 | 数据源 |
|---|---|
| 版本号 | PyPI JSON(`https://pypi.org/pypi/torch/json`) |
| 发布时间 | PyPI JSON(`upload_time` 字段) |
| CPU tag | PyPI JSON(wheel `filename` 含 `+cpu`) |
| CUDA 变体列表 | pytorch.org HTML(`https://pytorch.org/get-started/locally/`,从 `pt_version_map.release` 的 `cuda.x` / `cuda.y` / `cuda.z` 派生) |

> **为什么不只用 PyPI?** PyPI 的 `torch` 包只发 CPU wheel(2024 起
> 上游停发 CUDA 标记 wheel),CUDA 变体在 `download.pytorch.org/whl/{cuNNN}/`
> 各自独立的索引上。PyPI wheel filename 永远不出现 `+cu118/+cu126/+cpu`
> 等 tag,纯 PyPI 解析必然空。
>
> pytorch.org `pt_version_map` 是扁平结构:`{"cuda.x":..., "cuda.y":..., "cuda.z":...}`,
> `cuda.x` → `cu118` / `cuda.y` → `cu121` / `cuda.z` → `cu126` 映射
> 集中在 `PyTorchVersionCatalog.CudaLetterToTag` 常量字典里。

---

### 2) Cache:永久落盘,无 TTL

新增 `pytorch_catalog_cache.json`,跟老 `pytorch_versions_cache.json`
共存但不冲突:

| 文件 | 写者 | 路径 | 内容 |
|---|---|---|---|
| `pytorch_catalog_cache.json` | `PyTorchVersionCatalogCache` | `%APPDATA%/ComfyUI-Manager/` | 完整 stable 版本目录(每个版本号 + 发布日期 + CUDA 变体 + CPU tag) |
| `pytorch_versions_cache.json` | `PyTorchVersionCache` (v0.6.5.1 留) | `%APPDATA%/ComfyUI-Manager/` | 单 stable 版本号 + nightly cu126 标记 |

**永久 cache,无 TTL** — 拉过一次,本地一直用,除非手动删。升级 v0.6.5.2
或更早用户首次启动会拉一次 PyPI + pytorch.org,之后秒开。

---

### 3) 升级注意

- **直接覆盖 v0.6.5.2 文件即可。**
- 首次启动会拉一次 PyPI JSON(可能 1-2 分钟,响应体大),落
  `pytorch_catalog_cache.json`,之后秒开。
- 老 `pytorch_versions_cache.json` 不受影响(独立的 v0.6.5.1 cache)。
- **手动刷版本列表**:删 `%APPDATA%/ComfyUI-Manager/pytorch_catalog_cache.json`
  重启即可,会重拉 PyPI + pytorch.org(无 TTL,只能手动)。
- `<appDataDir>/base_env_profiles.json` 优先级最高(用户手动编辑的
  profile 文件),存在时 ComboBox 隐藏,只显示文件里的 profile。
- v0.6.5.2 的 5 个 hardcoded `pytorch-2.1` profile 不再展示
  (除非 user override 文件缺失且 PyPI 拉取失败 → fallback 到
  v0.6.5.2 字面量 5 个 stable + 1 nightly cu126)。

---

### 4) Verification

- **dotnet test WPF:** 273 PASS + 1 SKIP / 0 FAIL
  (基线 211 (v0.6.5.2) → 254 (T1-T4) → 258 (T4 R1 fix) → 262 (T4.5
  pin) → 272 (T5 user-override priority) → 273 (T7 AppWiringTests))
  - 1 SKIP 是 pre-existing live GitHub network opt-in test。
- **dotnet build Release:** 0 warnings, 0 errors
- **pytest version consistency:** 3 PASS(v0.6.5.2 → v0.6.5.3)
- **Manual GUI smoke (TBD 用户桌面):** 启动 → 基础环境页 → 顶部
  ComboBox → 选 stable / nightly → ListBox 跟随刷 → 选 profile + env
  → 开始部署 enabled。删除 `pytorch_catalog_cache.json` 重启应重拉。
  断网时 fallback 到 v0.6.5.2 5 个 hardcoded + nightly cu126。

---

### 5) Commits since v0.6.5.2(`376575d`)

```
e7d131f docs(sdd): specify multi-version BED selection
893aa08 feat(wpf): parse PyTorch versions from PyPI + pytorch.org
1168b03 feat(wpf): parse PyTorch versions from PyPI           (废弃保留)
3e094d7 feat(wpf): add permanent PyTorch catalog cache
e57cdaa feat(wpf): add PyTorch version directory
4edf411 feat(wpf): generate BED profiles per PyTorch version
39a6848 fix(wpf): correct CUDA label mapping in version-aware profiles
808ab4d feat(wpf): pin torch version in stable profiles
e13d754 feat(wpf): add PyTorch version selection to BED view model
255162c feat(wpf): add PyTorch version selector to BED view
2894a8b feat(wpf): wire PyTorch version directory into BED
```

---

### 已知 carry-over / 未做事项

- **未在本 session 完成:** tag `v0.6.5.3` push + `gh release create` +
  rebuild release zip — 由用户在下一 session 续做(待用户明确授权)。
- **PipArgs nightly 不 pin**:`Channel == "nightly"` 或空 `TorchVersion`
  时 `BuildPipArgs` 保持原行为(裸 `torch`),因为 nightly 没有固定
  版本号可 pin。
- **PyPI JSON 解析大小**:PyPI JSON 响应 ~2 MB,首次拉取会慢;cache
  命中后无影响。

---

### Lessons learned(SDD)

- **永远 verify 数据源 against real sample,不要信 spec 的想象结构** —
  原 T1 spec 假设 PyPI wheel filename 里有 CUDA tag,实际 PyPI 不发
  CUDA 标记 wheel(只有 CPU);v0.6.5.3 联合数据源是踩过坑后的修正。
- **pytorch.org HTML 是扁平 key,不是嵌套**:`pt_version_map.release`
  的 `cuda.x` / `cuda.y` / `cuda.z` 是 sibling key,不是
  `{"cuda":{"x":...}}` 嵌套;`PyTorchVersionCatalog.ReleaseBlockRegex`
  反映真实结构。
- **Test seam 优先用 `virtual` method,不要生造 interface**:
  T3 / T5 把 `sealed` 移除 + 把 `GetAllAsync` 标 `virtual`,
  测试子类直接 override 即可,无需为单一 testable seam 引入接口。
