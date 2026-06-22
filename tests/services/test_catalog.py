from pathlib import Path
from unittest.mock import MagicMock
import pytest
from comfy_mgr.db.connection import get_connection, init_schema
from comfy_mgr.infra.git import GitManager
from comfy_mgr.models.node import Node, NodeRepo
from comfy_mgr.services.catalog import CatalogService
from comfy_mgr.result import Result

@pytest.fixture
def svc(tmp_path):
    db = tmp_path / "test.db"
    conn = get_connection(db)
    init_schema(conn)
    catalog_root = tmp_path / "catalog" / "nodes"
    catalog_root.mkdir(parents=True)
    git_mock = MagicMock(spec=GitManager)
    return CatalogService(
        conn=conn,
        git=git_mock,
        catalog_root=catalog_root,
    ), git_mock, catalog_root

def test_add_node_clones_and_inserts(svc, mocker):
    service, git_mock, catalog_root = svc
    mocker.patch("comfy_mgr.services.catalog.derive_node_id",
                 return_value="ltdrdata__ComfyUI-Impact-Pack")

    result = service.add_node("https://github.com/ltdrdata/ComfyUI-Impact-Pack")
    assert result.ok
    assert result.value.id == "ltdrdata__ComfyUI-Impact-Pack"

    # 验证 git.clone 被调用
    git_mock.clone.assert_called_once()
    args = git_mock.clone.call_args[0]
    assert args[0] == "https://github.com/ltdrdata/ComfyUI-Impact-Pack"
    assert args[1] == catalog_root / "ComfyUI-Impact-Pack"

    # 验证 DB 插入
    nodes = service.list_nodes()
    assert len(nodes) == 1
    assert nodes[0].name == "ComfyUI-Impact-Pack"

def test_add_node_fails_if_git_fails(svc, mocker):
    service, git_mock, _ = svc
    mocker.patch("comfy_mgr.services.catalog.derive_node_id", return_value="x")
    git_mock.clone.return_value = Result.fail(
        __import__("comfy_mgr.result", fromlist=["ServiceError"]).ServiceError(
            code="GIT_CLONE_FAILED", message="net down"
        )
    )
    result = service.add_node("https://github.com/x/y")
    assert not result.ok
    assert result.error.code == "GIT_CLONE_FAILED"
    assert service.list_nodes() == []

def test_add_node_fails_if_already_exists(svc, mocker):
    service, git_mock, _ = svc
    mocker.patch("comfy_mgr.services.catalog.derive_node_id", return_value="x")
    service.add_node("https://github.com/x/y")
    result = service.add_node("https://github.com/x/y")
    assert not result.ok
    assert result.error.code == "NODE_ALREADY_EXISTS"

def test_remove_node_deletes_db_and_dir(svc, mocker):
    service, git_mock, catalog_root = svc
    mocker.patch("comfy_mgr.services.catalog.derive_node_id", return_value="x")

    # 先 add 一次（创建假目录）
    target = catalog_root / "ComfyUI-Y"
    target.mkdir()
    (target / "x.txt").write_text("x")

    service.add_node("https://github.com/x/ComfyUI-Y")
    assert service.remove_node("x").ok
    assert not target.exists()
    assert service.list_nodes() == []

def test_remove_node_missing_returns_fail(svc):
    service, _, _ = svc
    result = service.remove_node("nope")
    assert not result.ok
    assert result.error.code == "NODE_NOT_FOUND"