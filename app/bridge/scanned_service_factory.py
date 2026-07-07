"""ScannedServiceFactory — QML 可见的 per-env ScannedNodeService 工厂。

原 AppContext.scanned_node_service 是 Python 方法,QML 通过
`appContext.scanned_node_service(envId)` 调用,但 PySide6 不支持 QML
通过 `.` 访问注入 QObject 的 Python 属性(详见 main.py 注释)。这里
把工厂方法包装成 QObject 的 Slot,作为独立 context property 注入。

注意:这里不真的把 ScannedNodeService 暴露成 QObject(SQLAlchemy 内部
对象过 QML 反射会有问题),只暴露 env_id → service 的桥;QML 拿到
service 后再传给 nodeBridge.setScannedService(service)。
"""
from __future__ import annotations
from PySide6.QtCore import QObject, Slot

from app.app_context import AppContext


class ScannedServiceFactory(QObject):
    def __init__(self, ctx: AppContext) -> None:
        super().__init__()
        self._ctx = ctx

    @Slot(str, result="QVariant")
    def make(self, env_id: str):
        return self._ctx.scanned_node_service(env_id)
