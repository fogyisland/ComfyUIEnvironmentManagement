"""PySide6 入口（M1 最小骨架：只创建 QApplication 并退出）。"""
from __future__ import annotations
import sys
from PySide6.QtWidgets import QApplication


def main() -> None:
    app = QApplication(sys.argv)
    app.setOrganizationName("ComfyUI Manager")
    app.setApplicationName("ComfyUI Manager")
    print("ComfyUI Manager GUI placeholder (M1 Task 1)")
    sys.exit(0)


if __name__ == "__main__":
    main()
