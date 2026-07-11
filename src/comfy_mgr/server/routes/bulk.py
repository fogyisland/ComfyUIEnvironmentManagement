"""Bulk update routes — 3 endpoints: start / cancel / get-status."""
from __future__ import annotations
from fastapi import APIRouter, Depends, Request

from comfy_mgr.server.schemas import BulkUpdateRequest

router = APIRouter()


def get_bulk_service(request: Request):
    return request.app.state.bulk_update_service


def _envelope(result):
    """Result → {ok, value/error} envelope 同其他 routes 风格一致。"""
    if result.ok:
        return {"ok": True, "value": result.value}
    return {"ok": False, "error": {
        "code": result.error.code, "message": result.error.message}}


@router.post("/start")
async def start(body: BulkUpdateRequest,
                svc=Depends(get_bulk_service)):
    return _envelope(svc.start(env_ids=body.env_ids, node_ids=body.node_ids))


@router.post("/{bulk_id}/cancel")
async def cancel(bulk_id: str,
                 svc=Depends(get_bulk_service)):
    return _envelope(svc.cancel(bulk_id))


@router.get("/{bulk_id}")
async def get_status(bulk_id: str,
                     svc=Depends(get_bulk_service)):
    return _envelope(svc.get_status(bulk_id))
