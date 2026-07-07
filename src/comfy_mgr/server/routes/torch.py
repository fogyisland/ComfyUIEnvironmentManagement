"""Torch routes。"""
from __future__ import annotations
from fastapi import APIRouter, Depends, Request
from app.bridge.torch_bridge import TorchBridge
from comfy_mgr.server.adapter import call_slot
from comfy_mgr.server.schemas import InitEnvTorchRequest

router = APIRouter()


def get_torch_bridge(request: Request) -> TorchBridge:
    return request.app.state.torch_bridge


@router.post("/detect-cuda")
async def detect_cuda(bridge: TorchBridge = Depends(get_torch_bridge)):
    return call_slot(bridge, "detect_cuda")


@router.post("/init-env-torch")
async def init_env_torch(body: InitEnvTorchRequest,
                          bridge: TorchBridge = Depends(get_torch_bridge)):
    return call_slot(bridge, "init_env_torch",
                     env_id=body.env_id, cu_version=body.cu_version)


@router.get("/suggested-cu-versions")
async def suggested_cu_versions(bridge: TorchBridge = Depends(get_torch_bridge)):
    return {"ok": True, "value": bridge.suggested_cu_versions}
