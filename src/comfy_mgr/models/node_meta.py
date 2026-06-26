"""NodeMeta:节点在线元数据缓存(跨 env 共享)。"""
from __future__ import annotations
from dataclasses import dataclass


@dataclass
class NodeMeta:
    package: str
    github_url: str | None = None
    stars: int | None = None
    last_commit: str | None = None
    homepage: str | None = None
    fetched_at: str = ""
    fetch_error: str | None = None

    def to_row(self) -> dict:
        return {
            "package": self.package,
            "github_url": self.github_url,
            "stars": self.stars,
            "last_commit": self.last_commit,
            "homepage": self.homepage,
            "fetched_at": self.fetched_at,
            "fetch_error": self.fetch_error,
        }

    @classmethod
    def from_row(cls, row) -> "NodeMeta":
        d = dict(row)
        return cls(
            package=d["package"],
            github_url=d["github_url"],
            stars=d["stars"],
            last_commit=d["last_commit"],
            homepage=d["homepage"],
            fetched_at=d["fetched_at"],
            fetch_error=d["fetch_error"],
        )
