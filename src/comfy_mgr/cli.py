from __future__ import annotations
import typer
from pathlib import Path
from comfy_mgr import __version__
from comfy_mgr.db.connection import get_connection, init_schema
from comfy_mgr.infra.cuda import CudaDetector
from comfy_mgr.infra.fs import FS
from comfy_mgr.infra.git import GitManager
from comfy_mgr.infra.venv import VenvManager
from comfy_mgr.infra.process import ProcessService
from comfy_mgr.models.pytorch import TorchConfig
from comfy_mgr.settings import SettingsService
from comfy_mgr.services.catalog import CatalogService
from comfy_mgr.services.environment import EnvironmentService
from comfy_mgr.services.node import NodeService

app = typer.Typer(help="ComfyUI Manager CLI")

# 子命令组
env_app = typer.Typer(help="环境管理")
catalog_app = typer.Typer(help="节点 catalog 管理")
settings_app = typer.Typer(help="设置管理")
torch_app = typer.Typer(help="PyTorch 栈管理")
app.add_typer(env_app, name="env")
app.add_typer(catalog_app, name="catalog")
app.add_typer(settings_app, name="settings")
app.add_typer(torch_app, name="torch")


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
    with_torch: bool = typer.Option(False, "--with-torch", help="创建 venv 后自动安装 PyTorch 栈"),
    cu: str | None = typer.Option(None, "--cu", help="PyTorch cu 版本（cu118/cu124/cu126/cpu）；与 --with-torch 配合"),
    no_torch: bool = typer.Option(False, "--no-torch", help="显式跳过 torch 安装（默认）"),
):
    """创建新环境。"""
    services = build_services()
    install_torch = with_torch and not no_torch
    result = services["env"].create(
        name=name,
        layout=layout,  # type: ignore
        python_path=Path(python),
        comfyui_source=Path(comfyui_source) if comfyui_source else None,
        port=port,
        install_torch=install_torch,
        cu_version=cu,
    )
    if result.ok:
        torch_note = "（已装 PyTorch）" if install_torch else ""
        typer.echo(f"✓ 环境 {name} 创建成功（端口 {port}）{torch_note}")
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


@catalog_app.command("add")
def catalog_add(
    url: str = typer.Argument(..., help="GitHub 仓库 URL"),
):
    """添加节点到 catalog。"""
    services = build_services()
    result = services["catalog"].add_node(url)
    if result.ok:
        typer.echo(f"✓ 节点 {result.value.name} 已添加（ID: {result.value.id}）")
    else:
        typer.echo(f"✗ 添加失败: {result.error.message}", err=True)
        raise typer.Exit(code=1)


@catalog_app.command("list")
def catalog_list():
    """列出 catalog 中的所有节点。"""
    services = build_services()
    nodes = services["catalog"].list_nodes()
    if not nodes:
        typer.echo("（catalog 为空）")
        return
    typer.echo(f"{'NAME':<30} {'ID':<40} {'URL'}")
    for n in nodes:
        typer.echo(f"{n.name:<30} {n.id:<40} {n.repo_url}")


@catalog_app.command("remove")
def catalog_remove(
    node_id: str = typer.Argument(..., help="节点 ID"),
):
    """从 catalog 移除节点。"""
    services = build_services()
    result = services["catalog"].remove_node(node_id)
    if result.ok:
        typer.echo(f"✓ 节点 {node_id} 已移除")
    else:
        typer.echo(f"✗ 移除失败: {result.error.message}", err=True)
        raise typer.Exit(code=1)


@settings_app.command("show")
def settings_show():
    """显示当前所有设置。"""
    services = build_services()
    s = services["settings"]
    for key in ["catalog_db_path", "theme", "language", "log_level", "default_python_path"]:
        val = s.get(key)
        if val is None:
            val = "(默认)"
        typer.echo(f"{key}: {val}")


@settings_app.command("set")
def settings_set(
    key: str = typer.Argument(...),
    value: str = typer.Argument(...),
):
    """设置一个配置项。"""
    services = build_services()
    s = services["settings"]
    s.set(key, value)
    s.save()
    typer.echo(f"✓ {key} = {value}")


@settings_app.command("set-catalog-db-path")
def settings_set_catalog_db_path(
    new_path: str = typer.Argument(..., help="新 catalog.db 路径"),
):
    """切换 catalog.db 路径（自动迁移）。"""
    from comfy_mgr.result import ServiceError
    services = build_services()
    result = services["settings"].migrate_db_path(Path(new_path))
    if result.ok:
        typer.echo(f"✓ catalog.db 已迁移到 {new_path}")
    else:
        typer.echo(f"✗ 迁移失败: {result.error.message}", err=True)
        raise typer.Exit(code=1)


@torch_app.command("detect")
def torch_detect():
    """检测当前系统的 CUDA 环境。"""
    result = CudaDetector.detect()
    if not result.ok:
        typer.echo(f"✗ 检测失败: {result.error.message}", err=True)
        raise typer.Exit(code=1)
    info = result.value
    if not info.available:
        typer.echo("未检测到 NVIDIA GPU（nvidia-smi 不可用）")
        typer.echo("建议: cu=cpu")
        return
    typer.echo(f"GPU: {info.gpu_name}")
    typer.echo(f"驱动版本: {info.driver_version}")
    typer.echo(f"最大支持 CUDA: {info.max_cuda_version}")
    suggestions = CudaDetector.suggest_cu_version(info)
    typer.echo(f"推荐 cu 版本: {', '.join(suggestions)}")


@torch_app.command("init")
def torch_init(
    env: str = typer.Option(..., "--env", help="环境名"),
    cu: str | None = typer.Option(None, "--cu", help="cu 版本（cu118/cu124/cu126/cpu）"),
    non_interactive: bool = typer.Option(False, "--non-interactive", help="非交互模式"),
):
    """为已存在环境生成 torch 配置（写入 envs/<name>/.torch-config.yaml）。"""
    services = build_services()
    env_obj = next((e for e in services["env"].list_all() if e.name == env), None)
    if not env_obj:
        typer.echo(f"✗ 环境 {env} 不存在", err=True)
        raise typer.Exit(code=1)
    cuda_info = CudaDetector.detect()
    if not cuda_info.ok:
        typer.echo(f"✗ CUDA 检测失败: {cuda_info.error.message}", err=True)
        raise typer.Exit(code=1)
    info = cuda_info.value
    if cu is None:
        suggestions = CudaDetector.suggest_cu_version(info)
        if non_interactive or not info.available:
            cu = suggestions[0]
            typer.echo(f"非交互模式选择 cu={cu}")
        else:
            typer.echo(f"推荐: {', '.join(suggestions)}")
            cu = typer.prompt("请选择 cu 版本", default=suggestions[0])
    ver_result = VenvManager.get_python_version(env_obj.python_executable)
    py_ver = "3.10"
    if ver_result.ok:
        parts = ver_result.value.split()
        if len(parts) >= 2:
            py_ver = parts[1].rsplit(".", 1)[0]
    cfg = TorchConfig.default_for(cu, py_ver)
    cfg_path = env_obj.root_path / ".torch-config.yaml"
    cfg.save(cfg_path)
    typer.echo(f"✓ 配置写入 {cfg_path}")
    typer.echo(f"  cu={cu} torch={cfg.torch}")
    typer.echo(f"  安装命令: {cfg.install_command()}")
