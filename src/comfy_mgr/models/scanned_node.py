"""ScannedNode:扫描得到的 per-env 节点。"""
from __future__ import annotations
import json
import uuid
from dataclasses import dataclass, field
from pathlib import Path
from typing import Literal


@dataclass
class ScannedNode:
    id: str = field(default_factory=lambda: f"sn-{uuid.uuid4().hex[:8]}")
    env_id: str = ""
    package: str = ""
    package_path: Path = field(default_factory=Path)
    version: str | None = None
    author: str | None = None
    description: str | None = None
    class_mappings: list[str] = field(default_factory=list)
    status: Literal["enabled", "disabled"] = "enabled"
    scan_meta: dict = field(default_factory=dict)
    last_scanned_at: str | None = None

    def to_row(self) -> dict:
        return {
            "id": self.id,
            "env_id": self.env_id,
            "package": self.package,
            "package_path": str(self.package_path),
            "version": self.version,
            "author": self.author,
            "description": self.description,
            "class_mappings": json.dumps(self.class_mappings),
            "status": self.status,
            "scan_meta": json.dumps(self.scan_meta),
            "last_scanned_at": self.last_scanned_at,
        }

    @classmethod
    def from_row(cls, row) -> "ScannedNode":
        d = dict(row)
        return cls(
            id=d["id"],
            env_id=d["env_id"],
            package=d["package"],
            package_path=Path(d["package_path"]),
            version=d["version"],
            author=d["author"],
            description=d["description"],
            class_mappings=json.loads(d["class_mappings"] or "[]"),
            status=d["status"],
            scan_meta=json.loads(d["scan_meta"] or "{}"),
            last_scanned_at=d["last_scanned_at"],
        )
