#!/usr/bin/env bash
# Builds KtcMonitoringPortal-Setup-<version>.exe from Linux/macOS/Windows(Git Bash). Requires: .NET 8 SDK, NSIS (makensis).
set -euo pipefail
cd "$(dirname "$0")/.."
VERSION="${1:-1.0.0}"
rm -rf installer/publish
dotnet publish src/Ktc.Web -c Release -r win-x64 --self-contained true \
  -p:PublishSingleFile=false -p:DebugType=none -p:Version="$VERSION" -o installer/publish
rm -f installer/publish/appsettings.Development.json installer/publish/web.config
makensis -V2 -DVERSION="$VERSION" installer/ktc-portal.nsi
ls -lh installer/KtcMonitoringPortal-Setup-"$VERSION".exe
