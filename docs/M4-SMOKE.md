# M4 手动冒烟测试清单

> **目的**:M4 release 前用户手动 walkthrough 12 项功能,确认 WPF + Python service 端到端 OK。

## 前置条件

1. `git checkout v0.4.0`(或本地 `dotnet publish` 后的目录)
2. 解压 `ComfyUI-Manager-v0.4.0-win-x64.zip`
3. 双击 `ComfyUI Manager.exe`(不要点 `run.bat`,那是 M3 deprecated)
4. 等 3s,主窗口出现,无 Python 控制台窗口
5. 状态栏显示 "Connected"(WS 已连)

## 12 项检查

| # | 操作 | 期望 |
|---|------|------|
| 1 | 主窗口左侧"环境"按钮 | 显示 5 个测试 env(env-1 ~ env-5),默认都是 stopped 灰点 |
| 2 | 选 env-1,点"启动"按钮 | 状态点变绿,Process 显示 PID,3s 内 WS `envStarted` 推送 |
| 3 | 状态栏点"打开 ComfyUI" | 浏览器开 http://127.0.0.1:8188,看到 ComfyUI 主界面 |
| 4 | 选 env-1,点"停止" | 状态点变灰,WS `envStopped` 推送 |
| 5 | 节点目录页 → 搜索框输 "impact" | 实时刷新列表,1s 内显示相关条目 |
| 6 | 节点目录页 → 点"刷新" | toast 显示 "loaded N entries" |
| 7 | 节点目录页 → 选一条 → "安装到" 选 env-2 → "安装" | 进度条显示,完成后 env-2 节点列表多一项 |
| 8 | 设置页 → 语言下拉切到 "en_US" | 立即所有按钮文字变英文,无需重启 |
| 9 | 设置页 → TTL 输入 30 → 失焦 | `settings.json` catalog_cache_ttl_minutes = 30 |
| 10 | 节点详情 → VersionPanel → 锁按钮 | 锁图标亮,升级按钮灰,点升级 → "节点已锁定" |
| 11 | 关 WPF(右上 X) | Python service 子进程 5s 内退出,端口 7800 释放 |
| 12 | 重开 WPF | 自动恢复(env-1 ~ env-5 重新出现,状态保留) |

## 失败处理

- **Python service 没起来**:检查 `logs/server-error.log`,看 stderr
- **WPF 启动崩溃**:检查 `logs/wpf-crash.log`
- **WS 60s 无 _ping**:网络问题,重启 service
- **节点目录空白**:检查 `compat_api_base_url` 设置,可能 API 不可达

## 自动化等价测试

```bash
# scripts/smoke.sh 一键跑前 5 项
bash scripts/smoke.sh
```

手动 12 项是自动化覆盖不到的(需要真浏览器 / WPF 视觉验证)。