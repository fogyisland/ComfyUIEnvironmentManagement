"""WebSocket /ws/events endpoint。"""
from __future__ import annotations
from fastapi import APIRouter, WebSocket, WebSocketDisconnect
import logging

router = APIRouter()
logger = logging.getLogger(__name__)


@router.websocket("/ws/events")
async def ws_events(websocket: WebSocket) -> None:
    await websocket.accept()
    broadcaster = websocket.app.state.ws_broadcaster
    broadcaster.add_client(websocket)
    if broadcaster._heartbeat_task is None:
        broadcaster.start_heartbeat()
    try:
        # 客户端不需发消息,只保持连接;忽略任何收到的 text
        while True:
            await websocket.receive_text()
    except WebSocketDisconnect:
        pass
    finally:
        await broadcaster.remove_client(websocket)