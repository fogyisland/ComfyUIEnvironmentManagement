"""/healthz + /version endpoint。"""
from __future__ import annotations
from fastapi import APIRouter, Request
from comfy_mgr.server.schemas import HealthResponse, VersionResponse

router = APIRouter()


@router.get("/healthz", response_model=HealthResponse)
async def healthz() -> HealthResponse:
    return HealthResponse(status="ok")


@router.get("/version", response_model=VersionResponse)
async def version() -> VersionResponse:
    return VersionResponse(service="comfy_mgr.server", version="0.4.0", schema=5)