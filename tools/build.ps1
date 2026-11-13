$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$dotnet = Join-Path $root '.toolchain/dotnet/dotnet.exe'
$python = Join-Path $root '.venv/Scripts/python.exe'
if (-not (Test-Path $dotnet)) { throw 'Workspace .NET SDK missing. Follow DEV_SETUP.md and provision the pinned SDK.' }
if (-not (Test-Path $python)) { throw 'Project Python environment missing. Follow DEV_SETUP.md.' }

Push-Location $root
try {
    & $python tools/validate_examples.py
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
    & $dotnet build RoboWorld.slnx --configuration Release --nologo
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
    & $dotnet build godot/RoboWorld.Godot.csproj --configuration Release --nologo
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
    & $dotnet run --project src/RoboWorld.RobotService/RoboWorld.RobotService.csproj --configuration Release --no-build
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
    & $python python/controller.py
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
    & powershell.exe -NoProfile -ExecutionPolicy Bypass -File (Join-Path $PSScriptRoot 'probe-arm.ps1')
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
    & powershell.exe -NoProfile -ExecutionPolicy Bypass -File (Join-Path $PSScriptRoot 'probe-renode.ps1')
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
    & powershell.exe -NoProfile -ExecutionPolicy Bypass -File (Join-Path $PSScriptRoot 'probe-godot.ps1')
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
} finally {
    Pop-Location
}
