# ComfyUI Manager

ComfyUI 多环境管理桌面应用。

## 快速开始

```bash
poetry install
poetry run comfy-mgr-gui
```

或在 Windows 上双击 `start.bat`。

## 文档

- [Master spec](docs/superpowers/specs/2026-06-21-comfyui-manager-design.md)
- [M1 spec](docs/superpowers/specs/2026-06-24-m1-gui-design.md)

## 打包

```bash
python scripts/build_zip.py 0.1.0
```

输出：`dist/comfyui-manager-v0.1.0-win64.zip`，解压双击 `start.bat` 即可。

## 测试

```bash
poetry run pytest -v
```

## i18n

翻译源文件：`app/qml/i18n/comfyui_manager_*.ts`。
更新翻译后跑 `scripts/update_translations.bat` 编译 `.qm`。
