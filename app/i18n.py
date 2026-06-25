"""i18n 加载助手。"""
from __future__ import annotations
from pathlib import Path
from PySide6.QtCore import QTranslator, QCoreApplication, QLocale


def make_translator(app: QCoreApplication, language: str) -> QTranslator | None:
    """根据 language code 加载 .qm 翻译文件。
    返回 QTranslator 实例（已 install 到 app）；找不到返回 None。
    """
    if not language:
        return None
    qm_dir = Path(__file__).parent / "qml" / "i18n"
    # 注意：.qm 需要用 pyside6-lrelease 编译 .ts 生成。M1 用 .ts 占位也行（Qt 接受）
    qm_path = qm_dir / f"comfyui_manager_{language}.qm"
    ts_path = qm_dir / f"comfyui_manager_{language}.ts"
    actual = qm_path if qm_path.exists() else (ts_path if ts_path.exists() else None)
    if not actual:
        return None
    translator = QTranslator(app)
    if translator.load(str(actual)):
        app.installTranslator(translator)
        return translator
    return None
