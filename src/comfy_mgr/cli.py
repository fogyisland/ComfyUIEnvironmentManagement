from __future__ import annotations
import sqlite3
import typer
from pathlib import Path
from comfy_mgr import __version__
from comfy_mgr.db.connection import get_connection, init_schema
from comfy_mgr.infra.fs import FS
from comfy_mgr.infra.git import GitManager
from comfy_mgr.infra.venv import VenvManager
from comfy_mgr.infra.process import ProcessService
from comfy_mgr.settings import SettingsService
from comfy_mgr.services.catalog import CatalogService
from comfy_mgr.services.environment import EnvironmentService
from comfy_mgr.services.node import NodeService

app = typer.Typer(help="ComfyUI Manager CLI")

# 子命令组
env_app = typer.Typer(help="环境管理")
catalog_app = typer.Typer(help="节点 catalog 管理")
settings_app = typer.Typer(help="设置管理")
app.add_typer(env_app, name="env")
app.add_typer(catalog_app, name="catalog")
app.add_typer(settings_app, name="settings")


def build_services() -> dict:
    """依赖注入容器。"""
    settings = SettingsService()
    db_path = settings.resolve_db_path()
    db_path.parent.mkdir(parents=True, exist_ok=True)
    conn = get_connection(db_path)
    init_schema(conn)
    project_root = Path.cwd()
    return {
        "settings": settings,
        "conn": conn,
        "fs": FS(),
        "git": GitManager(),
        "venv": VenvManager(),
        "process": ProcessService(log_dir=project_root / "logs"),
        "env": EnvironmentService(
            conn=conn,
            project_root=project_root,
            fs=FS(),
            venv=VenvManager(),
        ),
        "catalog": CatalogService(
            conn=conn,
            git=GitManager(),
            catalog_root=project_root / "catalog" / "nodes",
        ),
        "node": NodeService(
            conn=conn,
            fs=FS(),
            env_repo=EnvironmentService(
                conn=conn, project_root=project_root, fs=FS(), venv=VenvManager()
            ).repo,
        ),
    }


@app.command()
def version():
    """显示版本号。"""
    typer.echo(f"comfyui-manager {__version__}")