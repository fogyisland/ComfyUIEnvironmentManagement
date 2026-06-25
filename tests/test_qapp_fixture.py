"""Smoke test for the global qapp fixture (tests/conftest.py)."""


def test_qapp_fixture_returns_qapplication(qapp):
    """Verify the qapp fixture wires up a real QApplication singleton."""
    from PySide6.QtWidgets import QApplication
    assert isinstance(qapp, QApplication)
    # applicationName() may be "" or set by pytest/other tests; the key is
    # that the fixture returns a live QApplication (not None / not a mock).
    assert qapp is QApplication.instance()