"""Process routes。"""
from __future__ import annotations
from fastapi import APIRouter, Depends, Request
from app.bridge.process_bridge import ProcessBridge
from comfy_mgr.server.adapter import call_slot
from comfy_mgr.server.schemas import (
    StartEnvRequest, StopEnvRequest, GetStatusRequest, LogsForRequest,
)

router = APIRouter()


def get_process_bridge(request: Request) -> ProcessBridge:
    return request.app.state.process_bridge


@router.post("/start-env")
async def start_env(body: StartEnvRequest,
                     bridge: ProcessBridge = Depends(get_process_bridge)):
    return call_slot(bridge, "start_env", env_id=body.env_id)


@router.post("/stop-env")
async def stop_env(body: StopEnvRequest,
                    bridge: ProcessBridge = Depends(get_process_bridge)):
    return call_slot(bridge, "stop_env",
                     env_id=body.env_id, timeout=body.timeout)


@router.post("/get-status")
async def get_status(body: GetStatusRequest,
                      bridge: ProcessBridge = Depends(get_process_bridge)):
    return call_slot(bridge, "get_status", env_id=body.env_id)


@router.post("/logs-for")
async def logs_for(body: LogsForRequest,
                    bridge: ProcessBridge = Depends(get_process_bridge)):
    return call_slot(bridge, "logs_for", env_id=body.env_id)


@router.post("/running-envs")
async def running_envs(bridge: ProcessBridge = Depends(get_process_bridge)):
    return call_slot(bridge, "running_envs")
