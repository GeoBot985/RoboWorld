$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$dotnet = Join-Path $root '.toolchain/dotnet/dotnet.exe'
$project = Join-Path $root 'tests/RoboWorld.Domain.Tests/RoboWorld.Domain.Tests.csproj'
$executable = Join-Path $root 'tests/RoboWorld.Domain.Tests/bin/Release/net8.0/RoboWorld.Domain.Tests.exe'
$evidence = Join-Path $root 'artifacts/evidence/T07'
New-Item -ItemType Directory -Force -Path $evidence | Out-Null
& $dotnet build $project --configuration Release --nologo
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
$previousDotnetRoot = $env:DOTNET_ROOT
$previousDotnetRootX64 = $env:DOTNET_ROOT_X64
try {
    $systemDotnet = Join-Path $env:ProgramFiles 'dotnet'
    $env:DOTNET_ROOT = $systemDotnet
    $env:DOTNET_ROOT_X64 = $systemDotnet
    & $executable 2>&1 | Tee-Object -FilePath (Join-Path $evidence 'domain-validation.log')
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
} finally {
    $env:DOTNET_ROOT = $previousDotnetRoot
    $env:DOTNET_ROOT_X64 = $previousDotnetRootX64
}
