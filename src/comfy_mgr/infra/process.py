from __future__ import annotations
import os
import subprocess
from datetime import datetime
from pathlib import Path
from comfy_mgr.models.environment import Environment
from comfy_mgr.models.process import ProcessHandle, ProcessStatus
from comfy_mgr.result import Result, ServiceError

IS_WINDOWS = os.name == "nt"

class ProcessService:
    """ComfyUI 进程管理（M0: subprocess；M1: QProcess）。"""

    def __init__(self, log_dir: Path):
        self.log_dir = log_dir
        self.log_dir.mkdir(parents=True, exist_ok=True)
        self._procs: dict[str, subprocess.Popen] = {}

    def start(self, env: Environment) -> Result[ProcessHandle]:
        if env.id in self._procs and self._procs[env.id].poll() is None:
            return Result.fail(ServiceError(
                code="PROCESS_ALREADY_RUNNING",
                message=f"环境 {env.name} 已在运行",
            ))
        log_file = self.log_dir / f"comfyui-{env.name}-{datetime.now():%Y%m%d-%H%M%S}.log"
        try:
            log_fh = open(log_file, "w", encoding="utf-8")
        except Exception as e:
            return Result.fail(ServiceError(
                code="PROCESS_LOG_FAILED",
                message=str(e),
            ))
        try:
            cmd = [
                str(env.python_executable),
                str(env.comfyui_source / "main.py"),
                "--port", str(env.port),
                "--listen", "0.0.0.0",
                "--disable-auto-launch",
            ]
            creationflags = subprocess.CREATE_NEW_PROCESS_GROUP if IS_WINDOWS else 0
            proc = subprocess.Popen(
                cmd,
                stdout=log_fh,
                stderr=subprocess.STDOUT,
                creationflags=creationflags,
                cwd=str(env.comfyui_source),
            )
            self._procs[env.id] = proc
            return Result.ok(ProcessHandle(
                env_id=env.id,
                pid=proc.pid,
                port=env.port,
                started_at=datetime.now(),
                log_file=log_file,
            ))
        except Exception as e:
            return Result.fail(ServiceError(
                code="PROCESS_START_FAILED",
                message=str(e),
            ))

    def stop(self, env: Environment, timeout: float = 10.0) -> Result[None]:
        proc = self._procs.get(env.id)
        if not proc:
            return Result.ok(None)  # 已停
        try:
            proc.terminate()
            try:
                proc.wait(timeout=timeout)
            except subprocess.TimeoutExpired:
                proc.kill()
                proc.wait(timeout=5)
            del self._procs[env.id]
            return Result.ok(None)
        except Exception as e:
            return Result.fail(ServiceError(
                code="PROCESS_STOP_FAILED",
                message=str(e),
            ))

    def get_status(self, env: Environment) -> ProcessStatus:
        proc = self._procs.get(env.id)
        if proc is None:
            return ProcessStatus(running=False, pid=None, port=env.port)
        running = proc.poll() is None
        return ProcessStatus(running=running, pid=proc.pid, port=env.port)
