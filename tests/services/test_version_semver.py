"""M4 T21: Semver 智能版本比较 + locked 检查。"""
from __future__ import annotations
import pytest
from pathlib import Path
from unittest.mock import MagicMock
from comfy_mgr.services.version import VersionService, _latest_semver
from comfy_mgr.result import Result


def test_latest_semver_picks_max():
    tags = ["v1.0.0", "v1.2.3", "v0.9.0", "v2.0.0-beta"]
    assert _latest_semver(tags) == "2.0.0-beta"


def test_latest_semver_skips_invalid():
    tags = ["v1.0.0", "release-2024", "v1.1.0"]
    assert _latest_semver(tags) == "1.1.0"


def test_latest_semver_empty():
    assert _latest_semver([]) is None


def test_downgrade_respects_locked():
    """downgrade 在节点 locked 时应返回 VERSION_LOCKED,不应执行 git。"""
    svc = VersionService(
        version_repo=MagicMock(), scanned_repo=MagicMock(),
        conn=MagicMock(), event_bus=MagicMock(),
        git_exe_resolver=MagicMock(return_value=Path("/fake/git")),
    )
    svc._get_locked = MagicMock(return_value=1)
    svc._fail_history = MagicMock(return_value=Result.fail(MagicMock()))
    svc._run_git = MagicMock()
    r = svc.downgrade("env-1", "pkg-a", "v1.0.0")
    svc._run_git.assert_not_called()


def test_rollback_respects_locked():
    svc = VersionService(
        version_repo=MagicMock(), scanned_repo=MagicMock(),
        conn=MagicMock(), event_bus=MagicMock(),
        git_exe_resolver=MagicMock(return_value=Path("/fake/git")),
    )
    svc._get_locked = MagicMock(return_value=1)
    svc.version_repo.get = MagicMock(return_value={
        "version_before": "v1.0.0",
    })
    r = svc.rollback("env-1", "pkg-a", "vh-1")
    assert not r.ok
    assert r.error.code == "VERSION_LOCKED"


def test_try_parse_semver_valid():
    assert VersionService._try_parse_semver("1.2.3") == "1.2.3"
    assert VersionService._try_parse_semver("v2.0.0-beta.1") == "2.0.0b1"


def test_try_parse_semver_invalid():
    assert VersionService._try_parse_semver("abc123def") is None
    assert VersionService._try_parse_semver(None) is None
