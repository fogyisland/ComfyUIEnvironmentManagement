"""Compatibility shim:comfy_mgr.app_context 重新导出 app.app_context。

背景:M3 之前 AppContext 写在 app/app_context.py(PySide6 入口)。
M4 plan(及 T4 brief)统一从 comfy_mgr.app_context 引用,但实际搬迁工作
尚未排到 task(T21+ 才整体迁移 app/* → comfy_mgr/*)。

此 shim 让 T4 严格遵循 brief 的 `from comfy_mgr.app_context import
AppContext` 写法,同时不破坏现有 M0-M3 代码。

T21 完整迁移后此文件应删除。
"""
from app.app_context import AppContext  # noqa: F401

__all__ = ["AppContext"]