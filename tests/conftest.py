import pytest
import uuid
from pathlib import Path

from comfy_mgr.db.connection import get_connection, init_schema

PROJECT_ROOT = Path(__file__).parent.parent
TEMPLATE_DIR = PROJECT_ROOT / "template"

@pytest.fixture
def tmp_appdata(monkeypatch, tmp_path):
    """重定向 APPDATA 到临时目录，settings 和 db 都在这里。"""
    appdata = tmp_path / "appdata"
    appdata.mkdir()
    monkeypatch.setenv("APPDATA", str(appdata))
    return appdata

@pytest.fixture
def template_python():
    """返回 template/ 下第一个可用的 python.exe。"""
    if not TEMPLATE_DIR.exists():
        pytest.skip("template/ 目录不存在")
    for ver in ["3.10", "3.11", "3.12", "3.13", "3.14"]:
        p = TEMPLATE_DIR / ver / "python.exe"
        if p.exists():
            return p
    pytest.skip("template/ 下没有可用的 python.exe")


@pytest.fixture
def platform_mock(monkeypatch):
    """用于在 Windows 上模拟其他平台。"""
    return monkeypatch

# === M1 新增 ===

@pytest.fixture(scope="session")
def qapp():
    """pytest-qt 全局 QApplication 实例（M1 Bridge 测试需要）。"""
    from PySide6.QtWidgets import QApplication
    app = QApplication.instance() or QApplication([])
    yield app


# === M2 新增 ===

@pytest.fixture
def fake_env_with_nodes(tmp_path: Path):
    """
    共享 fixture:建一个 env + 5 个不同形式的 fake custom_node 包。
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
