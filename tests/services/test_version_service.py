"""VersionService:git upgrade/downgrade + lock + history。

subprocess.run 用 mocker.patch,所有 git 操作不真打。
"""
from pathlib import Path
import sqlite3
import uuid
import json
from datetime import datetime
from unittest.mock import patch, MagicMock

from comfy_mgr.db.connection import get_connection, init_schema
from comfy_mgr.db.version_repo import VersionRepo
from comfy_mgr.db.scanned_node_repo import ScannedNodeRepo
from comfy_mgr.infra.event_bus import EventBus
from comfy_mgr.services.version import VersionService


def _git_exe():
    return Path("C:/fake/git.exe")


def _bootstrap(tmp_path: Path):
    conn = get_connection(tmp_path / "test.db")
    init_schema(conn)
    conn.execute(
        "INSERT INTO environments (id, name, root_path, comfyui_layout, "
        "venv_path, python_executable, custom_nodes_path, "
        "extra_model_paths_yaml, port) "
        "VALUES ('env-1','e1',?,'shared','/e1/.venv','/e1/.venv/python',"
        "'/e1/custom_nodes','/e1/emp.yaml',8188)",
        (str(tmp_path / "env1"),),
    )
    return conn


def _make_scanned_pkg(conn, env_id="env-1", package="pkg-a",
                       pkg_path: Path | None = None):
    repo = ScannedNodeRepo(conn)
    repo.upsert(type("N", (), {
        "id": f"sn-{uuid.uuid4().hex[:8]}",
        "env_id": env_id,
        "package": package,
        "package_path": pkg_path or Path("/fake/pkg"),
        "version": None,
        "author": None,
        "description": None,
        "class_mappings": [],
        "status": "enabled",
        "scan_meta": {},
        "last_scanned_at": datetime.now().isoformat(timespec="seconds"),
        "to_row": lambda self: {
            "id": self.id, "env_id": self.env_id, "package": self.package,
            "package_path": str(self.package_path), "version": self.version,
            "author": self.author, "description": self.description,
            "class_mappings": "[]", "status": self.status,
            "scan_meta": "{}", "last_scanned_at": self.last_scanned_at,
        },
    })())
    return repo


# ---------- list_status ----------

def test_list_status_no_git_dir(tmp_path: Path):
    conn = _bootstrap(tmp_path)
    pkg_dir = tmp_path / "env1" / "custom_nodes" / "pkg-a"
    pkg_dir.mkdir(parents=True)
    _make_scanned_pkg(conn, pkg_path=pkg_dir)
    svc = VersionService(
        version_repo=VersionRepo(conn),
        scanned_repo=ScannedNodeRepo(conn),
        conn=conn, event_bus=EventBus(),
        git_exe_resolver=lambda: _git_exe(),
    )
    r = svc.list_status("env-1", "pkg-a")
    assert r.ok
    assert r.value[0]["has_update"] is False
    assert r.value[0]["has_remote"] is False


# ---------- upgrade ----------

def _git_subprocess_ok(stdout="", stderr="", returncode=0):
    m = MagicMock(returncode=returncode, stdout=stdout, stderr=stderr)
    return m


@patch("comfy_mgr.services.version.subprocess.run")
def test_upgrade_success(mock_run, tmp_path: Path):
    conn = _bootstrap(tmp_path)
    pkg_dir = tmp_path / "env1" / "custom_nodes" / "pkg-a"
    pkg_dir.mkdir(parents=True)
    (pkg_dir / ".git").mkdir()  # 标记为 git 来源
    _make_scanned_pkg(conn, pkg_path=pkg_dir)
    svc = VersionService(
        version_repo=VersionRepo(conn),
        scanned_repo=ScannedNodeRepo(conn),
        conn=conn, event_bus=EventBus(),
        git_exe_resolver=lambda: _git_exe(),
    )
    # 模拟 git fetch + reset --hard 输出
    mock_run.side_effect = [
        _git_subprocess_ok(stdout="abc123\n"),       # rev-parse HEAD
        _git_subprocess_ok(stdout="Already up to date."),  # fetch
        _git_subprocess_ok(stdout="HEAD is now at def456"),  # reset
        _git_subprocess_ok(stdout="def456\n"),       # rev-parse HEAD after
    ]
    r = svc.upgrade("env-1", "pkg-a")
    assert r.ok
    assert mock_run.call_count == 4
    # version_history 写入
    history = VersionRepo(conn).list_by_env_and_package("env-1", "pkg-a")
    assert len(history) == 1
    assert history[0]["action"] == "upgrade"
    assert history[0]["result"] == "success"


@patch("comfy_mgr.services.version.subprocess.run")
def test_upgrade_blocked_when_locked(mock_run, tmp_path: Path):
    conn = _bootstrap(tmp_path)
    pkg_dir = tmp_path / "env1" / "custom_nodes" / "pkg-a"
    pkg_dir.mkdir(parents=True)
    (pkg_dir / ".git").mkdir()
    repo = ScannedNodeRepo(conn)
    _make_scanned_pkg(conn, pkg_path=pkg_dir)
    # 设置 locked=1
    repo.conn.execute(
        "UPDATE scanned_nodes SET locked=1 WHERE package='pkg-a'"
    )
    svc = VersionService(
        version_repo=VersionRepo(conn),
        scanned_repo=ScannedNodeRepo(conn),
        conn=conn, event_bus=EventBus(),
        git_exe_resolver=lambda: _git_exe(),
    )
    r = svc.upgrade("env-1", "pkg-a")
    assert not r.ok
    assert r.error.code == "VERSION_LOCKED"
    mock_run.assert_not_called()


def test_upgrade_fails_when_no_git_dir(tmp_path: Path):
    conn = _bootstrap(tmp_path)
    pkg_dir = tmp_path / "env1" / "custom_nodes" / "pkg-a"
    pkg_dir.mkdir(parents=True)  # 没 .git
    _make_scanned_pkg(conn, pkg_path=pkg_dir)
    svc = VersionService(
        version_repo=VersionRepo(conn),
        scanned_repo=ScannedNodeRepo(conn),
        conn=conn, event_bus=EventBus(),
        git_exe_resolver=lambda: _git_exe(),
    )
    r = svc.upgrade("env-1", "pkg-a")
    assert not r.ok
    assert r.error.code == "GIT_NO_REMOTE"


def test_git_exe_missing(tmp_path: Path):
    conn = _bootstrap(tmp_path)
    pkg_dir = tmp_path / "env1" / "custom_nodes" / "pkg-a"
    pkg_dir.mkdir(parents=True)
    (pkg_dir / ".git").mkdir()
    _make_scanned_pkg(conn, pkg_path=pkg_dir)
    svc = VersionService(
        version_repo=VersionRepo(conn),
        scanned_repo=ScannedNodeRepo(conn),
        conn=conn, event_bus=EventBus(),
        git_exe_resolver=lambda: None,  # 找不到 git
    )
    r = svc.upgrade("env-1", "pkg-a")
    assert not r.ok
    assert r.error.code == "GIT_PORTABLE_MISSING"


# ---------- lock / unlock ----------

def test_lock_sets_db_flag(tmp_path: Path):
    conn = _bootstrap(tmp_path)
    pkg_dir = tmp_path / "env1" / "custom_nodes" / "pkg-a"
    pkg_dir.mkdir(parents=True)
    _make_scanned_pkg(conn, pkg_path=pkg_dir)
    svc = VersionService(
        version_repo=VersionRepo(conn),
        scanned_repo=ScannedNodeRepo(conn),
        conn=conn, event_bus=EventBus(),
        git_exe_resolver=lambda: _git_exe(),
    )
    r = svc.lock("env-1", "pkg-a")
    assert r.ok
    row = conn.execute(
        "SELECT locked FROM scanned_nodes WHERE package='pkg-a'"
    ).fetchone()
    assert row["locked"] == 1


def test_unlock_clears_db_flag(tmp_path: Path):
    conn = _bootstrap(tmp_path)
    pkg_dir = tmp_path / "env1" / "custom_nodes" / "pkg-a"
    pkg_dir.mkdir(parents=True)
    _make_scanned_pkg(conn, pkg_path=pkg_dir)
    conn.execute("UPDATE scanned_nodes SET locked=1")
    svc = VersionService(
        version_repo=VersionRepo(conn),
        scanned_repo=ScannedNodeRepo(conn),
        conn=conn, event_bus=EventBus(),
        git_exe_resolver=lambda: _git_exe(),
    )
    r = svc.unlock("env-1", "pkg-a")
    assert r.ok
    row = conn.execute(
        "SELECT locked FROM scanned_nodes WHERE package='pkg-a'"
    ).fetchone()
    assert row["locked"] == 0
