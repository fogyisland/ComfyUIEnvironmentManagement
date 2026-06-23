import pytest
from pathlib import Path
from unittest.mock import MagicMock
from typer.testing import CliRunner
from comfy_mgr.cli import app
from comfy_mgr.models.node import Node
from comfy_mgr.result import Result

runner = CliRunner()


@pytest.fixture
def isolated(tmp_path, monkeypatch):
    monkeypatch.setenv("APPDATA", str(tmp_path / "appdata"))
    project_root = tmp_path / "project"
    project_root.mkdir()
    monkeypatch.chdir(project_root)
    return project_root


def test_catalog_add_then_list(isolated, mocker):
    mock_node = Node(id="ltdrdata__ComfyUI-Impact-Pack", name="ComfyUI-Impact-Pack",
                     repo_url="https://github.com/ltdrdata/ComfyUI-Impact-Pack",
                     local_path=Path("x"), description="", author="")
    from comfy_mgr.cli import build_services
    original = build_services
    def patched():
        services = original()
        services["catalog"].add_node = MagicMock(return_value=Result.ok(mock_node))
        services["catalog"].list_nodes = MagicMock(return_value=[mock_node])
        return services
    mocker.patch("comfy_mgr.cli.build_services", side_effect=patched)

    result = runner.invoke(app, ["catalog", "add", "https://github.com/ltdrdata/ComfyUI-Impact-Pack"])
    assert result.exit_code == 0

    result = runner.invoke(app, ["catalog", "list"])
    assert "ComfyUI-Impact-Pack" in result.stdout


def test_catalog_add_fails(isolated, mocker):
    from comfy_mgr.cli import build_services
    original = build_services
    def patched():
        services = original()
        services["catalog"].add_node = MagicMock(return_value=Result.fail(
            __import__("comfy_mgr.result", fromlist=["ServiceError"]).ServiceError(
                code="GIT_CLONE_FAILED", message="net down"
            )
        ))
        return services
    mocker.patch("comfy_mgr.cli.build_services", side_effect=patched)

    result = runner.invoke(app, ["catalog", "add", "https://github.com/x/y"])
    assert result.exit_code != 0
    assert "net down" in result.output or "GIT_CLONE_FAILED" in result.output


def test_catalog_remove(isolated, mocker):
    from comfy_mgr.cli import build_services
    original = build_services
    def patched():
        services = original()
        services["catalog"].remove_node = MagicMock(return_value=Result.ok(None))
        return services
    mocker.patch("comfy_mgr.cli.build_services", side_effect=patched)

    result = runner.invoke(app, ["catalog", "remove", "owner__X"])
    assert result.exit_code == 0
