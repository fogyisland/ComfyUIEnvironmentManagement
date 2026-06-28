"""M3 端到端:fake_env_with_nodes (扩展) → VersionService.upgrade 真 git 操作。

如果 bin/git-portable 不存在,跳过(M3 spec §10 风险:首次 setup 需联网下载)。
"""
import shutil
from pathlib import Path
import subprocess

import pytest

from comfy_mgr.db.connection import get_connection, init_schema
from comfy_mgr.db.scanned_node_repo import ScannedNodeRepo
from comfy_mgr.db.version_repo import VersionRepo
from comfy_mgr.infra.event_bus import EventBus
from comfy_mgr.services.version import VersionService


GIT_PORTABLE = Path("bin/git-portable/cmd/git.exe")


@pytest.mark.skipif(
    not GIT_PORTABLE.exists() and shutil.which("git") is None,
    reason="git 不可用(无 portable + 无系统 git)",
)
def test_upgrade_with_real_git(tmp_path: Path):
    """真打 git:克隆一个公开仓库,upgrade 到 origin/HEAD。"""
    # 1. 准备本地"远程":tmp_path/remote.git
    remote_dir = tmp_path / "remote.git"
    subprocess.run(["git", "init", "--bare", str(remote_dir)],
                   check=True, capture_output=True)

    # 2. 准备一个 work repo,push 2 个 commit
    work_dir = tmp_path / "work"
    work_dir.mkdir()
    subprocess.run(["git", "-c", "init.defaultBranch=main", "init"],
                   cwd=work_dir, check=True, capture_output=True)
    subprocess.run(["git", "config", "user.email", "test@example.com"],
                   cwd=work_dir, check=True)
    subprocess.run(["git", "config", "user.name", "test"], cwd=work_dir,
                   check=True)
    (work_dir / "f.txt").write_text("v1")
    subprocess.run(["git", "add", "f.txt"], cwd=work_dir, check=True)
    subprocess.run(["git", "commit", "-m", "v1"], cwd=work_dir,
                   check=True, capture_output=True)
    subprocess.run(["git", "remote", "add", "origin", str(remote_dir)],
                   cwd=work_dir, check=True)
    subprocess.run(["git", "push", "-u", "origin", "main"], cwd=work_dir,
                   check=True, capture_output=True)
    # bare repo HEAD 需指向 main,否则 origin/HEAD 解析失败
    subprocess.run(["git", "symbolic-ref", "HEAD", "refs/heads/main"],
                   cwd=remote_dir, check=True, capture_output=True)

    # 3. 准备 env + custom_nodes/foo + .git(用 work_dir clone 到 custom_nodes)
    conn = get_connection(tmp_path / "test.db")
    init_schema(conn)
    env_id = "env-1"
    custom_nodes = tmp_path / "env" / "custom_nodes"
    custom_nodes.mkdir(parents=True)
    pkg_dir = custom_nodes / "foo"
    subprocess.run(["git", "clone", str(remote_dir), str(pkg_dir)],
                   check=True, capture_output=True)
    (pkg_dir / ".git").exists()  # 真 git 仓库

    conn.execute(
        "INSERT INTO environments (id, name, root_path, comfyui_layout, "
        "venv_path, python_executable, custom_nodes_path, "
        "extra_model_paths_yaml, port) "
        "VALUES (?,?,?,?,?,?,?,?,?)",
        (env_id, "e1", str(tmp_path / "env"), "shared",
         str(tmp_path / "venv"), str(tmp_path / "venv" / "python"),
         str(custom_nodes), str(tmp_path / "emp.yaml"), 8188),
    )

    # 4. scanned_nodes 写一行
    import uuid
    from datetime import datetime
    from comfy_mgr.models.scanned_node import ScannedNode
    ScannedNodeRepo(conn).upsert(ScannedNode(
        id=f"sn-{uuid.uuid4().hex[:8]}",
        env_id=env_id, package="foo", package_path=pkg_dir,
        status="enabled", scan_meta={},
        last_scanned_at=datetime.now().isoformat(timespec="seconds"),
    ))

    # 5. push v2 到 remote
    (work_dir / "f.txt").write_text("v2")
    subprocess.run(["git", "add", "f.txt"], cwd=work_dir, check=True)
    subprocess.run(["git", "commit", "-m", "v2"], cwd=work_dir,
                   check=True, capture_output=True)
    subprocess.run(["git", "push"], cwd=work_dir, check=True,
                   capture_output=True)

    # 6. VersionService.upgrade
    git_resolver = (lambda: GIT_PORTABLE) if GIT_PORTABLE.exists() \
                   else (lambda: Path(shutil.which("git")))
    svc = VersionService(
        version_repo=VersionRepo(conn),
        scanned_repo=ScannedNodeRepo(conn),
        conn=conn, event_bus=EventBus(),
        git_exe_resolver=git_resolver,
    )
    r = svc.upgrade(env_id, "foo")
    assert r.ok
    history = VersionRepo(conn).list_by_env_and_package(env_id, "foo")
    assert len(history) == 1
    assert history[0]["result"] == "success"
    assert (pkg_dir / "f.txt").read_text() == "v2"


def test_integration_list_status_with_real_git(tmp_path: Path):
    """list_status 读 git rev-parse + 检测 has_remote。"""
    if not GIT_PORTABLE.exists() and shutil.which("git") is None:
        pytest.skip("git 不可用")

    # 真 git init 一个目录
    pkg_dir = tmp_path / "pkg"
    pkg_dir.mkdir()
    subprocess.run(["git", "init"], cwd=pkg_dir, check=True,
                   capture_output=True)
    subprocess.run(["git", "config", "user.email", "t@e.com"],
                   cwd=pkg_dir, check=True)
    subprocess.run(["git", "config", "user.name", "t"], cwd=pkg_dir,
                   check=True)
    (pkg_dir / "x").write_text("x")
    subprocess.run(["git", "add", "x"], cwd=pkg_dir, check=True)
    subprocess.run(["git", "commit", "-m", "init"], cwd=pkg_dir,
                   check=True, capture_output=True)

    conn = get_connection(tmp_path / "test.db")
    init_schema(conn)
    conn.execute(
        "INSERT INTO environments (id, name, root_path, comfyui_layout, "
        "venv_path, python_executable, custom_nodes_path, "
        "extra_model_paths_yaml, port) "
        "VALUES (?,?,?,?,?,?,?,?,?)",
        ("env-1", "e1", "/e", "shared", "/v", "/v/p", str(pkg_dir.parent),
         "/e.yaml", 8188),
    )
    from comfy_mgr.models.scanned_node import ScannedNode
    import uuid
    from datetime import datetime
    ScannedNodeRepo(conn).upsert(ScannedNode(
        id=f"sn-{uuid.uuid4().hex[:8]}",
        env_id="env-1", package="pkg", package_path=pkg_dir,
        status="enabled", scan_meta={},
        last_scanned_at=datetime.now().isoformat(timespec="seconds"),
    ))
    git_resolver = (lambda: GIT_PORTABLE) if GIT_PORTABLE.exists() \
                   else (lambda: Path(shutil.which("git")))
    svc = VersionService(
        version_repo=VersionRepo(conn),
        scanned_repo=ScannedNodeRepo(conn),
        conn=conn, event_bus=EventBus(),
        git_exe_resolver=git_resolver,
    )
    r = svc.list_status("env-1", "pkg")
    assert r.ok
    assert r.value[0]["has_remote"] is True
    assert len(r.value[0]["current_sha"]) == 40  # git SHA-1


def test_integration_catalog_offline_degradation(tmp_path: Path):
    """离线场景:CatalogHTTPClient HTTP 失败 → 返回 stale cache + stale=True。"""
    from comfy_mgr.db.catalog_repo import CatalogCacheRepo
    from comfy_mgr.infra.catalog_http_client import CatalogHTTPClient
    from comfy_mgr.infra.http_client import HTTPClient

    conn = get_connection(tmp_path / "test.db")
    init_schema(conn)
    repo = CatalogCacheRepo(conn)

    # 预填一个过期的 cache
    repo.upsert({
        "id": "cc-stale", "source_url": "https://api.comfy.org/nodes",
        "package": "Old-Pkg",
        "raw_metadata": '{"id": "Old-Pkg", "stars": 42}',
        "cached_at": "2020-01-01T00:00:00",
        "expires_at": "2020-01-01T01:00:00",
    })

    # 配一个 100% 失败的 HTTPClient(用 unreachable host)
    http = HTTPClient(timeout=0.1, max_retries=0)
    client = CatalogHTTPClient(
        catalog_repo=repo, http_client=http,
        base_url="http://127.0.0.1:1",  # 永远连不上
    )
    r = client.list_remote()
    assert r.ok  # 离线降级仍然 ok
    assert r.value[0]["id"] == "Old-Pkg"
    assert r.value[0]["stale"] is True
