from __future__ import annotations
import shutil
import sqlite3
from pathlib import Path
from typing import Literal
from comfy_mgr.infra.fs import FS
from comfy_mgr.infra.venv import VenvManager
from comfy_mgr.models.environment import Environment, EnvironmentRepo, PORT_BASE, generate_env_id
from comfy_mgr.result import Result, ServiceError


class EnvironmentService:
    def __init__(
        self,
        conn: sqlite3.Connection,
        project_root: Path,
        fs: FS,
        venv: VenvManager,
    ):
        self.conn = conn
        self.project_root = project_root
        self.fs = fs
        self.venv = venv
        self.repo = EnvironmentRepo(conn)
        self.envs_dir = project_root / "envs"
        self.envs_dir.mkdir(parents=True, exist_ok=True)

    def create(
        self,
        name: str,
        layout: Literal["shared", "independent"],
        python_path: Path,
        comfyui_source: Path | None = None,
        port: int | None = None,
    ) -> Result[Environment]:
        # 1. 校验 Python 解释器
        if not python_path.exists():
            return Result.fail(ServiceError(
                code="VENV_PYTHON_MISSING",
                message=f"Python 解释器不存在: {python_path}",
            ))
        # 2. 校验 shared 布局的 ComfyUI 源
        if layout == "shared" and (not comfyui_source or not comfyui_source.exists()):
            return Result.fail(ServiceError(
                code="COMFYUI_SOURCE_MISSING",
                message="shared 布局必须指定已存在的 ComfyUI 源",
            ))
        # 3. 检查名称唯一
        for e in self.repo.list_all():
            if e.name == name:
                return Result.fail(ServiceError(
                    code="ENV_NAME_DUPLICATE",
                    message=f"环境名 {name} 已存在",
                ))
        # 4. 分配端口
        if port is None:
            port = self._next_port()
        # 5. 创建目录
        env_id = generate_env_id()
        root_path = self.envs_dir / name
        if root_path.exists() and any(root_path.iterdir()):
            return Result.fail(ServiceError(
                code="ENV_PATH_NOT_EMPTY",
                message=f"目标路径 {root_path} 非空",
            ))
        self.fs.ensure_dir(root_path)
        # 6. 链接 / 复制 ComfyUI
        comfyui_link = root_path / "ComfyUI"
        if layout == "shared":
            jr = self.fs.create_junction(comfyui_link, comfyui_source)
            if not jr.ok:
                return jr
            comfyui_resolved = comfyui_source
        else:
            cr = self.fs.copy_directory(comfyui_source, comfyui_link)
            if not cr.ok:
                return cr
            comfyui_resolved = comfyui_link
        # 7. 创建 venv
        venv_path = root_path / "venv"
        vr = self.venv.create(python_path, venv_path)
        if not vr.ok:
            return vr
        # 8. 写 extra_model_paths.yaml（M0: 占位）
        extra_yaml = root_path / "extra_model_paths.yaml"
        extra_yaml.write_text("# TODO: M1 填充\n", encoding="utf-8")
        # 9. 构造 Environment 并入库
        env = Environment(
            id=env_id,
            name=name,
            root_path=root_path,
            comfyui_layout=layout,
            comfyui_source=comfyui_resolved,
            venv_path=venv_path,
            python_executable=venv_path / "Scripts" / "python.exe",
            custom_nodes_path=root_path / "custom_nodes",
            extra_model_paths_yaml=extra_yaml,
            port=port,
        )
        self.fs.ensure_dir(env.custom_nodes_path)
        save_result = self.repo.save(env)
        if not save_result.ok:
            return save_result
        return Result.ok(env)

    def list_all(self) -> list[Environment]:
        return self.repo.list_all()

    def get(self, env_id: str) -> Environment | None:
        return self.repo.get(env_id)

    def delete(self, env_id: str, force: bool = False) -> Result[None]:
        env = self.repo.get(env_id)
        if not env:
            return Result.fail(ServiceError(
                code="ENV_NOT_FOUND",
                message=f"环境 {env_id} 不存在",
            ))
        if env.status == "running" and not force:
            return Result.fail(ServiceError(
                code="ENV_RUNNING",
                message="环境正在运行，请先停止或使用 --force",
                recoverable=True,
            ))
        # 移除 junction / 目录
        if env.root_path.exists():
            shutil.rmtree(env.root_path, ignore_errors=True)
        return self.repo.delete(env_id)

    def clone(self, src_env_id: str, new_name: str) -> Result[Environment]:
        src = self.repo.get(src_env_id)
        if not src:
            return Result.fail(ServiceError(
                code="ENV_NOT_FOUND",
                message=f"源环境 {src_env_id} 不存在",
            ))
        # 克隆布局：shared 仍 shared，independent 仍 independent
        return self.create(
            name=new_name,
            layout=src.comfyui_layout,  # type: ignore
            python_path=src.python_executable,
            comfyui_source=src.comfyui_source,
        )

    def _next_port(self) -> int:
        used = {e.port for e in self.repo.list_all()}
        port = PORT_BASE
        while port in used:
            port += 1
        return port
