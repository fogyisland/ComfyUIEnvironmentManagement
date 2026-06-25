"""ProcessService（QProcess）单测。"""
import time
from datetime import datetime
from pathlib import Path
from unittest.mock import MagicMock
import pytest
from comfy_mgr.db.connection import get_connection, init_schema
from comfy_mgr.infra.process import ProcessService, QProcessBackend
from comfy_mgr.models.environment import Environment
from comfy_mgr.models.process import ProcessHandle, ProcessStatus
from comfy_mgr.models.process_state import ProcessState, ProcessStateRepo
from comfy_mgr.result import Result, ServiceError


def make_env(**overrides):
    defaults = dict(
        id="env1", name="env1",
        root_path=Path("D:/envs/env1"),
        comfyui_layout="shared",
        comfyui_source=Path("D:/shared/ComfyUI"),
        venv_path=Path("D:/envs/env1/venv"),
        python_executable=Path("D:/envs/env1/venv/Scripts/python.exe"),
        custom_nodes_path=Path("D:/envs/env1/custom_nodes"),
        extra_model_paths_yaml=Path("D:/envs/env1/extra_model_paths.yaml"),
        port=8188,
    )
    defaults.update(overrides)
    return Environment(**defaults)


@pytest.fixture
def svc(tmp_path):
    conn = get_connection(tmp_path / "test.db")
    init_schema(conn)
    # Insert a stub environments row so process_state FK constraint passes
    env = make_env()
    conn.execute(
        "INSERT INTO environments (id, name, root_path, comfyui_layout, "
        "comfyui_source, venv_path, python_executable, custom_nodes_path, "
        "extra_model_paths_yaml, port) VALUES (?, ?, ?, ?, ?, ?, ?, ?, ?, ?)",
        (env.id, env.name, str(env.root_path), env.comfyui_layout,
         str(env.comfyui_source), str(env.venv_path), str(env.python_executable),
         str(env.custom_nodes_path), str(env.extra_model_paths_yaml), env.port),
    )
    return ProcessService(
        conn=conn, log_dir=tmp_path / "logs",
        process_state_repo=ProcessStateRepo(conn),
    )


def test_start_calls_qprocess_with_correct_args(svc, mocker):
    mock_backend_cls = mocker.patch("comfy_mgr.infra.process.QProcessBackend")
    mock_inst = MagicMock()
    mock_inst.is_running.return_value = False
    mock_inst.pid.return_value = 4321
    mock_inst._proc.waitForStarted.return_value = True
    mock_backend_cls.return_value = mock_inst

    result = svc.start(make_env())
    assert result.ok
    assert result.value.pid == 4321
    mock_inst.start.assert_called_once()


def test_start_persists_process_state(svc, mocker):
    mock_backend_cls = mocker.patch("comfy_mgr.infra.process.QProcessBackend")
    mock_inst = MagicMock()
    mock_inst.pid.return_value = 9999
    mock_inst._proc.waitForStarted.return_value = True
    mock_backend_cls.return_value = mock_inst

    svc.start(make_env())
    state = svc._state_repo.get("env1")
    assert state is not None
    assert state.pid == 9999
    assert state.port == 8188


def test_start_fails_when_state_save_fails(svc, mocker):
    """save() 失败时 start() 必须失败（不静默吞错）。"""
    mock_backend_cls = mocker.patch("comfy_mgr.infra.process.QProcessBackend")
    mock_inst = MagicMock()
    mock_inst.pid.return_value = 1234
    mock_inst._proc.waitForStarted.return_value = True
    mock_inst.stop.return_value = True  # rollback stop must succeed
    mock_backend_cls.return_value = mock_inst

    # 强制 _state_repo.save() 失败
    svc._state_repo.save = MagicMock(return_value=Result.fail(
        ServiceError(code="PROCESS_STATE_SAVE_FAILED", message="FK violation")
    ))

    result = svc.start(make_env())
    assert not result.ok
    assert result.error.code == "PROCESS_STATE_SAVE_FAILED"
    mock_inst.stop.assert_called_once()  # 必须回滚 backend


def test_start_returns_fail_if_qprocess_fails_to_start(svc, mocker):
    mock_backend_cls = mocker.patch("comfy_mgr.infra.process.QProcessBackend")
    mock_inst = MagicMock()
    mock_inst._proc.waitForStarted.return_value = False
    mock_backend_cls.return_value = mock_inst

    result = svc.start(make_env())
    assert not result.ok
    assert result.error.code == "PROCESS_START_FAILED"


def test_start_returns_fail_if_already_running(svc, mocker):
    mock_backend_cls = mocker.patch("comfy_mgr.infra.process.QProcessBackend")
    mock_inst = MagicMock()
    mock_inst.is_running.return_value = True
    mock_backend_cls.return_value = mock_inst

    # 手动塞一个 backend 到 _backends
    svc._backends["env1"] = mock_inst
    result = svc.start(make_env())
    assert not result.ok
    assert result.error.code == "PROCESS_ALREADY_RUNNING"


def test_stop_terminates_qprocess(svc, mocker):
    mock_backend_cls = mocker.patch("comfy_mgr.infra.process.QProcessBackend")
    mock_inst = MagicMock()
    mock_inst.pid.return_value = 1234
    mock_inst._proc.waitForStarted.return_value = True
    mock_inst.stop.return_value = True
    mock_backend_cls.return_value = mock_inst

    svc.start(make_env())
    result = svc.stop(make_env(), timeout=5.0)
    assert result.ok
    mock_inst.stop.assert_called_once()


def test_stop_returns_timeout_error(svc, mocker):
    mock_backend_cls = mocker.patch("comfy_mgr.infra.process.QProcessBackend")
    mock_inst = MagicMock()
    mock_inst.pid.return_value = 1234
    mock_inst._proc.waitForStarted.return_value = True
    mock_inst.stop.return_value = False
    mock_backend_cls.return_value = mock_inst

    svc.start(make_env())
    result = svc.stop(make_env(), timeout=5.0)
    assert not result.ok
    assert result.error.code == "PROCESS_STOP_TIMEOUT"


def test_stop_removes_process_state(svc, mocker):
    mock_backend_cls = mocker.patch("comfy_mgr.infra.process.QProcessBackend")
    mock_inst = MagicMock()
    mock_inst.pid.return_value = 1234
    mock_inst._proc.waitForStarted.return_value = True
    mock_inst.stop.return_value = True
    mock_backend_cls.return_value = mock_inst

    svc.start(make_env())
    assert svc._state_repo.get("env1") is not None
    svc.stop(make_env())
    assert svc._state_repo.get("env1") is None


def test_stop_returns_not_running_for_persisted_only(svc):
    """GUI 重启后内存里没 backend，但 DB 有 state。"""
    # Insert environments row for env-orphan to satisfy FK
    orphan = make_env(id="env-orphan", name="orphan")
    svc.conn.execute(
        "INSERT INTO environments (id, name, root_path, comfyui_layout, "
        "comfyui_source, venv_path, python_executable, custom_nodes_path, "
        "extra_model_paths_yaml, port) VALUES (?, ?, ?, ?, ?, ?, ?, ?, ?, ?)",
        (orphan.id, orphan.name, str(orphan.root_path), orphan.comfyui_layout,
         str(orphan.comfyui_source), str(orphan.venv_path), str(orphan.python_executable),
         str(orphan.custom_nodes_path), str(orphan.extra_model_paths_yaml), orphan.port),
    )
    svc._state_repo.save(ProcessState(
        "env-orphan", 1, 8188, datetime.now()))
    result = svc.stop(make_env(id="env-orphan", name="orphan"), timeout=5.0)
    assert not result.ok
    assert result.error.code == "PROCESS_NOT_RUNNING"


def test_get_status_running(svc, mocker):
    mock_backend_cls = mocker.patch("comfy_mgr.infra.process.QProcessBackend")
    mock_inst = MagicMock()
    mock_inst.pid.return_value = 7777
    mock_inst._proc.waitForStarted.return_value = True
    mock_inst.is_running.return_value = True
    mock_backend_cls.return_value = mock_inst

    svc.start(make_env())
    status = svc.get_status(make_env())
    assert status.running is True
    assert status.pid == 7777


def test_get_status_stopped(svc):
    status = svc.get_status(make_env())
    assert status.running is False
    assert status.pid is None


def test_qprocess_backend_emits_line_received(qapp, qtbot, tmp_path):
    """真实 QProcess：跑 python -c "print('hi')"。"""
    import sys
    env = make_env(python_executable=Path(sys.executable),
                   comfyui_source=tmp_path)
    # main.py 缺失无所谓，python -c 直接跑
    log_fh = open(tmp_path / "log.txt", "w", encoding="utf-8")
    backend = QProcessBackend(env, tmp_path / "log.txt", log_fh)
    received = []
    backend.line_received.connect(lambda _eid, line: received.append(line))

    # 用简单 python -c
    import shlex
    backend._proc.start(  # noqa: SLF001
        sys.executable, ["-c", "print('hello-from-qprocess')"]
    )
    assert backend._proc.waitForStarted(2000)  # noqa: SLF001
    assert backend._proc.waitForFinished(5000)  # noqa: SLF001
    log_fh.close()
    assert any("hello-from-qprocess" in line for line in received)
