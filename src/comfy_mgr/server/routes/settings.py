"""Settings routes。"""
from __future__ import annotations
from fastapi import APIRouter, Depends, Request
from app.bridge.settings_bridge import SettingsBridge
from comfy_mgr.server.adapter import call_slot
from comfy_mgr.server.schemas import (
    SettingsSetValueRequest, SettingsMigrateDbPathRequest,
)

router = APIRouter()


def get_settings_bridge(request: Request) -> SettingsBridge:
    return request.app.state.settings_bridge


@router.post("/get-all")
async def get_all(bridge: SettingsBridge = Depends(get_settings_bridge)):
    return {"ok": True, "value": bridge.current}


@router.post("/set-value")
async def set_value(body: SettingsSetValueRequest,
                     bridge: SettingsBridge = Depends(get_settings_bridge)):
    return call_slot(bridge, "set_value", key=body.key, value=body.value)


@router.post("/migrate-db-path")
async def migrate_db_path(body: SettingsMigrateDbPathRequest,
                           bridge: SettingsBridge = Depends(get_settings_bridge)):
    return call_slot(bridge, "migrate_db_path", new_path=body.new_path)


@router.post("/reload")
async def reload(bridge: SettingsBridge = Depends(get_settings_bridge)):
    return call_slot(bridge, "reload")
