"""M4 T20: folder_rename mode 实际重命名。"""
from __future__ import annotations
import pytest
from pathlib import Path
from comfy_mgr.db.connection import get_connection, init_schema
from comfy_mgr.db.scanned_node_repo import ScannedNodeRepo
from comfy_mgr.infra.event_bus import EventBus
from comfy_mgr.infra.node_scanner import NodeScanner
from comfy_mgr.services.scanned_node import ScannedNodeService


@pytest.fixture
def svc(tmp_path):
    conn = get_connection(tmp_path / "test.db")
    init_schema(conn)
    bus = EventBus()
    custom_nodes = tmp_path / "env-1" / "custom_nodes"
    custom_nodes.mkdir(parents=True)
    pkg_dir = custom_nodes / "pkg-a"
    pkg_dir.mkdir()
    (pkg_dir / "__init__.py").write_text(
        "NODE_CLASS_MAPPINGS = {'A': object}\n")
    conn.execute("""
        INSERT INTO environments (id, name, root_path,
                                  comfyui_layout, custom_nodes_path)
        VALUES (?, ?, ?, ?, ?)
    """, ("env-1", "env-1", str(tmp_path / "env-1"),
          "isolated", str(custom_nodes)))
    return ScannedNodeService(conn, "env-1", NodeScanner(), bus)


def test_folder_rename_disabled_renames_dir(svc, tmp_path):
    r = svc.scan()
    assert r.ok
    node = r.value[0]
    pkg_dir = tmp_path / "env-1" / "custom_nodes" / "pkg-a"

    r = svc.set_disabled(node.id, disabled=True, mode="folder_rename")
    assert r.ok
    assert r.value.status == "disabled"
    disabled_dir = tmp_path / "env-1" / "custom_nodes" / "pkg-a.disabled"
    assert disabled_dir.exists()
    assert not pkg_dir.exists()
    assert r.value.package_path == disabled_dir


def test_folder_rename_re_enable_renames_back(svc, tmp_path):
    r = svc.scan()
    node = r.value[0]
    svc.set_disabled(node.id, disabled=True, mode="folder_rename")

    r = svc.set_disabled(node.id, disabled=False, mode="folder_rename")
    assert r.ok
    pkg_dir = tmp_path / "env-1" / "custom_nodes" / "pkg-a"
    assert pkg_dir.exists()
    assert r.value.package_path == pkg_dir


def test_folder_rename_target_exists_fails(svc, tmp_path):
    r = svc.scan()
    node = r.value[0]
    (tmp_path / "env-1" / "custom_nodes" / "pkg-a.disabled").mkdir()
    r = svc.set_disabled(node.id, disabled=True, mode="folder_rename")
    assert not r.ok
    assert r.error.code == "FOLDER_RENAME_CONFLICT"
    refreshed = svc.repo.get(node.id)
    assert refreshed.status == "enabled"  # DB 已回滚


def test_db_flag_mode_does_not_touch_disk(svc, tmp_path):
    r = svc.scan()
    node = r.value[0]
    pkg_dir = tmp_path / "env-1" / "custom_nodes" / "pkg-a"
    r = svc.set_disabled(node.id, disabled=True, mode="db_flag")
    assert r.ok
    assert pkg_dir.exists()
    assert r.value.disable_mode == "db_flag"


def test_unknown_mode_returns_bad_payload(svc):
    r = svc.scan()
    node = r.value[0]
    r = svc.set_disabled(node.id, disabled=True, mode="magic")
    assert not r.ok
    assert r.error.code == "BAD_PAYLOAD"
