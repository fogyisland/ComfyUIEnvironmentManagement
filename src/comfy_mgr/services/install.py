"""InstallService:节点安装(git clone) + 卸载。"""
from __future__ import annotations
import shutil
import sqlite3
import subprocess
import uuid
from datetime import datetime
from pathlib import Path
from typing import Callable
from urllib.parse import urlparse

from comfy_mgr.db.scanned_node_repo import ScannedNodeRepo
from comfy_mgr.db.version_repo import VersionRepo
from comfy_mgr.infra.event_bus import EventBus
from comfy_mgr.models.scanned_node import ScannedNode
from comfy_mgr.result import Result, ServiceError


class InstallService:
    def __init__(
        self,
        *,
        scanned_repo: ScannedNodeRepo,
        version_repo: VersionRepo,
        conn: sqlite3.Connection,
        bus: EventBus,
        git_exe_resolver: Callable[[], Path | None],
    ):
        self.scanned_repo = scanned_repo
        self.version_repo = version_repo
        self.conn = conn
        self.bus = bus
        self.git_exe_resolver = git_exe_resolver

    # ----- install_from_git -----

    def install_from_git(
        self, env_id: str, repo_url: str,
        *, target_dir_name: str | None = None,
    ) -> Result[ScannedNode]:
        git_exe = self.git_exe_resolver()
        if not git_exe:
            return Result.fail(ServiceError(
                code="GIT_PORTABLE_MISSING",
                message="git executable not found"))
        try:
            custom_nodes_dir = self._custom_nodes_dir(env_id)
        except ValueError as e:
            return Result.fail(ServiceError(code="ENV_NOT_FOUND", message=str(e)))

        dir_name = target_dir_name or self._derive_dir_name(repo_url)
        target_dir = custom_nodes_dir / dir_name
        if target_dir.exists():
            return Result.fail(ServiceError(
                code="INSTALL_DIR_EXISTS",
                message=f"目录 {target_dir} 已存在,请先 uninstall",
                detail={"path": str(target_dir)},
            ))

        try:
            proc = subprocess.run(
                [str(git_exe), "clone", repo_url, str(target_dir)],
                capture_output=True, text=True,
                encoding="utf-8", timeout=300,
            )
        except subprocess.TimeoutExpired:
            return Result.fail(ServiceError(
                code="GIT_TIMEOUT",
                message="git clone 超时(300s)"))
        except Exception as e:
            return Result.fail(ServiceError(
                code="INSTALL_FAILED", message=str(e)))
        if proc.returncode != 0:
            stderr = (proc.stderr or "")[:200]
            return Result.fail(ServiceError(
                code="INSTALL_FAILED",
                message=f"git clone 失败: {stderr}",
                detail={"url": repo_url},
            ))

        # Ensure target_dir exists (real git clone creates it; mocks may not)
        target_dir.mkdir(parents=True, exist_ok=True)

        # 写 scanned_nodes
        node = ScannedNode(
            id=f"sn-{uuid.uuid4().hex[:8]}",
            env_id=env_id,
            package=dir_name,
            package_path=target_dir,
            version=None, author=None, description=None,
            class_mappings=[], status="enabled",
            scan_meta={"source": "git_clone"},
            last_scanned_at=datetime.now().isoformat(timespec="seconds"),
        )
        r = self.scanned_repo.upsert(node)
        if not r.ok:
            return r

        # version_history
        rec = {
            "id": f"vh-{uuid.uuid4().hex[:8]}",
            "env_id": env_id, "package": dir_name,
            "action": "install", "version_before": None,
            "version_after": None, "pkg_version": None,
            "result": "success", "error_message": None,
            "performed_at": datetime.now().isoformat(timespec="seconds"),
        }
        self.version_repo.insert(rec)

        self.bus.emit("nodeInstalled", env_id, dir_name)
        return Result.ok(node)

    def install_from_catalog(
        self, env_id: str, catalog_entry: dict,
    ) -> Result[ScannedNode]:
        repo_url = catalog_entry.get("repo") or catalog_entry.get("repo_url", "")
        if not repo_url:
            return Result.fail(ServiceError(
                code="INSTALL_FAILED",
                message="catalog 条目没有 repo URL"))
        return self.install_from_git(env_id, repo_url)

    # ----- uninstall -----

    def uninstall(self, env_id: str, package: str) -> Result[None]:
        node = self.scanned_repo.get_by_env_package(env_id, package)
        if not node:
            return Result.fail(ServiceError(
                code="NODE_NOT_FOUND",
                message=f"节点 {package} 不存在"))
        pkg_path = Path(node.package_path)
        if pkg_path.exists():
            try:
                shutil.rmtree(pkg_path)
            except Exception as e:
                return Result.fail(ServiceError(
                    code="UNINSTALL_FAILED", message=str(e)))
        self.conn.execute(
            "DELETE FROM scanned_nodes WHERE id=?", (node.id,),
        )
        rec = {
            "id": f"vh-{uuid.uuid4().hex[:8]}",
            "env_id": env_id, "package": package,
            "action": "uninstall", "version_before": None,
            "version_after": None, "pkg_version": None,
            "result": "success", "error_message": None,
            "performed_at": datetime.now().isoformat(timespec="seconds"),
        }
        self.version_repo.insert(rec)
        self.bus.emit("nodeUninstalled", env_id, package)
        return Result.ok(None)

    # ----- helpers -----

    def _custom_nodes_dir(self, env_id: str) -> Path:
        row = self.conn.execute(
            "SELECT custom_nodes_path FROM environments WHERE id=?",
            (env_id,),
        ).fetchone()
        if not row:
            raise ValueError(f"env {env_id} 不存在")
        return Path(row[0])

    @staticmethod
    def _derive_dir_name(repo_url: str) -> str:
        """https://github.com/foo/bar.git → bar"""
        path = urlparse(repo_url).path.rstrip("/")
        last = path.rsplit("/", 1)[-1]
        if last.endswith(".git"):
            last = last[:-4]
        return last or "node"
