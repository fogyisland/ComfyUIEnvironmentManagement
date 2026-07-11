"""BulkUpdateService:跨 env × 节点批量调 node_bridge.upgrade_node。

设计要点:
- 内存 in-memory,bulk_id (UUID4) → status dict
- 后台异步执行(daemon task),前台 start() 立即返回 bulk_id
- 通过 EventBus 发 ws.push 到 'bulk_update' channel,UI 订阅
"""
from __future__ import annotations
import asyncio
import time
import uuid
from dataclasses import dataclass, field
from typing import Any, Optional

from comfy_mgr.infra.event_bus import EventBus
from comfy_mgr.result import Result, ServiceError
from app.bridge.node_bridge import NodeBridge


@dataclass
class _RowRecord:
    env_id: str
    node_id: str
    status: str = "pending"   # pending | running | succeeded | skipped | failed
    reason: Optional[str] = None
    latency_ms: int = 0


@dataclass
class _BulkRecord:
    bulk_id: str
    env_ids: list[str]
    node_ids: list[str]
    status: str = "pending"   # pending | running | completed | cancelled | failed
    started_at: Optional[str] = None
    finished_at: Optional[str] = None
    cancelled_at_checkpoint: Optional[str] = None
    rows: list[_RowRecord] = field(default_factory=list)
    succeeded: int = 0
    skipped: int = 0
    failed: int = 0
    current: Optional[str] = None
    _cancel_requested: bool = False
    _task: Optional[asyncio.Task] = None


class BulkUpdateService:
    """跨 env × 节点的批量 update 服务。

    启动后立即返回 bulk_id(后台异步执行)。
    """

    def __init__(self, node_bridge: NodeBridge, bus: EventBus) -> None:
        self._bridge = node_bridge
        self._bus = bus
        self._bulks: dict[str, _BulkRecord] = {}

    @staticmethod
    def _now_iso() -> str:
        from datetime import datetime
        return datetime.now().isoformat(timespec="seconds")

    def start(self, env_ids: list[str], node_ids: list[str]) -> Result[str]:
        if not env_ids:
            return Result.fail(ServiceError(
                code="BAD_VALIDATION", message="env_ids 不能为空"))
        if not node_ids:
            return Result.fail(ServiceError(
                code="BAD_VALIDATION", message="node_ids 不能为空"))
        bulk_id = str(uuid.uuid4())
        rows = [_RowRecord(env_id=e, node_id=n)
                for e in env_ids for n in node_ids]
        rec = _BulkRecord(
            bulk_id=bulk_id,
            env_ids=list(env_ids),
            node_ids=list(node_ids),
            rows=rows,
        )
        self._bulks[bulk_id] = rec
        # 启动后台 task(在已运行的 event loop 中)
        try:
            loop = asyncio.get_running_loop()
            if loop.is_running():
                rec._task = asyncio.create_task(self._run_bulk(rec))
            else:
                # 测试环境无 running loop,延后到下一轮手动 pump
                pass
        except RuntimeError:
            pass
        self._bus.emit("ws.push", "bulk_update.started", {
            "bulk_id": bulk_id,
            "total": len(rows),
            "env_ids": env_ids,
            "node_ids": node_ids,
        })
        return Result.ok(bulk_id)

    async def _run_bulk(self, rec: _BulkRecord) -> None:
        rec.status = "running"
        rec.started_at = self._now_iso()
        for row in rec.rows:
            if rec._cancel_requested:
                rec.status = "cancelled"
                break
            rec.current = f"{row.env_id}#{row.node_id}"
            row.status = "running"
            t0 = time.monotonic()
            res = self._bridge.upgrade_node(
                env_id=row.env_id, package=row.node_id, target=None)
            row.latency_ms = int((time.monotonic() - t0) * 1000)
            # res 是 dict envelope: {"ok": bool, "value": ...} 或 {"ok": false, "error": {"code", "message"}}
            if res.get("ok"):
                row.status = "succeeded"
                rec.succeeded += 1
                self._bus.emit("ws.push", "bulk_update.progress", {
                    "bulk_id": rec.bulk_id,
                    "env_id": row.env_id,
                    "node_id": row.node_id,
                    "status": "succeeded",
                    "latency_ms": row.latency_ms,
                })
            else:
                err = res.get("error") or {}
                err_code = err.get("code", "UNKNOWN")
                err_msg = err.get("message", "")
                # GIT_DIRTY/GIT_LOCKED/VERSION_LOCKED → skipped;其他 → failed
                if err_code in ("GIT_DIRTY", "GIT_HAS_LOCAL_CHANGES",
                                "GIT_LOCKED", "VERSION_LOCKED"):
                    row.status = "skipped"
                    row.reason = err_msg or "skipped"
                    rec.skipped += 1
                else:
                    row.status = "failed"
                    row.reason = err_msg or "failed"
                    rec.failed += 1
                self._bus.emit("ws.push", "bulk_update.progress", {
                    "bulk_id": rec.bulk_id,
                    "env_id": row.env_id,
                    "node_id": row.node_id,
                    "status": row.status,
                    "reason": row.reason,
                    "latency_ms": row.latency_ms,
                })
        rec.finished_at = self._now_iso()
        if rec.status != "cancelled":
            rec.status = "completed"
        rec.current = None
        summary = self._summary_of(rec)
        if rec.status == "cancelled":
            self._bus.emit("ws.push", "bulk_update.cancelled", {
                "bulk_id": rec.bulk_id,
                "summary": summary,
            })
        else:
            self._bus.emit("ws.push", "bulk_update.completed", {
                "bulk_id": rec.bulk_id,
                "summary": summary,
            })

    def cancel(self, bulk_id: str) -> Result[str]:
        rec = self._bulks.get(bulk_id)
        if rec is None:
            return Result.fail(ServiceError(
                code="BULK_NOT_FOUND", message=f"bulk_id {bulk_id} 不存在"))
        if rec.status in ("completed", "cancelled", "failed"):
            return Result.fail(ServiceError(
                code="BULK_NOT_RUNNING",
                message=f"bulk {bulk_id} 已 {rec.status}"))
        rec._cancel_requested = True
        checkpoint = rec.current or f"{rec.env_ids[0]}#{rec.node_ids[0]}"
        rec.cancelled_at_checkpoint = checkpoint
        return Result.ok(checkpoint)

    def get_status(self, bulk_id: str) -> Result[dict]:
        rec = self._bulks.get(bulk_id)
        if rec is None:
            return Result.fail(ServiceError(
                code="BULK_NOT_FOUND", message=f"bulk_id {bulk_id} 不存在"))
        out = {
            "bulk_id": bulk_id,
            "status": rec.status,
            "started_at": rec.started_at,
            "finished_at": rec.finished_at,
            "cancelled_at_checkpoint": rec.cancelled_at_checkpoint,
            "total": len(rec.rows),
            "succeeded": rec.succeeded,
            "skipped": rec.skipped,
            "failed": rec.failed,
            "current": rec.current,
        }
        return Result.ok(out)

    def get_all_running_ids(self) -> list[str]:
        return [bid for bid, r in self._bulks.items()
                if r.status in ("running", "pending")]

    def _summary_of(self, rec: _BulkRecord) -> dict:
        return {
            "total": len(rec.rows),
            "succeeded": rec.succeeded,
            "skipped": rec.skipped,
            "failed": rec.failed,
            "rows": [
                {
                    "env_id": r.env_id,
                    "node_id": r.node_id,
                    "status": r.status,
                    "reason": r.reason,
                    "latency_ms": r.latency_ms,
                }
                for r in rec.rows
            ],
        }
