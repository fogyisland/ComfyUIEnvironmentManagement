from __future__ import annotations
import subprocess
from pathlib import Path
from comfy_mgr.result import Result, ServiceError

class GitManager:
    """git 命令的薄包装。"""

    @staticmethod
    def clone(url: str, dest: Path) -> Result[None]:
        try:
            dest.parent.mkdir(parents=True, exist_ok=True)
            result = subprocess.run(
                ["git", "clone", url, str(dest)],
                capture_output=True,
                text=True,
            )
            if result.returncode != 0:
                return Result.fail(ServiceError(
                    code="GIT_CLONE_FAILED",
                    message=f"git clone 失败: {result.stderr.strip()}",
                    detail={"url": url, "dest": str(dest)},
                ))
            return Result.ok(None)
        except Exception as e:
            return Result.fail(ServiceError(
                code="GIT_CLONE_FAILED",
                message=str(e),
                detail={"url": url},
            ))

    @staticmethod
    def pull(repo_path: Path) -> Result[None]:
        try:
            result = subprocess.run(
                ["git", "-C", str(repo_path), "pull"],
                capture_output=True,
                text=True,
            )
            if result.returncode != 0:
                return Result.fail(ServiceError(
                    code="GIT_PULL_FAILED",
                    message=f"git pull 失败: {result.stderr.strip()}",
                    detail={"path": str(repo_path)},
                ))
            return Result.ok(None)
        except Exception as e:
            return Result.fail(ServiceError(
                code="GIT_PULL_FAILED",
                message=str(e),
            ))