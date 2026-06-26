"""ScannedNodeService.scan + set_disabled 测试。"""
from __future__ import annotations
import uuid
from pathlib import Path
import pytest
from comfy_mgr.db.connection import get_connection, init_schema
from comfy_mgr.db.scanned_node_repo import ScannedNodeRepo
from comfy_mgr.infra.event_bus import EventBus
from comfy_mgr.infra.node_scanner import NodeScanner
from comfy_mgr.services.scanned_node import ScannedNodeService


@pytest.fixture
def fake_env_with_nodes(tmp_path: Path):
    """
    返回 {"env_id": str, "env_root": Path, "conn": Connection},
    env 已注册到 DB,custom_nodes/ 下放了 5 个不同形式的 fake 包。
    用于 ScannedNodeService.scan / ConflictService.detect 的端到端测试。

    直接插 environments 行(跳过 EnvironmentService.create 的重型逻辑),
    因为 M2 只需要 FK 约束满足。
    """
    conn = get_connection(tmp_path / "test.db")
    init_schema(conn)
    env_root = tmp_path / "env"
    env_root.mkdir(parents=True)
    custom_nodes_dir = env_root / "custom_nodes"
    custom_nodes_dir.mkdir(parents=True)

    env_id = f"env-{uuid.uuid4().hex[:8]}"
    conn.execute("""
        INSERT INTO environments (
            id, name, root_path, comfyui_layout, comfyui_source,
            venv_path, python_executable, custom_nodes_path,
            extra_model_paths_yaml, port, enabled_node_ids_json,
            status, pid
        ) VALUES (?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?)
    """, (
        env_id, "test_env", str(env_root), "shared", None,
        str(tmp_path / "venv"), str(tmp_path / "venv" / "Scripts" / "python.exe"),
        str(custom_nodes_dir), str(tmp_path / "extra.yaml"),
        8188, "[]", "stopped", None,
    ))

    # 干净的字面量 dict
    (custom_nodes_dir / "pkg_clean").mkdir()
    (custom_nodes_dir / "pkg_clean" / "__init__.py").write_text(
        'NODE_CLASS_MAPPINGS = {"A": A, "B": B}\n'
    )

    # 动态(函数调用)
    (custom_nodes_dir / "pkg_dynamic").mkdir()
    (custom_nodes_dir / "pkg_dynamic" / "__init__.py").write_text(
        'NODE_CLASS_MAPPINGS = build_mappings()\n'
    )

    # 语法错
    (custom_nodes_dir / "pkg_broken").mkdir()
    (custom_nodes_dir / "pkg_broken" / "__init__.py").write_text(
        'def x(:\n'
    )

    # 没 NODE_CLASS_MAPPINGS
    (custom_nodes_dir / "pkg_empty").mkdir()
    (custom_nodes_dir / "pkg_empty" / "__init__.py").write_text(
        'X = 1\n'
    )

    # 跟 pkg_clean 同 class(冲突测试)
    (custom_nodes_dir / "pkg_clash").mkdir()
    (custom_nodes_dir / "pkg_clash" / "__init__.py").write_text(
        'NODE_CLASS_MAPPINGS = {"A": ClsA, "C": ClsC}\n'
    )

    return {"env_id": env_id, "env_root": env_root, "conn": conn}


@pytest.fixture
def service(fake_env_with_nodes) -> ScannedNodeService:
    return ScannedNodeService(
        conn=fake_env_with_nodes["conn"],
        env_id=fake_env_with_nodes["env_id"],
        scanner=NodeScanner(),
        bus=EventBus(),
    )


# ============ scan tests ============

def test_scan_returns_5_nodes(service, fake_env_with_nodes):
    r = service.scan()
    assert r.ok
    assert len(r.value) == 5


def test_scan_persists_all_to_db(service, fake_env_with_nodes):
    service.scan()
    repo = ScannedNodeRepo(fake_env_with_nodes["conn"])
    assert repo.count() == 5


def test_scan_extracts_class_mappings_for_clean(service, fake_env_with_nodes):
    r = service.scan()
    by_pkg = {n.package: n for n in r.value}
    assert sorted(by_pkg["pkg_clean"].class_mappings) == ["A", "B"]
    assert by_pkg["pkg_clean"].scan_meta["source"] == "ast_clean"


def test_scan_marks_dynamic_with_warning(service, fake_env_with_nodes):
    r = service.scan()
    by_pkg = {n.package: n for n in r.value}
    assert by_pkg["pkg_dynamic"].class_mappings == []
    assert "dynamic_mappings" in by_pkg["pkg_dynamic"].scan_meta["warnings"]


def test_scan_handles_syntax_error(service, fake_env_with_nodes):
    r = service.scan()
    by_pkg = {n.package: n for n in r.value}
    assert by_pkg["pkg_broken"].scan_meta["source"] == "parse_error"
    assert any("syntax_error" in w for w in by_pkg["pkg_broken"].scan_meta["warnings"])


def test_scan_emits_nodes_changed(service, fake_env_with_nodes):
    bus = service.bus
    received = []
    bus.on("nodesChanged", lambda eid: received.append(eid))
    service.scan()
    assert received == [fake_env_with_nodes["env_id"]]


def test_scan_idempotent(service, fake_env_with_nodes):
    """重复 scan 不会重复插入(env_id+package 唯一)。"""
    r1 = service.scan()
    r2 = service.scan()
    repo = ScannedNodeRepo(fake_env_with_nodes["conn"])
    assert repo.count() == 5
