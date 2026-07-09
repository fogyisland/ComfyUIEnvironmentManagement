"""VersionService:节点版本管理(本地 git)。

所有 git 操作走 subprocess.run(60s timeout, capture_output=True)。
升级/回滚/锁定记录到 version_history。
"""
from __future__ import annotations
import sqlite3
import subprocess
import uuid
from datetime import datetime
from pathlib import Path
from typing import Callable

from packaging.version import Version, InvalidVersion

from comfy_mgr.db.version_repo import VersionRepo
from comfy_mgr.db.scanned_node_repo import ScannedNodeRepo
from comfy_mgr.infra.event_bus import EventBus
from comfy_mgr.result import Result, ServiceError


def _latest_semver(tags: list[str]) -> str | None:
    """从 git tag 列表找最大 Semver,忽略 v 前缀和不可解析的 tag。"""
    best: Version | None = None
    best_str = ""
    for tag in tags:
        t = tag.strip().lstrip("v")
        try:
            v = Version(t)
        except InvalidVersion:
            continue
        if best is None or v > best:
            best = v
            best_str = t
    return best_str or None


class VersionService:
    def __init__(
        self,
        *,
        version_repo: VersionRepo,
        scanned_repo: ScannedNodeRepo,
        conn: sqlite3.Connection,
        event_bus: EventBus,
        git_exe_resolver: Callable[[], Path | None],
    ):
        self.version_repo = version_repo
        self.scanned_repo = scanned_repo
        self.conn = conn
        self.bus = event_bus
        self.git_exe_resolver = git_exe_resolver

    # ----- list_status -----

    def list_status(self, env_id: str, package: str) -> Result[list[dict]]:
        node = self.scanned_repo.get_by_env_package(env_id, package)
        if not node:
            return Result.fail(ServiceError(
                code="NODE_NOT_FOUND",
                message=f"节点 {package} 不存在",
            ))
        pkg_path = Path(node.package_path)
        has_remote = (pkg_path / ".git").exists()
        locked = bool(self._get_locked(env_id, package))
        current_sha = ""
        current_pkg_version = node.version or ""
        latest = ""
        has_update = False
        if has_remote:
            r = self._run_git(pkg_path, ["rev-parse", "HEAD"])
            if r.ok:
                current_sha = r.value.stdout.strip()
            r = self._run_git(pkg_path, ["ls-remote", "--tags", "--sort=-v:refname"])
            if r.ok:
                tags = [
                    line.split("refs/tags/", 1)[-1].strip()
                    for line in r.value.stdout.splitlines()
                    if "refs/tags/" in line
                ]
                latest = _latest_semver(tags) or ""
                if latest and current_pkg_version:
                    try:
                        if Version(latest) > Version(current_pkg_version):
                            has_update = True
                    except InvalidVersion:
                        pass
        return Result.ok([{
            "package": package,
            "current_sha": current_sha,
            "current_sha_short": current_sha[:7] if current_sha else "",
            "current_version": current_pkg_version,
            "latest_version": latest,
            "has_remote": has_remote,
            "has_update": has_update,
            "locked": locked,
        }])

    # ----- upgrade -----

    def upgrade(self, env_id: str, package: str,
                *, target: str | None = None) -> Result[dict]:
        node = self.scanned_repo.get_by_env_package(env_id, package)
        if not node:
            return Result.fail(ServiceError(
                code="NODE_NOT_FOUND",
                message=f"节点 {package} 不存在",
            ))
        if self._get_locked(env_id, package):
            return self._fail_history(
                env_id, package, "upgrade", "blocked",
                error_code="VERSION_LOCKED",
                error_message="节点已锁定,请先 unlock",
            )
        pkg_path = Path(node.package_path)
        if not (pkg_path / ".git").exists():
            return self._fail_history(
                env_id, package, "upgrade", "failed",
                error_code="GIT_NO_REMOTE",
                error_message=f"{pkg_path} 不是 git 仓库",
            )
        git_exe = self.git_exe_resolver()
        if not git_exe:
            return self._fail_history(
                env_id, package, "upgrade", "failed",
                error_code="GIT_PORTABLE_MISSING",
                error_message="git executable not found",
            )

        before_sha = ""
        r = self._run_git(pkg_path, ["rev-parse", "HEAD"])
        if r.ok:
            before_sha = r.value.stdout.strip()

        # fetch
        r = self._run_git(pkg_path, ["fetch", "--all", "--prune"])
        if not r.ok:
            return self._fail_history(
                env_id, package, "upgrade", "failed",
                before_sha, error_code=r.error.code,
                error_message=r.error.message,
            )

        # reset --hard
        target_ref = target or "origin/HEAD"
        r = self._run_git(pkg_path, ["reset", "--hard", target_ref])
        if not r.ok:
            return self._fail_history(
                env_id, package, "upgrade", "failed",
                before_sha, error_code=r.error.code,
                error_message=r.error.message,
            )

        after_sha = ""
        r = self._run_git(pkg_path, ["rev-parse", "HEAD"])
        if r.ok:
            after_sha = r.value.stdout.strip()

        record = self._record(env_id, package, "upgrade",
                              before_sha, after_sha, "success")
        self.bus.emit("versionChanged", env_id, package)
        return Result.ok(record)

    # ----- downgrade (回滚到任意 commit/tag) -----

    def downgrade(self, env_id: str, package: str, target: str) -> Result[dict]:
        """git checkout {target}。"""
        node = self.scanned_repo.get_by_env_package(env_id, package)
        if not node:
            return Result.fail(ServiceError(
                code="NODE_NOT_FOUND",
                message=f"节点 {package} 不存在"))
        if self._get_locked(env_id, package):
            return self._fail_history(
                env_id, package, "downgrade", "blocked",
                error_code="VERSION_LOCKED",
                error_message="节点已锁定,请先 unlock",
            )
        if not (Path(node.package_path) / ".git").exists():
            return Result.fail(ServiceError(
                code="GIT_NO_REMOTE",
                message=f"{node.package_path} 不是 git 仓库"))
        git_exe = self.git_exe_resolver()
        if not git_exe:
            return Result.fail(ServiceError(
                code="GIT_PORTABLE_MISSING",
                message="git executable not found"))

        before = ""
        r = self._run_git(Path(node.package_path), ["rev-parse", "HEAD"])
        if r.ok:
            before = r.value.stdout.strip()

        r = self._run_git(Path(node.package_path), ["checkout", target])
        if not r.ok:
            return self._fail_history(
                env_id, package, "downgrade", "failed",
                before, error_code=r.error.code,
                error_message=r.error.message,
            )

        after = ""
        r = self._run_git(Path(node.package_path), ["rev-parse", "HEAD"])
        if r.ok:
            after = r.value.stdout.strip()

        record = self._record(env_id, package, "downgrade",
                              before, after, "success")
        self.bus.emit("versionChanged", env_id, package)
        return Result.ok(record)

    # ----- rollback(回滚到 version_history 里的某条) -----

    def rollback(self, env_id: str, package: str,
                 history_id: str) -> Result[dict]:
        rec = self.version_repo.get(history_id)
        if not rec:
            return Result.fail(ServiceError(
                code="HISTORY_NOT_FOUND",
                message=f"历史记录 {history_id} 不存在"))
        if not rec.get("version_before"):
            return Result.fail(ServiceError(
                code="ROLLBACK_NO_TARGET",
                message="该记录没有可回滚的 before 版本"))
        if self._get_locked(env_id, package):
            return Result.fail(ServiceError(
                code="VERSION_LOCKED",
                message="节点已锁定,请先 unlock",
            ))
        # 调 downgrade,result 标记 rolled_back
        r = self.downgrade(env_id, package, rec["version_before"])
        if r.ok:
            r.value["result"] = "rolled_back"
        return r

    # ----- list_history -----

    def list_history(self, env_id: str, package: str,
                     *, limit: int = 50) -> Result[list[dict]]:
        return Result.ok(self.version_repo.list_by_env_and_package(
            env_id, package, limit=limit))

    # ----- lock / unlock -----

    def lock(self, env_id: str, package: str) -> Result[dict]:
        return self._set_locked(env_id, package, True)

    def unlock(self, env_id: str, package: str) -> Result[dict]:
        return self._set_locked(env_id, package, False)

    # ----- internals -----

    def _get_locked(self, env_id: str, package: str) -> int:
        row = self.conn.execute(
            "SELECT locked FROM scanned_nodes WHERE env_id=? AND package=?",
            (env_id, package),
        ).fetchone()
        return row["locked"] if row else 0

    def _set_locked(self, env_id: str, package: str,
                    locked: bool) -> Result[dict]:
        node = self.scanned_repo.get_by_env_package(env_id, package)
        if not node:
            return Result.fail(ServiceError(
                code="NODE_NOT_FOUND",
                message=f"节点 {package} 不存在"))
        self.conn.execute(
            "UPDATE scanned_nodes SET locked=? WHERE env_id=? AND package=?",
            (1 if locked else 0, env_id, package),
        )
        action = "lock" if locked else "unlock"
        record = self._record(env_id, package, action, None, None, "success")
        self.bus.emit("versionChanged", env_id, package)
        return Result.ok(record)

    def _run_git(self, cwd: Path, args: list[str]) -> Result:
        git_exe = self.git_exe_resolver()
        if not git_exe:
            return Result.fail(ServiceError(
                code="GIT_PORTABLE_MISSING",
                message="git executable not found"))
        try:
            proc = subprocess.run(
                [str(git_exe), *args],
                cwd=str(cwd), capture_output=True, text=True,
                encoding="utf-8", timeout=60,
            )
        except subprocess.TimeoutExpired:
            return Result.fail(ServiceError(
                code="GIT_TIMEOUT",
                message=f"git {' '.join(args)} 超时(60s)"))
        except Exception as e:
            return Result.fail(ServiceError(
                code="GIT_FAILED", message=str(e)))
        if proc.returncode != 0:
            stderr = (proc.stderr or "")[:200]
            return Result.fail(ServiceError(
                code="GIT_FAILED",
                message=f"git {' '.join(args)}: {stderr}"))
        return Result.ok(proc)

    def _record(self, env_id, package, action, before, after, result,
                error_message=None) -> dict:
        pkg_version = self._try_parse_semver(after)
        rec = {
            "id": f"vh-{uuid.uuid4().hex[:8]}",
            "env_id": env_id,
            "package": package,
            "action": action,
            "version_before": before,
            "version_after": after,
            "pkg_version": pkg_version,
            "result": result,
            "error_message": error_message,
            "performed_at": datetime.now().isoformat(timespec="seconds"),
        }
        self.version_repo.insert(rec)
        return rec

    @staticmethod
    def _try_parse_semver(sha_or_version: str | None) -> str | None:
        if not sha_or_version:
            return None
        try:
            v = Version(sha_or_version)
            return str(v)
        except InvalidVersion:
            return None

    def _fail_history(self, env_id, package, action, result,
                      before_sha=None, *, error_code: str,
                      error_message: str) -> Result[dict]:
        rec = self._record(env_id, package, action, before_sha,
                           None, result, error_message=error_message)
        return Result.fail(ServiceError(
            code=error_code, message=error_message, detail=rec))
