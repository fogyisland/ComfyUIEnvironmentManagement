@echo off
chcp 65001 > nul
setlocal

cd /d "%~dp0"

where python > nul 2>&1
if errorlevel 1 (
    echo [ERROR] Python not in PATH. Install Python 3.10+ and ensure it's in PATH.
    pause
    exit /b 1
)

python -c "import PySide6" 2>nul
if errorlevel 1 (
    echo [INFO] PySide6 not found. Installing dependencies via poetry...
    where poetry > nul 2>&1
    if errorlevel 1 (
        echo [ERROR] poetry not found. Install with: pip install poetry
        pause
        exit /b 1
    )
    poetry install || (echo [ERROR] poetry install failed & pause & exit /b 1)
)

poetry run comfy-mgr-gui
