from __future__ import annotations
from dataclasses import dataclass
from pathlib import Path

@dataclass
class Node:
    id: str
    name: str
    repo_url: str
    local_path: Path
    current_version: str | None = None
    description: str = ""
    author: str = ""