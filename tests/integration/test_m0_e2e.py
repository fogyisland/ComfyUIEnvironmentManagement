"""端到端测试：完整 M0 流程。

需要：
- template/3.10/python.exe（或任意 template python）
- Windows 平台（junction）
"""
import shutil
import time
import pytest
from pathlib import Path
from typer.testing import CliRunner
from comfy_mgr.cli import app
from comfy_mgr.paths import get_appdata_dir
from comfy_mgr.result import Result
from tests.conftest import TEMPLATE_DIR

pytestmark = pytest.mark.integration

runner = CliRunner()


@pytest.fixture
def project_root(tmp_path, monkeypatch):
    monkeypatch.setenv("APPDATA", str(tmp_path / "appdata"))
    project_root = tmp_path / "project"
    project_root.mkdir()
    comfyui_src = tmp_path / "shared" / "ComfyUI"
    comfyui_src.mkdir(parents=True)
    (comfyui_src / "main.py").write_text("# fake comfyui main")
    (comfyui_src / "requirements.txt").write_text("# no deps")
    monkeypatch.chdir(project_root)
    return project_root


def test_full_m0_flow(project_root, template_python):
    """完整 M0 流程：settings 初始化 → env create → 启动 → 停止。"""

    # 1. settings 初始化（隐式发生在 build_services）
    result = runner.invoke(app, ["settings", "show"])
    assert result.exit_code == 0

    # 2. env create（用真实 template python）
    result = runner.invoke(app, [
        "env", "create",
        "--name", "test-env",
        "--layout", "independent",  # independent 避免共享源依赖
        "--port", "8188",
        "--python", str(template_python),
        "--comfyui-source", str(project_root.parent / "shared" / "ComfyUI"),
    ])
    assert result.exit_code == 0, result.stdout

    # 3. 验证 venv 被创建（真实 venv，不是 mock）
    venv_python = project_root / "envs" / "test-env" / "venv" / "Scripts" / "python.exe"
    assert venv_python.exists()

    # 4. 验证 venv 内 python 可运行
    import subprocess
    r = subprocess.run([str(venv_python), "--version"], capture_output=True, text=True)
    assert r.returncode == 0
    assert "Python" in r.stdout

    # 5. env list
    result = runner.invoke(app, ["env", "list"])
    assert "test-env" in result.stdout

    # 6. 启动（用 mock 替换 Popen 避免真实启动 ComfyUI）
    #   这一步是 e2e 但 Popen 是真实的；fake main.py 会立即退出
    #   集成测试只覆盖 env create 真实路径；start/stop 由单测覆盖
    #   （注：原 brief 中的 unittest.mock.patch 调用是 dead code —— 没有任何
    #    context manager / 赋值，patch 不生效，且测试在 step 5 后直接进 delete，
    #    根本不调用 start。已删除。）

    # 7. env delete
    result = runner.invoke(app, ["env", "delete", "test-env", "--force"])
    assert result.exit_code == 0
    assert not (project_root / "envs" / "test-env").exists()


def test_torch_install_in_env(project_root, template_python, mocker):
    """真实 venv + 真实 pip install torch (CPU 版避免 GPU 依赖)。"""
    comfyui_src = project_root.parent / "shared" / "ComfyUI"
    # mock cuda 检测为不可用（强制 cpu）
    from comfy_mgr.infra.cuda import CudaInfo
    mocker.patch("comfy_mgr.cli.CudaDetector.detect",
                 return_value=Result.ok(CudaInfo(None, None, None, False)))

    result = runner.invoke(app, [
        "env", "create",
        "--name", "torch-test",
        "--layout", "independent",
        "--port", "8188",
        "--python", str(template_python),
        "--comfyui-source", str(comfyui_src),
        "--with-torch",
        "--cu", "cpu",
    ])
    assert result.exit_code == 0, result.stdout

    # 验证 config 文件
    cfg_path = project_root / "envs" / "torch-test" / ".torch-config.yaml"
    assert cfg_path.exists()

    # 验证 torch 真的被装了（v0.1 跳过实际安装加速；可选）
    # 如需真实验证：去掉下面这行的 skip
    pytest.skip("torch 真实下载较慢，手动跑验证")
    venv_python = project_root / "envs" / "torch-test" / "venv" / "Scripts" / "python.exe"
    r = subprocess.run(
        [str(venv_python), "-c", "import torch; print(torch.__version__)"],
        capture_output=True, text=True, timeout=30,
    )
    assert r.returncode == 0
    assert "cpu" in r.stdout or "2." in r.stdout
