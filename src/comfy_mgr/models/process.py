from __future__ import annotations
from dataclasses import dataclass
from datetime import datetime
from pathlib import Path

@dataclass
class ProcessHandle:
    env_id: str
    pid: int
    port: int
    started_at: datetime
    log_file: Path

@dataclass
class ProcessStatus:
    running: bool
    pid: int | None
    port: int | None
