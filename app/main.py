"""PySide6 入口。"""
from __future__ import annotations
import os
import sys
from pathlib import Path
from PySide6.QtCore import QUrl
from PySide6.QtGui import QGuiApplication
from PySide6.QtQml import QQmlApplicationEngine
from PySide6.QtWidgets import QApplication
from app.app_context import AppContext
from app.i18n import make_translator


def _pid_alive(pid: int) -> bool:
    """Signal 0 = check existence only, never delivered to process.
    Works for local PIDs on Windows (TerminateProcess probe) and POSIX.
    """
    try:
        os.kill(pid, 0)
        return True
    except (OSError, ProcessLookupError):
        return False


def recover_persisted_processes(ctx: "AppContext") -> None:
    """恢复持久化的 process state：只对真实活着的 PID 标 running。

    之前会把所有持久化的 state 一律当作 "running" — 但如果进程已死或
    残留的是上一个会话的 PID，UI 会卡在 "运行中" 状态无法启动。
    """
    for state in ctx.process._state_repo.list_all():
        env = ctx.environment.get(state.env_id)
        if not env:
            # env 已删但 state 残留 — 直接清掉
            ctx.process._state_repo.delete(state.env_id)
            continue
        if _pid_alive(state.pid):
            # UI 会显示 "运行中"，但 stop 操作会因 PROCESS_NOT_RUNNING
            # 失败并提示重启（因为我们没有真正拉起 backend）
            env.status = "running"
            env.pid = state.pid
            ctx.environment.repo.save(env)
        else:
            # 进程已死但 state 残留 — 把 env 改回 stopped 并清 state
            env.status = "stopped"
            env.pid = None
            ctx.environment.repo.save(env)
            ctx.process._state_repo.delete(state.env_id)


def main() -> None:
    app = QApplication(sys.argv)
    app.setOrganizationName("ComfyUI Manager")
    app.setApplicationName("ComfyUI Manager")

    ctx = AppContext()

    recover_persisted_processes(ctx)

    # i18n
    language = ctx.settings.get("language") or "zh_CN"
    make_translator(app, language)

    # 注册 Bridge 到 QML
    engine = QQmlApplicationEngine()
    # QML 模块路径必须包含 app/qml/,这样 qmldir 里声明的 module Manager 才能解析
    engine.addImportPath(str(Path(__file__).parent / "qml"))
    # PySide6 注入说明:setContextProperty 只暴露对象本身作为全局变量,QML 不能
    # 通过 `.` 访问注入 QObject 的 Python 属性(例如 appContext.node_bridge 是
    # undefined)。QML 必须直接使用下面注入的 camelCase 名字。
    engine.rootContext().setContextProperty("envBridge", ctx.environment_bridge)
    engine.rootContext().setContextProperty("processBridge", ctx.process_bridge)
    engine.rootContext().setContextProperty("catalogBridge", ctx.catalog_bridge)
    engine.rootContext().setContextProperty("nodeBridge", ctx.node_bridge)
    engine.rootContext().setContextProperty("settingsBridge", ctx.settings_bridge)
    engine.rootContext().setContextProperty("torchBridge", ctx.torch_bridge)
    engine.rootContext().setContextProperty("appContext", ctx)
    # per-env ScannedNodeService 工厂:原 QML 写 appContext.scanned_node_service(envId),
    # PySide6 不允许点访问,这里包装成 QObject 注入。
    from app.bridge.scanned_service_factory import ScannedServiceFactory
    scanned_factory = ScannedServiceFactory(ctx)
    engine.rootContext().setContextProperty("scannedServiceFactory", scanned_factory)

    # 加载 Main.qml
    qml_file = Path(__file__).parent / "qml" / "Main.qml"
    engine.load(QUrl.fromLocalFile(str(qml_file)))
    if not engine.rootObjects():
        print("[ERROR] Failed to load QML", file=sys.stderr)
        sys.exit(1)

    sys.exit(app.exec())


if __name__ == "__main__":
    main()
