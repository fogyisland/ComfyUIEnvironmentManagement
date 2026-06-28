"""DepService:解析 requirements.txt + pyproject.toml + 本地冲突检测。"""
from pathlib import Path
import uuid
from unittest.mock import MagicMock
from comfy_mgr.db.connection import get_connection, init_schema
from comfy_mgr.db.dep_repo import DepRepo
from comfy_mgr.db.scanned_node_repo import ScannedNodeRepo
from comfy_mgr.infra.event_bus import EventBus
from comfy_mgr.result import Result
from comfy_mgr.services.dependency import DepService


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


def _make_pkg(conn, package, pkg_path):
    repo = ScannedNodeRepo(conn)
    repo.upsert(type("N", (), {
        "id": f"sn-{uuid.uuid4().hex[:8]}",
        "env_id": "env-1", "package": package,
        "package_path": pkg_path, "version": None,
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


# ---------- parse_requirements_txt ----------

def test_parse_requirements_txt_simple(tmp_path: Path):
    conn = _bootstrap(tmp_path)
    pkg = tmp_path / "pkg-a"
    pkg.mkdir()
    (pkg / "requirements.txt").write_text(
        "torch>=2.0\nnumpy==1.24\n# comment\n-r other.txt\n"
    )
    svc = DepService(
        dep_repo=DepRepo(conn),
        scanned_repo=ScannedNodeRepo(conn),
        conn=conn, bus=EventBus(),
        compat_client=None,
    )
    r = svc._parse_requirements_txt(pkg)
    assert r.ok
    specs = {(d["dep_name"], d["dep_version_spec"]) for d in r.value}
    assert ("torch", ">=2.0") in specs
    assert ("numpy", "==1.24") in specs
    assert len(r.value) == 2  # -r 行跳过


def test_parse_requirements_txt_missing(tmp_path: Path):
    conn = _bootstrap(tmp_path)
    pkg = tmp_path / "pkg-a"
    pkg.mkdir()
    svc = DepService(
        dep_repo=DepRepo(conn),
        scanned_repo=ScannedNodeRepo(conn),
        conn=conn, bus=EventBus(),
        compat_client=None,
    )
    r = svc._parse_requirements_txt(pkg)
    assert r.ok
    assert r.value == []


# ---------- parse_pyproject ----------

def test_parse_pyproject_dependencies(tmp_path: Path):
    conn = _bootstrap(tmp_path)
    pkg = tmp_path / "pkg-a"
    pkg.mkdir()
    (pkg / "pyproject.toml").write_text(
        '[project]\n'
        'name = "x"\n'
        'dependencies = [\n'
        '  "torch>=2.0,<3.0",\n'
        '  "pillow>=10.0",\n'
        ']\n'
    )
    svc = DepService(
        dep_repo=DepRepo(conn),
        scanned_repo=ScannedNodeRepo(conn),
        conn=conn, bus=EventBus(),
        compat_client=None,
    )
    r = svc._parse_pyproject(pkg)
    assert r.ok
    specs = {(d["dep_name"], d["dep_version_spec"]) for d in r.value}
    assert ("torch", ">=2.0,<3.0") in specs
    assert ("pillow", ">=10.0") in specs


def test_parse_pyproject_no_file(tmp_path: Path):
    conn = _bootstrap(tmp_path)
    pkg = tmp_path / "pkg-a"
    pkg.mkdir()
    svc = DepService(
        dep_repo=DepRepo(conn),
        scanned_repo=ScannedNodeRepo(conn),
        conn=conn, bus=EventBus(),
        compat_client=None,
    )
    r = svc._parse_pyproject(pkg)
    assert r.ok
    assert r.value == []


def test_parse_pyproject_malformed_falls_back(tmp_path: Path):
    conn = _bootstrap(tmp_path)
    pkg = tmp_path / "pkg-a"
    pkg.mkdir()
    (pkg / "pyproject.toml").write_text("not valid [[[")
    svc = DepService(
        dep_repo=DepRepo(conn),
        scanned_repo=ScannedNodeRepo(conn),
        conn=conn, bus=EventBus(),
        compat_client=None,
    )
    r = svc._parse_pyproject(pkg)
    assert r.ok
    assert r.value == []


# ---------- scan_deps ----------

def test_scan_deps_writes_to_repo(tmp_path: Path):
    conn = _bootstrap(tmp_path)
    pkg = tmp_path / "pkg-a"
    pkg.mkdir()
    (pkg / "requirements.txt").write_text("torch>=2.0\n")
    (pkg / "pyproject.toml").write_text(
        '[project]\nname = "x"\ndependencies = ["numpy>=1.20"]\n'
    )
    _make_pkg(conn, "pkg-a", pkg)
    svc = DepService(
        dep_repo=DepRepo(conn),
        scanned_repo=ScannedNodeRepo(conn),
        conn=conn, bus=EventBus(),
        compat_client=None,
    )
    r = svc.scan_deps("env-1", "pkg-a")
    assert r.ok
    rows = DepRepo(conn).list_by_env_and_package("env-1", "pkg-a")
    deps = {(row["source"], row["dep_name"]) for row in rows}
    assert ("requirements_txt", "torch") in deps
    assert ("pyproject_toml", "numpy") in deps


def test_scan_deps_overwrites_old(tmp_path: Path):
    conn = _bootstrap(tmp_path)
    pkg = tmp_path / "pkg-a"
    pkg.mkdir()
    (pkg / "requirements.txt").write_text("torch>=1.0\n")
    _make_pkg(conn, "pkg-a", pkg)
    svc = DepService(
        dep_repo=DepRepo(conn),
        scanned_repo=ScannedNodeRepo(conn),
        conn=conn, bus=EventBus(),
        compat_client=None,
    )
    svc.scan_deps("env-1", "pkg-a")
    (pkg / "requirements.txt").write_text("torch>=2.0\n")
    svc.scan_deps("env-1", "pkg-a")
    rows = DepRepo(conn).list_by_env_and_package("env-1", "pkg-a")
    assert len(rows) == 1
    assert rows[0]["dep_version_spec"] == ">=2.0"


# ---------- detect_conflicts ----------

def test_detect_no_conflicts_when_compatible(tmp_path: Path):
    conn = _bootstrap(tmp_path)
    repo = DepRepo(conn)
    # pkg-a: torch>=2.0  ;  pkg-b: torch>=1.0(满足)
    now = "2026-06-28T00:00:00"
    repo.upsert({"id": "dr-1", "env_id": "env-1", "package": "pkg-a",
                 "source": "requirements_txt", "dep_name": "torch",
                 "dep_version_spec": ">=2.0", "scanned_at": now})
    repo.upsert({"id": "dr-2", "env_id": "env-1", "package": "pkg-b",
                 "source": "requirements_txt", "dep_name": "torch",
                 "dep_version_spec": ">=1.0", "scanned_at": now})
    svc = DepService(
        dep_repo=repo, scanned_repo=ScannedNodeRepo(conn),
        conn=conn, bus=EventBus(), compat_client=None,
    )
    r = svc.detect_conflicts("env-1")
    assert r.ok
    assert r.value == []


def test_detect_finds_local_conflict(tmp_path: Path):
    conn = _bootstrap(tmp_path)
    repo = DepRepo(conn)
    now = "2026-06-28T00:00:00"
    # pkg-a: torch>=3.0  ;  pkg-b: torch<3.0  → 冲突
    repo.upsert({"id": "dr-1", "env_id": "env-1", "package": "pkg-a",
                 "source": "requirements_txt", "dep_name": "torch",
                 "dep_version_spec": ">=3.0", "scanned_at": now})
    repo.upsert({"id": "dr-2", "env_id": "env-1", "package": "pkg-b",
                 "source": "requirements_txt", "dep_name": "torch",
                 "dep_version_spec": "<3.0", "scanned_at": now})
    svc = DepService(
        dep_repo=repo, scanned_repo=ScannedNodeRepo(conn),
        conn=conn, bus=EventBus(), compat_client=None,
    )
    r = svc.detect_conflicts("env-1")
    assert r.ok
    assert len(r.value) == 1
    c = r.value[0]
    assert c["conflict_type"] == "local_dep_version"
    assert set(c["node_ids"]) == {"pkg-a", "pkg-b"}


def test_detect_ignores_unparseable_specs(tmp_path: Path):
    conn = _bootstrap(tmp_path)
    repo = DepRepo(conn)
    now = "2026-06-28T00:00:00"
    repo.upsert({"id": "dr-1", "env_id": "env-1", "package": "pkg-a",
                 "source": "requirements_txt", "dep_name": "torch",
                 "dep_version_spec": "garbage", "scanned_at": now})
    svc = DepService(
        dep_repo=repo, scanned_repo=ScannedNodeRepo(conn),
        conn=conn, bus=EventBus(), compat_client=None,
    )
    r = svc.detect_conflicts("env-1")
    assert r.ok
    assert r.value == []


# ---------- _is_incompatible edge cases ----------

def test_is_incompatible_upper_bound_compatible():
    # <2.0 和 <3.0 共享 [0.0.0, 2.0) 区间 → 不应判为不兼容
    assert DepService._is_incompatible("<2.0", "<3.0") is False


def test_is_incompatible_upper_lower_incompatible():
    # >=3.0 和 <2.0 没有交集 → 应判为不兼容
    assert DepService._is_incompatible(">=3.0", "<2.0") is True


# ---------- check_global ----------

def test_check_global_returns_empty_when_no_client(tmp_path: Path):
    conn = _bootstrap(tmp_path)
    svc = DepService(
        dep_repo=DepRepo(conn), scanned_repo=ScannedNodeRepo(conn),
        conn=conn, bus=EventBus(), compat_client=None,
    )
    r = svc.check_global("env-1")
    assert r.ok
    assert r.value == []


def test_check_global_translates_incompat(tmp_path: Path):
    conn = _bootstrap(tmp_path)
    repo = DepRepo(conn)
    now = "2026-06-28T00:00:00"
    repo.upsert({"id": "dr-1", "env_id": "env-1", "package": "pkg-a",
                 "source": "requirements_txt", "dep_name": "torch",
                 "dep_version_spec": ">=2.0", "scanned_at": now})
    mock = MagicMock()
    mock.check_known_incompat.return_value = Result.ok([
        {"node_ids": ["pkg-a"], "detail": "incompat with cuda 11"}
    ])
    svc = DepService(
        dep_repo=repo, scanned_repo=ScannedNodeRepo(conn),
        conn=conn, bus=EventBus(), compat_client=mock,
    )
    r = svc.check_global("env-1")
    assert r.ok
    assert len(r.value) == 1
    assert r.value[0]["conflict_type"] == "global_dep_known_incompat"
    mock.check_known_incompat.assert_called_once()
