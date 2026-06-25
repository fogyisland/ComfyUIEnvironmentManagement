"""ProcessService（M1: QProcess 实现）。"""
from __future__ import annotations
from datetime import datetime
from pathlib import Path
from typing import Callable, Optional
from PySide6.QtCore import QObject, QProcess, Signal
from comfy_mgr.models.environment import Environment
from comfy_mgr.models.process import ProcessHandle, ProcessStatus
from comfy_mgr.models.process_state import ProcessState, ProcessStateRepo
from comfy_mgr.result import Result, ServiceError


class QProcessBackend(QObject):
    """单个 ComfyUI 进程的 QProcess 包装。"""

    line_received = Signal(str, str)  # env_id, line
    finished_signal = Signal(str, int)  # env_id, exit_code

    def __init__(self, env: Environment, log_file: Path,
                 log_fh, parent: QObject | None = None):
        super().__init__(parent)
        self._env = env
        self._log_file = log_file
        self._log_fh = log_fh
        self._proc = QProcess(self)
        self._proc.setProcessChannelMode(QProcess.SeparateChannels)
        self._proc.readyReadStandardOutput.connect(self._on_stdout)
        self._proc.readyReadStandardError.connect(self._on_stderr)
        self._proc.finished.connect(self._on_finished)
        self._proc.errorOccurred.connect(self._on_error)
        self._buf_out = ""
        self._buf_err = ""
        self._exited = False

    def start(self, extra_args: list[str] | None = None) -> None:
        cmd = [
            str(self._env.python_executable),
            str(self._env.comfyui_source / "main.py"),
            "--port", str(self._env.port),
            "--listen", "0.0.0.0",
            "--disable-auto-launch",
        ]
        if self._env.extra_model_paths_yaml and self._env.extra_model_paths_yaml.exists():
            cmd += ["--extra-model-paths-config", str(self._env.extra_model_paths_yaml)]
        if extra_args:
            cmd += extra_args
        self._proc.setWorkingDirectory(str(self._env.comfyui_source))
        self._proc.start(cmd[0], cmd[1:])

    def pid(self) -> int:
        return int(self._proc.processId())

    def stop(self, timeout_ms: int) -> bool:
        if self._exited:
            return True
        self._proc.terminate()
        if self._proc.waitForFinished(timeout_ms):
            return True
        self._proc.kill()
        return self._proc.waitForFinished(5000)

    def is_running(self) -> bool:
        return self._proc.state() != QProcess.NotRunning

    def _on_stdout(self) -> None:
        chunk = bytes(self._proc.readAllStandardOutput()).decode("utf-8", errors="replace")
        self._buf_out += chunk
        while "\n" in self._buf_out:
            line, self._buf_out = self._buf_out.split("\n", 1)
            self._emit_line(line.rstrip("\r"))

    def _on_stderr(self) -> None:
        chunk = bytes(self._proc.readAllStandardError()).decode("utf-8", errors="replace")
        self._buf_err += chunk
        while "\n" in self._buf_err:
            line, self._buf_err = self._buf_err.split("\n", 1)
            self._emit_line(line.rstrip("\r"))

    def _emit_line(self, line: str) -> None:
        if self._log_fh:
            self._log_fh.write(line + "\n")
            self._log_fh.flush()
        self.line_received.emit(self._env.id, line)

    def _on_finished(self, exit_code: int, _status) -> None:
        # flush 残留 buffer
        for attr in ("_buf_out", "_buf_err"):
            buf = getattr(self, attr)
            if buf:
                self._emit_line(buf)
                setattr(self, attr, "")
        if self._log_fh:
            self._log_fh.close()
            self._log_fh = None  # type: ignore[assignment]
        self._exited = True
        self.finished_signal.emit(self._env.id, exit_code)

    def _on_error(self, err) -> None:
        self._emit_line(f"[QProcess error: {err}]")


class ProcessService:
    """ComfyUI 进程管理（M1: QProcess + bridge_sink 推送日志）。"""

    def __init__(self, conn, log_dir: Path,
                 bridge_sink: Optional[Callable[[str, str], None]] = None,
                 process_state_repo: ProcessStateRepo | None = None):
        self.conn = conn
        self.log_dir = log_dir
        self.log_dir.mkdir(parents=True, exist_ok=True)
        self._backends: dict[str, QProcessBackend] = {}
        self._bridge_sink = bridge_sink
        self._state_repo = process_state_repo or ProcessStateRepo(conn)

    def start(self, env: Environment) -> Result[ProcessHandle]:
        if env.id in self._backends and self._backends[env.id].is_running():
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
        backend = QProcessBackend(env, log_file, log_fh)
        if self._bridge_sink:
            backend.line_received.connect(self._bridge_sink)
        backend.finished_signal.connect(self._on_finished)
        self._backends[env.id] = backend
        try:
            backend.start()
            # 等进程真正启动（最多 2s）
            if not backend._proc.waitForStarted(2000):  # noqa: SLF001
                raise RuntimeError("QProcess failed to start within 2s")
        except Exception as e:
            self._backends.pop(env.id, None)
            return Result.fail(ServiceError(
                code="PROCESS_START_FAILED",
                message=str(e),
            ))
        handle = ProcessHandle(
            env_id=env.id,
            pid=backend.pid(),
            port=env.port,
            started_at=datetime.now(),
            log_file=log_file,
        )
        save_result = self._state_repo.save(ProcessState(
            env_id=env.id, pid=handle.pid,
            port=env.port, started_at=handle.started_at,
        ))
        if not save_result.ok:
            # rollback: stop the backend we just started
            backend.stop(timeout_ms=3000)
            self._backends.pop(env.id, None)
            return Result.fail(ServiceError(
                code="PROCESS_STATE_SAVE_FAILED",
                message=f"持久化进程状态失败: {save_result.error.message}",
            ))
        return Result.ok(handle)

    def stop(self, env: Environment, timeout: float = 10.0) -> Result[None]:
        backend = self._backends.get(env.id)
        if not backend:
            # 检查 DB 是否有 state（GUI 重启后没内存 backend）
            persisted = self._state_repo.get(env.id)
            if persisted:
                return Result.fail(ServiceError(
                    code="PROCESS_NOT_RUNNING",
                    message=f"环境 {env.name} 进程不在本 GUI 内存中，请重启 GUI 后再试",
                ))
            return Result.ok(None)
        ok = backend.stop(int(timeout * 1000))
        self._backends.pop(env.id, None)
        self._state_repo.delete(env.id)
        if not ok:
            return Result.fail(ServiceError(
                code="PROCESS_STOP_TIMEOUT",
                message=f"环境 {env.name} 停止超时（{timeout}s）",
            ))
        return Result.ok(None)

    def get_status(self, env: Environment) -> ProcessStatus:
        backend = self._backends.get(env.id)
        if backend is None:
            return ProcessStatus(running=False, pid=None, port=env.port)
        running = backend.is_running()
        return ProcessStatus(
            running=running,
            pid=backend.pid() if running else None,
            port=env.port,
        )

    def _on_finished(self, env_id: str, _exit_code: int) -> None:
        self._backends.pop(env_id, None)
        self._state_repo.delete(env_id)
