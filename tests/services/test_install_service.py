"""InstallService:git clone 节点 + uninstall + 目录冲突处理。"""
from pathlib import Path
import uuid
from unittest.mock import patch, MagicMock
from comfy_mgr.db.connection import get_connection, init_schema
from comfy_mgr.db.scanned_node_repo import ScannedNodeRepo
from comfy_mgr.db.version_repo import VersionRepo
from comfy_mgr.infra.event_bus import EventBus
from comfy_mgr.services.install import InstallService


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
        "?,'/e1/emp.yaml',8188)",
        (str(tmp_path / "env1"), str(tmp_path / "env1" / "custom_nodes")),
    )
    return conn


def _git_ok(stdout="", stderr="", returncode=0):
    return MagicMock(returncode=returncode, stdout=stdout, stderr=stderr)


# ---------- install_from_git ----------

@patch("comfy_mgr.services.install.subprocess.run")
def test_install_success(mock_run, tmp_path: Path):
    conn = _bootstrap(tmp_path)
    cn = tmp_path / "env1" / "custom_nodes"
    cn.mkdir(parents=True)
    svc = InstallService(
        scanned_repo=ScannedNodeRepo(conn),
        version_repo=VersionRepo(conn),
        conn=conn, bus=EventBus(),
        git_exe_resolver=lambda: _git_exe(),
    )
    mock_run.return_value = _git_ok(stdout="Cloning into 'foo'...")
    r = svc.install_from_git(
        "env-1", "https://github.com/ltdrdata/foo.git",
    )
    assert r.ok
    # 目录被建
    assert (cn / "foo").exists()
    # scanned_nodes 有记录
    rows = ScannedNodeRepo(conn).list_by_env("env-1")
    assert len(rows) == 1
    assert rows[0].package == "foo"
    # version_history 有 install 记录
    history = VersionRepo(conn).list_by_env_and_package("env-1", "foo")
    assert len(history) == 1
    assert history[0]["action"] == "install"


@patch("comfy_mgr.services.install.subprocess.run")
def test_install_dir_exists_returns_conflict(mock_run, tmp_path: Path):
    conn = _bootstrap(tmp_path)
    cn = tmp_path / "env1" / "custom_nodes"
    cn.mkdir(parents=True)
    (cn / "foo").mkdir()  # 目录已存在
    svc = InstallService(
        scanned_repo=ScannedNodeRepo(conn),
        version_repo=VersionRepo(conn),
        conn=conn, bus=EventBus(),
        git_exe_resolver=lambda: _git_exe(),
    )
    r = svc.install_from_git(
        "env-1", "https://github.com/ltdrdata/foo.git",
    )
    assert not r.ok
    assert r.error.code == "INSTALL_DIR_EXISTS"
    mock_run.assert_not_called()


@patch("comfy_mgr.services.install.subprocess.run")
def test_install_clone_failed(mock_run, tmp_path: Path):
    conn = _bootstrap(tmp_path)
    cn = tmp_path / "env1" / "custom_nodes"
    cn.mkdir(parents=True)
    svc = InstallService(
        scanned_repo=ScannedNodeRepo(conn),
        version_repo=VersionRepo(conn),
        conn=conn, bus=EventBus(),
        git_exe_resolver=lambda: _git_exe(),
    )
    mock_run.return_value = _git_ok(
        returncode=128, stderr="repository not found",
    )
    r = svc.install_from_git(
        "env-1", "https://github.com/nope/nope.git",
    )
    assert not r.ok
    assert r.error.code == "INSTALL_FAILED"


def test_install_no_git_exe(tmp_path: Path):
    conn = _bootstrap(tmp_path)
    cn = tmp_path / "env1" / "custom_nodes"
    cn.mkdir(parents=True)
    svc = InstallService(
        scanned_repo=ScannedNodeRepo(conn),
        version_repo=VersionRepo(conn),
        conn=conn, bus=EventBus(),
        git_exe_resolver=lambda: None,
    )
    r = svc.install_from_git(
        "env-1", "https://github.com/ltdrdata/foo.git",
    )
    assert not r.ok
    assert r.error.code == "GIT_PORTABLE_MISSING"


# ---------- uninstall ----------

def test_uninstall_removes_dir_and_row(tmp_path: Path):
    conn = _bootstrap(tmp_path)
    cn = tmp_path / "env1" / "custom_nodes"
    cn.mkdir(parents=True)
    pkg = cn / "foo"
    pkg.mkdir()
    (pkg / "x.py").write_text("x")
    repo = ScannedNodeRepo(conn)
    repo.upsert(type("N", (), {
        "id": "sn-foo", "env_id": "env-1", "package": "foo",
        "package_path": pkg, "version": None,
        "author": None, "description": None,
        "class_mappings": [], "status": "enabled",
        "scan_meta": {}, "last_scanned_at": "2026-06-28T00:00:00",
        "to_row": lambda self: {
            "id": self.id, "env_id": self.env_id, "package": self.package,
            "package_path": str(self.package_path), "version": self.version,
            "author": self.author, "description": self.description,
            "class_mappings": "[]", "status": self.status,
            "scan_meta": "{}", "last_scanned_at": self.last_scanned_at,
        },
    })())
    svc = InstallService(
        scanned_repo=repo, version_repo=VersionRepo(conn),
        conn=conn, bus=EventBus(),
        git_exe_resolver=lambda: _git_exe(),
    )
    r = svc.uninstall("env-1", "foo")
    assert r.ok
    assert not pkg.exists()
    assert repo.get("sn-foo") is None
    history = VersionRepo(conn).list_by_env_and_package("env-1", "foo")
    assert history[0]["action"] == "uninstall"


def test_uninstall_pkg_not_found(tmp_path: Path):
    conn = _bootstrap(tmp_path)
    svc = InstallService(
        scanned_repo=ScannedNodeRepo(conn),
        version_repo=VersionRepo(conn),
        conn=conn, bus=EventBus(),
        git_exe_resolver=lambda: _git_exe(),
    )
    r = svc.uninstall("env-1", "nonexistent")
    assert not r.ok
    assert r.error.code == "NODE_NOT_FOUND"
