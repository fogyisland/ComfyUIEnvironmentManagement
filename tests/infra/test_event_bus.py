"""EventBus 轻量事件总线测试。"""
from __future__ import annotations
from comfy_mgr.infra.event_bus import EventBus


def test_emit_invokes_handlers():
    bus = EventBus()
    calls = []
    bus.on("foo", lambda x: calls.append(x))
    bus.emit("foo", 42)
    assert calls == [42]


def test_emit_no_handlers_does_not_raise():
    bus = EventBus()
    bus.emit("nothing")  # 不挂


def test_on_supports_multiple_handlers():
    bus = EventBus()
    a, b = [], []
    bus.on("x", lambda v: a.append(v))
    bus.on("x", lambda v: b.append(v))
    bus.emit("x", 1)
    assert a == [1]
    assert b == [1]


def test_off_removes_specific_handler():
    bus = EventBus()
    a, b = [], []
    handler_a = lambda v: a.append(v)
    bus.on("x", handler_a)
    bus.on("x", lambda v: b.append(v))
    bus.off("x", handler_a)
    bus.emit("x", 1)
    assert a == []
    assert b == [1]


def test_emit_passes_multiple_args_kwargs():
    bus = EventBus()
    received = []
    bus.on("ev", lambda a, b, c=None: received.append((a, b, c)))
    bus.emit("ev", 1, 2, c=3)
    assert received == [(1, 2, 3)]


def test_emit_handler_exception_does_not_stop_others():
    bus = EventBus()
    a, b = [], []
    bus.on("x", lambda v: (_ for _ in ()).throw(RuntimeError("boom")) if v else None)
    bus.on("x", lambda v: a.append(v))
    bus.on("x", lambda v: b.append(v))
    # 第一个 handler 抛错不阻断后续(简化:逐个 try/except)
    # 我们希望同步 emit,handler 异常被吞掉并记录
    bus.emit("x", 1)
    # 实现可能选择: 第一个抛错就停 / 不停。这里我们要求"不停"
    assert a == [1]
    assert b == [1]
