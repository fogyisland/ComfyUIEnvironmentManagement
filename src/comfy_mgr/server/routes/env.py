"""Env routes — 1:1 mirror EnvironmentBridge。"""
from __future__ import annotations
from fastapi import APIRouter, Depends, Request
from app.bridge.environment_bridge import EnvironmentBridge
from comfy_mgr.server.adapter import call_slot
from comfy_mgr.server.schemas import (
    EnvListRequest, EnvCreateRequest, EnvDeleteRequest, EnvCloneRequest,
)

router = APIRouter()


def get_env_bridge(request: Request) -> EnvironmentBridge:
    return request.app.state.environment_bridge


@router.post("/list")
async def list_envs(body: EnvListRequest,
                    bridge: EnvironmentBridge = Depends(get_env_bridge)):
    return call_slot(bridge, "list_envs", env_id=body.env_id or "")


@router.post("/get")
async def get_env(body: EnvListRequest,
                   bridge: EnvironmentBridge = Depends(get_env_bridge)):
    return call_slot(bridge, "get_env", env_id=body.env_id or "")


@router.post("/create")
async def create_env(body: EnvCreateRequest,
                      bridge: EnvironmentBridge = Depends(get_env_bridge)):
    return call_slot(bridge, "create_env",
                     name=body.name, layout=body.layout, python=body.python,
                     comfyui_source=body.comfyui_source, port=body.port)


@router.post("/delete")
async def delete_env(body: EnvDeleteRequest,
                      bridge: EnvironmentBridge = Depends(get_env_bridge)):
    return call_slot(bridge, "delete_env", env_id=body.env_id, force=body.force)


@router.post("/clone")
async def clone_env(body: EnvCloneRequest,
                     bridge: EnvironmentBridge = Depends(get_env_bridge)):
    return call_slot(bridge, "clone_env",
                     src_env_id=body.src_env_id, new_name=body.new_name)
