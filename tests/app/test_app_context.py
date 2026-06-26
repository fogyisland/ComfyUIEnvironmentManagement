"""AppContext 回归测试。"""
from __future__ import annotations


def test_AppContext_wires_bridge_sink_correctly(qapp, tmp_path, monkeypatch):
    """回归测试：AppContext 必须正确把 ProcessBridge._on_line 接入 ProcessService。

    原 bug：app_context.py 把 sink 设到 self.process.bridge_sink（错误属性名），
    但 ProcessService 实际存储在 self._bridge_sink。结果 live log 永远进不了 QML。
    """
    from app.app_context import AppContext

    # 使用隔离的 settings 目录，避免污染真实 AppData
    fake_appdata = tmp_path / "appdata"
    monkeypatch.setenv("APPDATA", str(fake_appdata))
    monkeypatch.setenv("XDG_CONFIG_HOME", str(tmp_path / "config"))

    ctx = AppContext(project_root=tmp_path)

    # 关键断言：_bridge_sink 必须被正确连接（不是 bridge_sink）
    # PySide6 QObject 子类的 bound method 每次访问都新建对象，所以比较 __func__
    assert ctx.process._bridge_sink is not None
    assert ctx.process._bridge_sink.__func__ is ctx.process_bridge._on_line.__func__
    # 并且：调用 _bridge_sink（callable）会追加到 bridge 的 _logs
    ctx.process._bridge_sink("env-test", "hello via sink")
    assert "hello via sink" in ctx.process_bridge.logsFor("env-test")


def test_AppContext_process_bridge_has_env_resolver(qapp, tmp_path, monkeypatch):
    """AppContext 必须把 env_resolver 注入到 ProcessBridge。"""
    from app.app_context import AppContext

    fake_appdata = tmp_path / "appdata"
    monkeypatch.setenv("APPDATA", str(fake_appdata))

    ctx = AppContext(project_root=tmp_path)
    # env_resolver should be wired and return None for unknown env
    assert ctx.process_bridge._env_resolver is not None
    assert ctx.process_bridge._env_resolver("nonexistent") is None