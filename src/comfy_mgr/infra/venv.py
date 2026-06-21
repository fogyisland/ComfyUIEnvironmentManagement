from __future__ import annotations
import subprocess
from pathlib import Path
from comfy_mgr.result import Result, ServiceError

class VenvManager:
    """Python venv 创建与依赖安装。"""

    @staticmethod
    def create(python_exe: Path, venv_path: Path) -> Result[None]:
        try:
            venv_path.parent.mkdir(parents=True, exist_ok=True)
            result = subprocess.run(
                [str(python_exe), "-m", "venv", str(venv_path)],
                capture_output=True,
                text=True,
            )
            if result.returncode != 0:
                return Result.fail(ServiceError(
                    code="VENV_CREATE_FAILED",
                    message=f"venv 创建失败: {result.stderr.strip()}",
                    detail={"python": str(python_exe), "venv": str(venv_path)},
                ))
            return Result.ok(None)
        except Exception as e:
            return Result.fail(ServiceError(
                code="VENV_CREATE_FAILED",
                message=str(e),
            ))

    @staticmethod
    def install_requirements(venv_python: Path, requirements: Path) -> Result[None]:
        try:
            result = subprocess.run(
                [str(venv_python), "-m", "pip", "install", "-r", str(requirements)],
                capture_output=True,
                text=True,
            )
            if result.returncode != 0:
                return Result.fail(ServiceError(
                    code="VENV_PIP_FAILED",
                    message=f"pip install 失败: {result.stderr.strip()[:500]}",
                    detail={"requirements": str(requirements)},
                ))
            return Result.ok(None)
        except Exception as e:
            return Result.fail(ServiceError(
                code="VENV_PIP_FAILED",
                message=str(e),
            ))

    @staticmethod
    def get_python_version(python_exe: Path) -> Result[str]:
        try:
            result = subprocess.run(
                [str(python_exe), "--version"],
                capture_output=True,
                text=True,
            )
            if result.returncode != 0:
                return Result.fail(ServiceError(
                    code="VENV_VERSION_FAILED",
                    message=result.stderr.strip(),
                ))
            return Result.ok(result.stdout.strip() or result.stderr.strip())
        except Exception as e:
            return Result.fail(ServiceError(
                code="VENV_VERSION_FAILED",
                message=str(e),
            ))