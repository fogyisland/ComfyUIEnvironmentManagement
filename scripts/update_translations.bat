@echo off
chcp 65001 > nul
setlocal
cd /d "%~dp0\.."

echo [1/3] Extracting translatable strings via pyside6-lupdate...
poetry run pyside6-lupdate app/qml -ts app/qml/i18n/comfyui_manager_zh_CN.ts app/qml/i18n/comfyui_manager_en_US.ts
if errorlevel 1 goto :error

echo [2/3] Translating (placeholder: keep .ts as-is)...

echo [3/3] Compiling .ts to .qm via pyside6-lrelease...
poetry run pyside6-lrelease app/qml/i18n/comfyui_manager_zh_CN.ts
poetry run pyside6-lrelease app/qml/i18n/comfyui_manager_en_US.ts
if errorlevel 1 goto :error

echo [OK] Translations updated.
exit /b 0

:error
echo [ERROR] Translation update failed.
exit /b 1
