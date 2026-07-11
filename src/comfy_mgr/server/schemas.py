"""Pydantic 请求/响应模型,所有 route 用作 Request/Response shape。"""
from __future__ import annotations
from typing import Any, Literal, Optional
from pydantic import BaseModel


# ===== Health =====

class HealthResponse(BaseModel):
    status: str = "ok"


class VersionResponse(BaseModel):
    service: str
    version: str
    schema: int


# ===== Env =====

class EnvListRequest(BaseModel):
    env_id: Optional[str] = None


class EnvCreateRequest(BaseModel):
    name: str
    layout: str
    python: str
    comfyui_source: str
    port: int


class EnvDeleteRequest(BaseModel):
    env_id: str
    force: bool = False


class EnvCloneRequest(BaseModel):
    src_env_id: str
    new_name: str


# ===== Catalog =====

class CatalogAddRequest(BaseModel):
    url: str


class CatalogRemoveRequest(BaseModel):
    node_id: str


# ===== Node =====

class EnableInEnvRequest(BaseModel):
    env_id: str
    node_id: str


class DisableInEnvRequest(BaseModel):
    env_id: str
    node_id: str


class SetScannedServiceRequest(BaseModel):
    env_id: str


class NodeListRequest(BaseModel):
    env_id: str


class SetDisabledRequest(BaseModel):
    node_id: str
    disabled: bool


class ResolveConflictRequest(BaseModel):
    conflict_id: str


class FetchRemoteMetaRequest(BaseModel):
    package: str
    owner: str
    repo: str


class NodeDetailRequest(BaseModel):
    node_id: str


class ListVersionsRequest(BaseModel):
    env_id: str
    package: str


class UpgradeNodeRequest(BaseModel):
    env_id: str
    package: str
    target: Optional[str] = None


class DowngradeNodeRequest(BaseModel):
    env_id: str
    package: str
    target: str


class LockVersionRequest(BaseModel):
    env_id: str
    package: str


class RollbackVersionRequest(BaseModel):
    env_id: str
    package: str
    history_id: str


class ListVersionHistoryRequest(BaseModel):
    env_id: str
    package: str
    limit: int = 50


class ScanDepsRequest(BaseModel):
    env_id: str
    package: str


class ListDepsRequest(BaseModel):
    env_id: str
    package: Optional[str] = None


class SearchCatalogRequest(BaseModel):
    query: str
    page: int = 1


class InstallFromCatalogRequest(BaseModel):
    package: str
    target_env_id: str


class UninstallNodeRequest(BaseModel):
    env_id: str
    package: str


# ===== Process =====

class StartEnvRequest(BaseModel):
    env_id: str


class StopEnvRequest(BaseModel):
    env_id: str
    timeout: float = 10.0


class GetStatusRequest(BaseModel):
    env_id: str


class LogsForRequest(BaseModel):
    env_id: str


# ===== Settings =====

class SettingsSetValueRequest(BaseModel):
    key: str
    value: Any


class SettingsMigrateDbPathRequest(BaseModel):
    new_path: str


# ===== Torch =====

class InitEnvTorchRequest(BaseModel):
    env_id: str
    cu_version: str = ""


# ===== Error envelope =====

class ErrorBody(BaseModel):
    code: str
    message: str


class ErrorEnvelope(BaseModel):
    ok: bool = False
    error: ErrorBody


class OkEnvelope(BaseModel):
    ok: bool = True
    value: Any = None


# ===== M5 bulk update =====

class BulkUpdateRequest(BaseModel):
    env_ids: list[str] = []
    node_ids: list[str] = []  # 注:空值校验交由 service 层返回 BAD_VALIDATION envelope(测试期望 200)


class BulkUpdateStartedResponse(BaseModel):
    bulk_id: str


class BulkUpdateRow(BaseModel):
    env_id: str
    node_id: str
    status: Literal["succeeded", "skipped", "failed"]
    reason: Optional[str] = None
    latency_ms: int = 0


class BulkUpdateSummary(BaseModel):
    total: int
    succeeded: int
    skipped: int
    failed: int
    rows: list[BulkUpdateRow]


class BulkUpdateStatus(BaseModel):
    bulk_id: str
    status: Literal["pending", "running", "completed", "cancelled", "failed"]
    started_at: Optional[str] = None
    finished_at: Optional[str] = None
    total: int
    succeeded: int
    skipped: int
    failed: int
    current: Optional[str] = None
