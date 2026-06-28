"""Conflict:节点冲突检测结果。"""
from __future__ import annotations
import json
import uuid
from dataclasses import dataclass, field
from typing import Literal


@dataclass
class Conflict:
    id: str = field(default_factory=lambda: f"cf-{uuid.uuid4().hex[:8]}")
    env_id: str = ""
    conflict_type: Literal[
        "duplicate_class", "version_mismatch", "missing_dep",
        "local_dep_version", "global_dep_known_incompat",
    ] = "duplicate_class"
    node_ids: list[str] = field(default_factory=list)
    detail: dict = field(default_factory=dict)
    detected_at: str = ""
    resolved_at: str | None = None
    ignored: int = 0

    def to_row(self) -> dict:
        return {
            "id": self.id,
            "env_id": self.env_id,
            "conflict_type": self.conflict_type,
            "node_ids": json.dumps(sorted(self.node_ids)),  # 排序保证幂等
            "detail": json.dumps(self.detail),
            "detected_at": self.detected_at,
            "resolved_at": self.resolved_at,
            "ignored": self.ignored,
        }

    @classmethod
    def from_row(cls, row) -> "Conflict":
        d = dict(row)
        return cls(
            id=d["id"],
            env_id=d["env_id"],
            conflict_type=d["conflict_type"],
            node_ids=json.loads(d["node_ids"] or "[]"),
            detail=json.loads(d["detail"] or "{}"),
            detected_at=d["detected_at"],
            resolved_at=d["resolved_at"],
            ignored=d["ignored"],
        )
