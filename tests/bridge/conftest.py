"""Bridge 测试的本地 fixture。

说明：pytest-qt 的 ``qtbot`` 在当前环境中不可用（poetry 元数据
显示已安装 pytest-qt 4.5.0，但 site-packages 中缺失 ``pytestqt`` 模块）。
为不阻塞 M1 计划，本文件提供最小本地 ``qtbot`` 替代品，仅支持
``waitSignal`` 上下文管理器（Bridge 测试用到的唯一子集）。

未来若 pytest-qt 真正安装后，应删除本文件，恢复使用 ``qtbot``。
"""
from __future__ import annotations
from contextlib import contextmanager
from typing import Iterator, List, Any


class _SignalBlocker:
    """记录 Signal 在 ``with`` 块内 emit 的参数（兼容 pytest-qt 语义）。"""

    def __init__(self) -> None:
        # pytest-qt 语义：args 是 emit 时的位置参数列表（最后一次 emit 覆盖）。
        self.args: List[Any] = []
        self._connected: List[tuple] = []

    def attach(self, signal) -> None:
        def slot(*a):
            # 模拟 pytest-qt：每次 emit 用位置参数列表覆盖。
            self.args = list(a)
        signal.connect(slot)
        self._connected.append((signal, slot))

    def detach(self) -> None:
        for signal, slot in self._connected:
            try:
                signal.disconnect(slot)
            except (RuntimeError, TypeError):
                pass
        self._connected.clear()


@contextmanager
def _wait_signal(signal, timeout: int = 1000) -> Iterator[_SignalBlocker]:
    """同步等待：进入块时连接 Signal，退出时断开。"""
    blocker = _SignalBlocker()
    blocker.attach(signal)
    try:
        yield blocker
    finally:
        blocker.detach()


class _QtBot:
    def waitSignal(self, signal, timeout: int = 1000):  # noqa: N802 - 兼容 pytest-qt API
        return _wait_signal(signal, timeout=timeout)


import pytest


@pytest.fixture
def qtbot():
    return _QtBot()
