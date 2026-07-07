"""Catalog routes。"""
from __future__ import annotations
from fastapi import APIRouter, Depends, Request
from app.bridge.catalog_bridge import CatalogBridge
from comfy_mgr.server.adapter import call_slot
from comfy_mgr.server.schemas import CatalogAddRequest, CatalogRemoveRequest

router = APIRouter()


def get_catalog_bridge(request: Request) -> CatalogBridge:
    return request.app.state.catalog_bridge


@router.post("/list")
async def list_nodes(bridge: CatalogBridge = Depends(get_catalog_bridge)):
    return call_slot(bridge, "list_nodes")


@router.post("/add")
async def add_node(body: CatalogAddRequest,
                    bridge: CatalogBridge = Depends(get_catalog_bridge)):
    return call_slot(bridge, "add_node", url=body.url)


@router.post("/remove")
async def remove_node(body: CatalogRemoveRequest,
                       bridge: CatalogBridge = Depends(get_catalog_bridge)):
    return call_slot(bridge, "remove_node", node_id=body.node_id)
