"""ProcessStateRepo 测试。"""
import sqlite3
from datetime import datetime
from comfy_mgr.db.connection import get_connection, init_schema
from comfy_mgr.models.process_state import ProcessState, ProcessStateRepo


def _insert_env(conn, env_id: str) -> None:
    """插入一个 environments 父行，满足 process_state 的 FK 约束。"""
    conn.execute("""
        INSERT INTO environments (id, name, root_path, comfyui_layout,
            venv_path, python_executable, custom_nodes_path,
            extra_model_paths_yaml, port)
        VALUES (?, ?, 'C:/envs/' || ?, 'shared', 'C:/envs/' || ? || '/v',
            'C:/envs/' || ? || '/v/Scripts/python.exe', 'C:/envs/' || ? || '/cn',
            'C:/envs/' || ? || '/extra.yaml', 8188)
    """, (env_id, env_id, env_id, env_id, env_id, env_id, env_id))


def test_save_and_get(tmp_path):
    conn = get_connection(tmp_path / "test.db")
    init_schema(conn)
    _insert_env(conn, "env-abc")
    repo = ProcessStateRepo(conn)
    state = ProcessState(
        env_id="env-abc", pid=1234, port=8188,
        started_at=datetime(2026, 6, 25, 10, 0, 0),
    )
    r = repo.save(state)
    assert r.ok
    got = repo.get("env-abc")
    assert got is not None
    assert got.pid == 1234
    assert got.port == 8188


def test_save_updates_existing(tmp_path):
    conn = get_connection(tmp_path / "test.db")
    init_schema(conn)
    _insert_env(conn, "e1")
    repo = ProcessStateRepo(conn)
    repo.save(ProcessState("e1", 1, 8188, datetime.now()))
    repo.save(ProcessState("e1", 2, 8189, datetime.now()))
    got = repo.get("e1")
    assert got.pid == 2
    assert got.port == 8189


def test_delete_removes_row(tmp_path):
    conn = get_connection(tmp_path / "test.db")
    init_schema(conn)
    _insert_env(conn, "e1")
    repo = ProcessStateRepo(conn)
    repo.save(ProcessState("e1", 1, 8188, datetime.now()))
    repo.delete("e1")
    assert repo.get("e1") is None


def test_list_all_returns_all(tmp_path):
    conn = get_connection(tmp_path / "test.db")
    init_schema(conn)
    _insert_env(conn, "e1")
    _insert_env(conn, "e2")
    repo = ProcessStateRepo(conn)
    repo.save(ProcessState("e1", 1, 8188, datetime.now()))
    repo.save(ProcessState("e2", 2, 8189, datetime.now()))
    states = repo.list_all()
    assert {s.env_id for s in states} == {"e1", "e2"}


def test_cascade_delete_with_environment(tmp_path):
    """删 environment 时 process_state 应被级联删除。"""
    conn = get_connection(tmp_path / "test.db")
    init_schema(conn)
    conn.execute("PRAGMA foreign_keys=ON")
    conn.execute("""
        INSERT INTO environments (id, name, root_path, comfyui_layout,
            venv_path, python_executable, custom_nodes_path,
            extra_model_paths_yaml, port)
        VALUES ('e1', 'n1', 'C:/envs/n1', 'shared', 'C:/envs/n1/v',
            'C:/envs/n1/v/Scripts/python.exe', 'C:/envs/n1/cn',
            'C:/envs/n1/extra.yaml', 8188)
    """)
    repo = ProcessStateRepo(conn)
    repo.save(ProcessState("e1", 1, 8188, datetime.now()))
    conn.execute("DELETE FROM environments WHERE id = 'e1'")
    assert repo.get("e1") is None
