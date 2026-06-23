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


@env_app.command("create")
def env_create(
    name: str = typer.Option(..., "--name", help="环境名"),
    layout: str = typer.Option(..., "--layout", help="shared 或 independent"),
    port: int = typer.Option(8188, "--port", help="ComfyUI 端口"),
    python: str = typer.Option(..., "--python", help="Python 解释器路径"),
    comfyui_source: str | None = typer.Option(None, "--comfyui-source", help="ComfyUI 源码路径（shared 必填）"),
):
    """创建新环境。"""
    services = build_services()
    result = services["env"].create(
        name=name,
        layout=layout,  # type: ignore
        python_path=Path(python),
        comfyui_source=Path(comfyui_source) if comfyui_source else None,
        port=port,
    )
    if result.ok:
        typer.echo(f"✓ 环境 {name} 创建成功（端口 {port}）")
    else:
        typer.echo(f"✗ 创建失败: {result.error.message}", err=True)
        raise typer.Exit(code=1)


@env_app.command("list")
def env_list():
    """列出所有环境。"""
    services = build_services()
    envs = services["env"].list_all()
    if not envs:
        typer.echo("（无环境）")
        return
    typer.echo(f"{'NAME':<20} {'LAYOUT':<12} {'PORT':<6} {'STATUS':<10}")
    for e in envs:
        typer.echo(f"{e.name:<20} {e.comfyui_layout:<12} {e.port:<6} {e.status:<10}")


@env_app.command("delete")
def env_delete(
    name: str = typer.Argument(...),
    force: bool = typer.Option(False, "--force", help="强制删除运行中环境"),
):
    """删除环境。"""
    services = build_services()
    env = next((e for e in services["env"].list_all() if e.name == name), None)
    if not env:
        typer.echo(f"✗ 环境 {name} 不存在", err=True)
        raise typer.Exit(code=1)
    result = services["env"].delete(env.id, force=force)
    if result.ok:
        typer.echo(f"✓ 环境 {name} 已删除")
    else:
        typer.echo(f"✗ 删除失败: {result.error.message}", err=True)
        raise typer.Exit(code=1)


@env_app.command("clone")
def env_clone(
    src: str = typer.Argument(..., help="源环境名"),
    new_name: str = typer.Argument(..., help="新环境名"),
):
    """克隆环境。"""
    services = build_services()
    src_env = next((e for e in services["env"].list_all() if e.name == src), None)
    if not src_env:
        typer.echo(f"✗ 源环境 {src} 不存在", err=True)
        raise typer.Exit(code=1)
    result = services["env"].clone(src_env.id, new_name)
    if result.ok:
        typer.echo(f"✓ 克隆 {src} → {new_name} 成功（端口 {result.value.port}）")
    else:
        typer.echo(f"✗ 克隆失败: {result.error.message}", err=True)
        raise typer.Exit(code=1)


@env_app.command("start")
def env_start(
    name: str = typer.Argument(...),
):
    """启动环境的 ComfyUI 进程。"""
    services = build_services()
    env = next((e for e in services["env"].list_all() if e.name == name), None)
    if not env:
        typer.echo(f"✗ 环境 {name} 不存在", err=True)
        raise typer.Exit(code=1)
    result = services["process"].start(env)
    if result.ok:
        h = result.value
        typer.echo(f"✓ {name} 启动中（PID={h.pid}, 端口={h.port}, 日志={h.log_file.name}）")
    else:
        typer.echo(f"✗ 启动失败: {result.error.message}", err=True)
        raise typer.Exit(code=1)


@env_app.command("stop")
def env_stop(
    name: str = typer.Argument(...),
    timeout: float = typer.Option(10.0, "--timeout", help="优雅停止超时（秒）"),
):
    """停止环境的 ComfyUI 进程。"""
    services = build_services()
    env = next((e for e in services["env"].list_all() if e.name == name), None)
    if not env:
        typer.echo(f"✗ 环境 {name} 不存在", err=True)
        raise typer.Exit(code=1)
    result = services["process"].stop(env, timeout=timeout)
    if result.ok:
        typer.echo(f"✓ {name} 已停止")
    else:
        typer.echo(f"✗ 停止失败: {result.error.message}", err=True)
        raise typer.Exit(code=1)


@env_app.command("status")
def env_status(
    name: str = typer.Argument(...),
):
    """显示环境状态。"""
    services = build_services()
    env = next((e for e in services["env"].list_all() if e.name == name), None)
    if not env:
        typer.echo(f"✗ 环境 {name} 不存在", err=True)
        raise typer.Exit(code=1)
    status = services["process"].get_status(env)
    state = "运行中" if status.running else "已停止"
    typer.echo(f"{env.name}: {state} (PID={status.pid}, 端口={status.port})")
