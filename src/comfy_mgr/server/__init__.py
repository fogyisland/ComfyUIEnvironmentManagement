"""comfy_mgr.server：FastAPI + WebSocket 后端,WSBroadcaster 把 bus 事件推给所有 WPF client。"""
from comfy_mgr.server.app import build_app
from comfy_mgr.server.adapter import call_slot

__all__ = ["build_app", "call_slot"]