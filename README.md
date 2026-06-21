# ComfyUI Manager

ComfyUI 多环境管理工具。

## M0 状态：CLI 内核

M0 提供 CLI 命令管理 ComfyUI 环境、节点 catalog、设置。

## 安装

```bash
poetry install
```

## 使用

```bash
poetry run comfy-mgr --help
poetry run comfy-mgr env create --name my-env --layout shared --port 8188 --python C:/Python310/python.exe
poetry run comfy-mgr env list
poetry run comfy-mgr env start my-env
poetry run comfy-mgr env stop my-env
poetry run comfy-mgr catalog add https://github.com/ltdrdata/ComfyUI-Impact-Pack
poetry run comfy-mgr settings show
```

## 测试

```bash
poetry run pytest                    # 单元测试
poetry run pytest -m integration     # 集成测试（需 template/ 下的真实 Python）
```
