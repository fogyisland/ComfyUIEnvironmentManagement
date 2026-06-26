"""ConflictService:duplicate_class 检测 + auto-recompute on nodesChanged。"""
from __future__ import annotations
import uuid
from pathlib import Path
import pytest
from comfy_mgr.db.connection import get_connection, init_schema
from comfy_mgr.db.scanned_node_repo import ScannedNodeRepo
from comfy_mgr.infra.event_bus import EventBus
from comfy_mgr.infra.node_scanner import NodeScanner
from comfy_mgr.services.scanned_node import ScannedNodeService
from comfy_mgr.services.conflict import ConflictService


@pytest.fixture
def fixtures(tmp_path: Path):
    """建一个 env + 3 个包:pkg_a (class X, Y), pkg_b (class X), pkg_c (class Z)。

    直接 INSERT INTO environments(跳过 EnvironmentService.create 重型逻辑),
    跟 T6/T7 fixture 保持一致 — M2 只需要 FK 约束满足。
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

    (custom_nodes_dir / "pkg_a").mkdir()
    (custom_nodes_dir / "pkg_a" / "__init__.py").write_text(
        'NODE_CLASS_MAPPINGS = {"X": X, "Y": Y}\n')
    (custom_nodes_dir / "pkg_b").mkdir()
    (custom_nodes_dir / "pkg_b" / "__init__.py").write_text(
        'NODE_CLASS_MAPPINGS = {"X": X2}\n')
    (custom_nodes_dir / "pkg_c").mkdir()
    (custom_nodes_dir / "pkg_c" / "__init__.py").write_text(
        'NODE_CLASS_MAPPINGS = {"Z": Z}\n')

    return {"env_id": env_id, "conn": conn, "env_root": env_root}


@pytest.fixture
def services(fixtures):
    bus = EventBus()
    node_svc = ScannedNodeService(
        conn=fixtures["conn"], env_id=fixtures["env_id"],
        scanner=NodeScanner(), bus=bus,
    )
    conflict_svc = ConflictService(
        conn=fixtures["conn"], node_service=node_svc, bus=bus,
    )
    return {"node_svc": node_svc, "conflict_svc": conflict_svc, **fixtures}


def test_detect_finds_duplicate_class(services):
    services["node_svc"].scan()
    r = services["conflict_svc"].detect(services["env_id"])
    assert r.ok
    # pkg_a 和 pkg_b 都提供 class "X" → 1 条 duplicate_class
    types = {c.conflict_type for c in r.value}
    assert "duplicate_class" in types


def test_detect_no_conflicts_when_no_overlap(tmp_path):
    """单独的 env,没有 class 重叠 → 空 conflicts 列表。"""
    conn = get_connection(tmp_path / "test.db")
    init_schema(conn)
    env_root = tmp_path / "env2"
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
        env_id, "test_env2", str(env_root), "shared", None,
        str(tmp_path / "venv"), str(tmp_path / "venv" / "Scripts" / "python.exe"),
        str(custom_nodes_dir), str(tmp_path / "extra.yaml"),
        8189, "[]", "stopped", None,
    ))

    (custom_nodes_dir / "pkg_a").mkdir()
    (custom_nodes_dir / "pkg_a" / "__init__.py").write_text(
        'NODE_CLASS_MAPPINGS = {"X": X}\n')
    (custom_nodes_dir / "pkg_b").mkdir()
    (custom_nodes_dir / "pkg_b" / "__init__.py").write_text(
        'NODE_CLASS_MAPPINGS = {"Y": Y}\n')

    bus = EventBus()
    node_svc = ScannedNodeService(
        conn=conn, env_id=env_id, scanner=NodeScanner(), bus=bus,
    )
    conflict_svc = ConflictService(
        conn=conn, node_service=node_svc, bus=bus,
    )
    node_svc.scan()
    r = conflict_svc.detect(env_id)
    assert r.ok
    assert r.value == []


def test_detect_excludes_disabled(services):
    services["node_svc"].scan()
    # 把 pkg_b 禁了
    node_repo = ScannedNodeRepo(services["conn"])
    pkg_b = next(
        n for n in node_repo.list_by_env(services["env_id"])
        if n.package == "pkg_b"
    )
    services["node_svc"].set_disabled(pkg_b.id, True)
    r = services["conflict_svc"].detect(services["env_id"])
    assert r.ok
    # pkg_b disabled,不参与冲突计算
    assert r.value == []


def test_detect_soft_deletes_old_on_recompute(services):
    services["node_svc"].scan()
    services["conflict_svc"].detect(services["env_id"])
    # 再 detect 一次,旧 conflict 软删
    services["conflict_svc"].detect(services["env_id"])
    active = services["conflict_svc"].list_active(services["env_id"])
    # 老的软删后被新写的覆盖,active 列表只有当前的
    assert len(active) == 1


def test_resolve_marks_resolved(services):
    services["node_svc"].scan()
    services["conflict_svc"].detect(services["env_id"])
    cf_id = services["conflict_svc"].list_active(services["env_id"])[0].id
    r = services["conflict_svc"].resolve(cf_id)
    assert r.ok
    assert services["conflict_svc"].list_active(services["env_id"]) == []


def test_ignore_marks_ignored(services):
    services["node_svc"].scan()
    services["conflict_svc"].detect(services["env_id"])
    cf_id = services["conflict_svc"].list_active(services["env_id"])[0].id
    r = services["conflict_svc"].ignore(cf_id)
    assert r.ok
    assert services["conflict_svc"].list_active(services["env_id"]) == []


def test_auto_recompute_on_nodes_changed(services):
    """scan → emit nodesChanged → ConflictService 自动重算。"""
    received = []
    services["conflict_svc"].bus.on("nodesChanged",
        lambda eid: received.append(eid))
    services["node_svc"].scan()
    assert received == [services["env_id"]]
    # 自动重算后,active 列表应该已经有 1 条
    active = services["conflict_svc"].list_active(services["env_id"])
    assert len(active) == 1
