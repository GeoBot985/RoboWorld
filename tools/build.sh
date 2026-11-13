#!/usr/bin/env sh
set -eu
ROOT="$(CDPATH= cd -- "$(dirname -- "$0")/.." && pwd)"
cd "$ROOT"
DOTNET="${DOTNET:-dotnet}"
PYTHON="${PYTHON:-python3}"
"$PYTHON" tools/validate_examples.py
"$DOTNET" build RoboWorld.slnx --configuration Release --nologo
"$DOTNET" build godot/RoboWorld.Godot.csproj --configuration Release --nologo
"$DOTNET" run --project src/RoboWorld.RobotService/RoboWorld.RobotService.csproj --configuration Release --no-build
"$PYTHON" python/controller.py
