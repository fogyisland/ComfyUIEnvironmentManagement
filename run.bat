@echo off
setlocal
cd /d %~dp0

REM Launch the ComfyUI Manager GUI using the bundled portable Python.
REM - PySide6 resolves from user-site-packages (C:\Users\<user>\AppData\Roaming\Python\Python310\site-packages).
REM - The project uses a src/ layout; `app/main.py` does `from app.app_context import ...` and
REM   `app_context.py` does `from comfy_mgr.db.connection import ...`. Both `app/` and `src/`
REM   must be on PYTHONPATH for those imports to resolve under the portable interpreter.

set "PYTHONPATH=%CD%;%CD%\src;%PYTHONPATH%"

echo.
echo === ComfyUI Manager (M3 smoke build) ===
echo Python: %CD%\python\python.exe
echo PYTHONPATH: %PYTHONPATH%
echo.

python\python.exe app\main.py %*

if errorlevel 1 (
    echo.
    echo [run.bat] app\main.py exited with code %errorlevel%.
    pause
)

endlocal