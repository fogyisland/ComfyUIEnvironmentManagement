"""PySide6 入口。"""
from __future__ import annotations
import sys
from pathlib import Path
from PySide6.QtCore import QUrl
from PySide6.QtGui import QGuiApplication
from PySide6.QtQml import QQmlApplicationEngine
from PySide6.QtWidgets import QApplication
from app.app_context import AppContext
from app.i18n import make_translator


def main() -> None:
    app = QApplication(sys.argv)
    app.setOrganizationName("ComfyUI Manager")
    app.setApplicationName("ComfyUI Manager")

    ctx = AppContext()

    # 恢复持久化的 process state
    for state in ctx.process._state_repo.list_all():
        env = ctx.environment.get(state.env_id)
        if env:
            # 标记 env 状态为 running（M0 status 字段），但不实际启动 backend
            # UI 会显示 "运行中"，但 stop 操作会因 PROCESS_NOT_RUNNING 失败并提示重启
            env.status = "running"
            env.pid = state.pid
            ctx.environment.repo.save(env)

    # i18n
    language = ctx.settings.get("language") or "zh_CN"
    make_translator(app, language)

    # 注册 Bridge 到 QML
    engine = QQmlApplicationEngine()
    engine.rootContext().setContextProperty("envBridge", ctx.environment_bridge)
    engine.rootContext().setContextProperty("processBridge", ctx.process_bridge)
    engine.rootContext().setContextProperty("catalogBridge", ctx.catalog_bridge)
    engine.rootContext().setContextProperty("nodeBridge", ctx.node_bridge)
    engine.rootContext().setContextProperty("settingsBridge", ctx.settings_bridge)
    engine.rootContext().setContextProperty("torchBridge", ctx.torch_bridge)

    # 加载 Main.qml
    qml_file = Path(__file__).parent / "qml" / "Main.qml"
    engine.load(QUrl.fromLocalFile(str(qml_file)))
    if not engine.rootObjects():
        print("[ERROR] Failed to load QML", file=sys.stderr)
        sys.exit(1)

    sys.exit(app.exec())


if __name__ == "__main__":
    main()
