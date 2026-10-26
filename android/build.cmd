@echo off
rem [AI-EDITION] Сгенерировано ИИ (Claude, Anthropic). Стабильность не гарантируется.
rem Двойной щелчок: собрать APK в android\dist. Параметры передаются в build.ps1, например: build.cmd -Pull
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0build.ps1" %*
pause
