"""Bridge 测试共享 fixtures：service mocks。"""
from __future__ import annotations
from unittest.mock import MagicMock
import pytest


@pytest.fixture
def mock_m0_service() -> MagicMock:
    """NodeBridge.m0_service 占位 mock。"""
    return MagicMock()


@pytest.fixture
def mock_scanned_service() -> MagicMock:
    """NodeBridge.scanned (ScannedNodeService) 占位 mock。"""
    return MagicMock()


@pytest.fixture
def mock_conflict_service() -> MagicMock:
    """NodeBridge.conflict (ConflictService) 占位 mock。"""
    return MagicMock()


@pytest.fixture
def mock_meta_service() -> MagicMock:
    """NodeBridge.meta (NodeMetaService) 占位 mock。"""
    return MagicMock()


@pytest.fixture
def mock_version_service() -> MagicMock:
    """NodeBridge.version (VersionService) 占位 mock。"""
    return MagicMock()


@pytest.fixture
def mock_dep_service() -> MagicMock:
    """NodeBridge.dep (DepService) 占位 mock。"""
    return MagicMock()


@pytest.fixture
def mock_catalog_client() -> MagicMock:
    """NodeBridge.catalog (CatalogHTTPClient) 占位 mock。"""
    return MagicMock()


@pytest.fixture
def mock_compat_client() -> MagicMock:
    """NodeBridge.compat (CompatHTTPClient) 占位 mock。"""
    return MagicMock()


@pytest.fixture
def mock_install_service() -> MagicMock:
    """NodeBridge.install (InstallService) 占位 mock。"""
    return MagicMock()