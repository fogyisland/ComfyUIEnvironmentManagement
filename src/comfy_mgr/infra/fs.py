from __future__ import annotations
import shutil
import subprocess
import sys
from pathlib import Path
from comfy_mgr.result import Result, ServiceError

class FS:
    """文件系统操作：junction、目录复制、目录创建。"""

    @staticmethod
    def create_junction(link: Path, target: Path) -> Result[None]:
        """Windows: mklink /J link target."""
        if sys.platform != "win32":
            return Result.fail(ServiceError(
                code="FS_PLATFORM_UNSUPPORTED",
                message=f"junction 仅支持 Windows，当前 {sys.platform}",
            ))
        try:
            link.parent.mkdir(parents=True, exist_ok=True)
            result = subprocess.run(
                ["cmd", "/c", "mklink", "/J", str(link), str(target)],
                capture_output=True,
                text=True,
            )
            if result.returncode != 0:
                return Result.fail(ServiceError(
                    code="FS_JUNCTION_FAILED",
                    message=f"mklink 失败: {result.stderr.strip()}",
                    detail={"link": str(link), "target": str(target)},
                ))
            return Result.ok(None)
        except Exception as e:
            return Result.fail(ServiceError(
                code="FS_JUNCTION_FAILED",
                message=str(e),
                detail={"link": str(link), "target": str(target)},
            ))

    @staticmethod
    def remove_junction(link: Path) -> Result[None]:
        """删除 junction（M0: 用 rmdir）。"""
        if sys.platform != "win32":
            return Result.fail(ServiceError(
                code="FS_PLATFORM_UNSUPPORTED",
                message=f"junction 仅支持 Windows，当前 {sys.platform}",
            ))
        if not link.exists():
            # 已不存在视为成功
            return Result.ok(None)
        try:
            subprocess.run(
                ["cmd", "/c", "rmdir", str(link)],
                capture_output=True,
                text=True,
            )
            return Result.ok(None)
        except Exception as e:
            return Result.fail(ServiceError(
                code="FS_JUNCTION_FAILED",
                message=str(e),
            ))

    @staticmethod
    def copy_directory(src: Path, dst: Path) -> Result[None]:
        try:
            shutil.copytree(src, dst)
            return Result.ok(None)
        except Exception as e:
            return Result.fail(ServiceError(
                code="FS_COPY_FAILED",
                message=str(e),
            ))

    @staticmethod
    def ensure_dir(path: Path) -> Result[None]:
        try:
            path.mkdir(parents=True, exist_ok=True)
            return Result.ok(None)
        except Exception as e:
            return Result.fail(ServiceError(
                code="FS_MKDIR_FAILED",
                message=str(e),
            ))
