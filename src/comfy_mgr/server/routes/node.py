"""Node routes — 30 endpoints,1:1 mirror NodeBridge。"""
from __future__ import annotations
from fastapi import APIRouter, Depends, Request
from app.bridge.node_bridge import NodeBridge
from comfy_mgr.server.adapter import call_slot
from comfy_mgr.server.schemas import (
    EnableInEnvRequest, DisableInEnvRequest, SetScannedServiceRequest,
    NodeListRequest, SetDisabledRequest, ResolveConflictRequest,
    FetchRemoteMetaRequest, NodeDetailRequest,
    ListVersionsRequest, UpgradeNodeRequest, DowngradeNodeRequest,
    LockVersionRequest, RollbackVersionRequest, ListVersionHistoryRequest,
    ScanDepsRequest, ListDepsRequest,
    SearchCatalogRequest, InstallFromCatalogRequest, UninstallNodeRequest,
)

router = APIRouter()


def get_node_bridge(request: Request) -> NodeBridge:
    return request.app.state.node_bridge


# ===== M0/M1 启停 =====

@router.post("/enable-in-env")
async def enable_in_env(body: EnableInEnvRequest,
                         bridge: NodeBridge = Depends(get_node_bridge)):
    return call_slot(bridge, "enable_in_env",
                     env_id=body.env_id, node_id=body.node_id)


@router.post("/disable-in-env")
async def disable_in_env(body: DisableInEnvRequest,
                          bridge: NodeBridge = Depends(get_node_bridge)):
    return call_slot(bridge, "disable_in_env",
                     env_id=body.env_id, node_id=body.node_id)


@router.post("/set-scanned-service")
async def set_scanned_service(body: SetScannedServiceRequest,
                               bridge: NodeBridge = Depends(get_node_bridge)):
    bridge.set_scanned_service(bridge.scanned)  # 由 WPF 调用前已注入,这里仅 ping
    return {"ok": True, "value": {"env_id": body.env_id}}


# ===== M2 =====

@router.post("/node-list")
async def node_list(body: NodeListRequest,
                     bridge: NodeBridge = Depends(get_node_bridge)):
    return call_slot(bridge, "node_list", env_id=body.env_id)


@router.post("/conflict-list")
async def conflict_list(body: NodeListRequest,
                         bridge: NodeBridge = Depends(get_node_bridge)):
    return call_slot(bridge, "conflict_list", env_id=body.env_id)


@router.post("/request-scan")
async def request_scan(body: NodeListRequest,
                        bridge: NodeBridge = Depends(get_node_bridge)):
    return call_slot(bridge, "request_scan", env_id=body.env_id)


@router.post("/set-disabled")
async def set_disabled(body: SetDisabledRequest,
                        bridge: NodeBridge = Depends(get_node_bridge)):
    return call_slot(bridge, "set_disabled",
                     node_id=body.node_id, disabled=body.disabled)


@router.post("/toggle-disabled")
async def toggle_disabled(body: NodeDetailRequest,
                           bridge: NodeBridge = Depends(get_node_bridge)):
    return call_slot(bridge, "toggle_disabled", node_id=body.node_id)


@router.post("/resolve-conflict")
async def resolve_conflict(body: ResolveConflictRequest,
                            bridge: NodeBridge = Depends(get_node_bridge)):
    return call_slot(bridge, "resolve_conflict", conflict_id=body.conflict_id)


@router.post("/ignore-conflict")
async def ignore_conflict(body: ResolveConflictRequest,
                           bridge: NodeBridge = Depends(get_node_bridge)):
    return call_slot(bridge, "ignore_conflict", conflict_id=body.conflict_id)


@router.post("/fetch-remote-meta")
async def fetch_remote_meta(body: FetchRemoteMetaRequest,
                             bridge: NodeBridge = Depends(get_node_bridge)):
    return call_slot(bridge, "fetch_remote_meta",
                     package=body.package, owner=body.owner, repo=body.repo)


@router.post("/get-node-detail")
async def get_node_detail(body: NodeDetailRequest,
                           bridge: NodeBridge = Depends(get_node_bridge)):
    return call_slot(bridge, "get_node_detail", node_id=body.node_id)


# ===== M3 版本 =====

@router.post("/list-versions")
async def list_versions(body: ListVersionsRequest,
                         bridge: NodeBridge = Depends(get_node_bridge)):
    return call_slot(bridge, "list_versions",
                     env_id=body.env_id, package=body.package)


@router.post("/upgrade-node")
async def upgrade_node(body: UpgradeNodeRequest,
                        bridge: NodeBridge = Depends(get_node_bridge)):
    return call_slot(bridge, "upgrade_node",
                     env_id=body.env_id, package=body.package, target=body.target)


@router.post("/downgrade-node")
async def downgrade_node(body: DowngradeNodeRequest,
                          bridge: NodeBridge = Depends(get_node_bridge)):
    return call_slot(bridge, "downgrade_node",
                     env_id=body.env_id, package=body.package, target=body.target)


@router.post("/lock-version")
async def lock_version(body: LockVersionRequest,
                        bridge: NodeBridge = Depends(get_node_bridge)):
    return call_slot(bridge, "lock_version",
                     env_id=body.env_id, package=body.package)


@router.post("/unlock-version")
async def unlock_version(body: LockVersionRequest,
                          bridge: NodeBridge = Depends(get_node_bridge)):
    return call_slot(bridge, "unlock_version",
                     env_id=body.env_id, package=body.package)


@router.post("/rollback-version")
async def rollback_version(body: RollbackVersionRequest,
                            bridge: NodeBridge = Depends(get_node_bridge)):
    return call_slot(bridge, "rollback_version",
                     env_id=body.env_id, package=body.package,
                     history_id=body.history_id)


@router.post("/list-version-history")
async def list_version_history(body: ListVersionHistoryRequest,
                                bridge: NodeBridge = Depends(get_node_bridge)):
    return call_slot(bridge, "list_version_history",
                     env_id=body.env_id, package=body.package, limit=body.limit)


# ===== M3 依赖 =====

@router.post("/scan-deps")
async def scan_deps(body: ScanDepsRequest,
                     bridge: NodeBridge = Depends(get_node_bridge)):
    return call_slot(bridge, "scan_deps",
                     env_id=body.env_id, package=body.package)


@router.post("/list-deps")
async def list_deps(body: ListDepsRequest,
                     bridge: NodeBridge = Depends(get_node_bridge)):
    return call_slot(bridge, "list_deps",
                     env_id=body.env_id, package=body.package or "")


@router.post("/detect-dep-conflicts")
async def detect_dep_conflicts(body: NodeListRequest,
                                bridge: NodeBridge = Depends(get_node_bridge)):
    return call_slot(bridge, "detect_dep_conflicts", env_id=body.env_id)


@router.post("/check-global-compat")
async def check_global_compat(body: NodeListRequest,
                               bridge: NodeBridge = Depends(get_node_bridge)):
    return call_slot(bridge, "check_global_compat", env_id=body.env_id)


# ===== M3 目录 =====

@router.post("/search-catalog")
async def search_catalog(body: SearchCatalogRequest,
                          bridge: NodeBridge = Depends(get_node_bridge)):
    return call_slot(bridge, "search_catalog",
                     query=body.query, page=body.page)


@router.post("/get-catalog-entry")
async def get_catalog_entry(body: ListVersionsRequest,
                             bridge: NodeBridge = Depends(get_node_bridge)):
    return call_slot(bridge, "get_catalog_entry", package=body.package)


@router.post("/refresh-catalog")
async def refresh_catalog(bridge: NodeBridge = Depends(get_node_bridge)):
    return call_slot(bridge, "refresh_catalog")


@router.post("/install-from-catalog")
async def install_from_catalog(body: InstallFromCatalogRequest,
                                bridge: NodeBridge = Depends(get_node_bridge)):
    return call_slot(bridge, "install_from_catalog",
                     package=body.package, target_env_id=body.target_env_id)


@router.post("/uninstall-node")
async def uninstall_node(body: UninstallNodeRequest,
                          bridge: NodeBridge = Depends(get_node_bridge)):
    return call_slot(bridge, "uninstall_node",
                     env_id=body.env_id, package=body.package)


@router.post("/check-git-portable")
async def check_git_portable(bridge: NodeBridge = Depends(get_node_bridge)):
    return call_slot(bridge, "check_git_portable")