$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$sourceRoot = Join-Path $root '.toolchain/renode-source-v1.17.0'
$infrastructure = Join-Path $sourceRoot 'src/Infrastructure'
$infraProject = Join-Path $infrastructure 'src/Infrastructure.csproj'
$renodeProject = Join-Path $sourceRoot 'src/Renode/Renode.csproj'
$resourcesRoot = Join-Path $root '.toolchain/renode-resources'
$resourcesDestination = Join-Path $sourceRoot 'lib/resources'
$patchPath = Join-Path $root 'tools/renode-patches/renode-1.17.0-loopback-monitor.patch'
$controlPatchPath = Join-Path $root 'tools/renode-patches/renode-1.17.0-loopback-external-control.patch'
$dotnet = Join-Path $root '.toolchain/dotnet/dotnet.exe'
$renodeRuntime = Join-Path $root '.toolchain/renode/renode_1.17.0-portable'
$buildLog = Join-Path $root 'artifacts/evidence/T05/renode-loopback-build.log'
$renodeBuildLog = Join-Path $root 'artifacts/evidence/T05/renode-loopback-application-build.log'
$sourceCommit = 'ab721d88e135a1bcb8ed2ecc5a38f51cbe61fdd2'
$infrastructureCommit = '066a7f13c052215632d469c995c89aea37c573b1'
$resourcesCommit = '9d7824771f3db67663950cae06f5bb7f89fbf33b'
$stockInfrastructureSha256 = 'D292E87A787FC6047037CE9B667CD1D345181B6182D71B5B431653AEA1B8B52E'
$stockRenodeSha256 = '94D745550FEE2FB3AC0CB484A80BB722E85B67A345FF367F72E08D59E33EA0C4'
$patchManifest = Join-Path $renodeRuntime 'RoboWorld-Loopback-Patch.json'

foreach ($path in @($dotnet, $patchPath, $controlPatchPath, $renodeRuntime)) {
    if (-not (Test-Path -LiteralPath $path)) { throw "Required pinned Renode build input is missing: $path" }
}

if (-not (Test-Path -LiteralPath $sourceRoot)) {
    & git clone --depth 1 --filter=blob:none --recurse-submodules --shallow-submodules --branch v1.17.0 https://github.com/renode/renode.git $sourceRoot
    if ($LASTEXITCODE -ne 0) { throw 'Could not clone the pinned Renode v1.17.0 source and submodules.' }
}
if ((& git -C $sourceRoot rev-parse HEAD).Trim() -ne $sourceCommit) { throw 'Renode source checkout does not match the pinned v1.17.0 source commit.' }
if ((& git -C $infrastructure rev-parse HEAD).Trim() -ne $infrastructureCommit) { throw 'Renode Infrastructure submodule does not match the source tag.' }

if (-not (Test-Path -LiteralPath $resourcesRoot)) {
    & git clone --no-checkout https://github.com/renode/renode-resources.git $resourcesRoot
    if ($LASTEXITCODE -ne 0) { throw 'Could not clone Renode build resources.' }
}
$resourceHead = (& git -C $resourcesRoot rev-parse HEAD).Trim()
if ($resourceHead -ne $resourcesCommit) {
    & git -C $resourcesRoot fetch --depth 1 origin $resourcesCommit
    if ($LASTEXITCODE -ne 0) { throw 'Could not fetch the pinned Renode build resources.' }
    & git -C $resourcesRoot checkout --detach FETCH_HEAD
    if ($LASTEXITCODE -ne 0) { throw 'Could not check out the pinned Renode build resources.' }
}

$socketSource = Join-Path $infrastructure 'src/Emulator/Main/Utilities/SocketIOSource.cs'
$providerSource = Join-Path $infrastructure 'src/Emulator/Main/Utilities/SocketServerProvider.cs'
$socketText = Get-Content -LiteralPath $socketSource -Raw
$providerText = Get-Content -LiteralPath $providerSource -Raw
$uartText = Get-Content -LiteralPath (Join-Path $infrastructure 'src/Emulator/Extensions/Analyzers/SocketUartAnalyzer.cs') -Raw
$terminalText = Get-Content -LiteralPath (Join-Path $infrastructure 'src/Emulator/Main/Backends/Terminals/ServerSocketTerminal.cs') -Raw
$gdbText = Get-Content -LiteralPath (Join-Path $infrastructure 'src/Emulator/Main/Utilities/GDB/GdbStub.cs') -Raw
$controlText = Get-Content -LiteralPath (Join-Path $sourceRoot 'src/Renode/Network/ExternalControl/ExternalControlSocket.cs') -Raw
$alreadyPatched = $socketText.Contains('bindAddress: IPAddress.Loopback') -and $providerText.Contains('IPAddress bindAddress = null') -and $uartText.Contains('bindAddress: IPAddress.Loopback') -and $terminalText.Contains('bindAddress: IPAddress.Loopback') -and $gdbText.Contains('serverName: "GDB", bindAddress: IPAddress.Loopback') -and $controlText.Contains('Bind(new IPEndPoint(IPAddress.Loopback, port))') -and $controlText.Contains('var headerBuffer = new byte[Message.HeaderSize];') -and -not $controlText.Contains('stackalloc byte[Message.HeaderSize]')
if ($alreadyPatched) {
    # The exact patch has already been applied to this source checkout.
} else {
    & git -C $infrastructure apply --check $patchPath
    if ($LASTEXITCODE -ne 0) { throw 'Renode source is dirty or the loopback patch no longer applies cleanly.' }
    & git -C $infrastructure apply $patchPath
    if ($LASTEXITCODE -ne 0) { throw 'Could not apply the loopback-only Renode monitor patch.' }
    & git -C $sourceRoot apply --check $controlPatchPath
    if ($LASTEXITCODE -ne 0) { throw 'Renode root source is dirty or the external-control loopback patch no longer applies cleanly.' }
    & git -C $sourceRoot apply $controlPatchPath
    if ($LASTEXITCODE -ne 0) { throw 'Could not apply the Renode external-control loopback patch.' }
}

New-Item -ItemType Directory -Force -Path $resourcesDestination | Out-Null
Get-ChildItem -LiteralPath $resourcesRoot -Force | Where-Object Name -ne '.git' | ForEach-Object {
    Copy-Item -LiteralPath $_.FullName -Destination $resourcesDestination -Recurse -Force
}

$targets = @'
<Project>
  <PropertyGroup>
    <TargetFrameworks>net8.0</TargetFrameworks>
    <EnableWindowsTargeting>true</EnableWindowsTargeting>
  </PropertyGroup>
</Project>
'@
[System.IO.File]::WriteAllText((Join-Path $sourceRoot 'Directory.Build.targets'), $targets, [System.Text.UTF8Encoding]::new($false))
$env:ROOT_PATH = $sourceRoot
New-Item -ItemType Directory -Force -Path (Split-Path -Parent $buildLog) | Out-Null
& $dotnet build $infraProject -c Release --nologo -p:GUI_DISABLED=true *> $buildLog
if ($LASTEXITCODE -ne 0) { Get-Content -LiteralPath $buildLog; throw 'Patched Renode Infrastructure build failed.' }
& $dotnet build $renodeProject -c Release --nologo -p:GUI_DISABLED=true -p:RequireWPF=false *> $renodeBuildLog
if ($LASTEXITCODE -ne 0) { Get-Content -LiteralPath $renodeBuildLog; throw 'Patched Renode executable build failed.' }

$runtimeInfrastructure = Join-Path $renodeRuntime 'Infrastructure.dll'
$runtimeRenode = Join-Path $renodeRuntime 'Renode.dll'
$runtimeInfrastructureHash = (Get-FileHash -Algorithm SHA256 -LiteralPath $runtimeInfrastructure).Hash
$runtimeRenodeHash = (Get-FileHash -Algorithm SHA256 -LiteralPath $runtimeRenode).Hash
$previous = if (Test-Path -LiteralPath $patchManifest) { Get-Content -LiteralPath $patchManifest -Raw | ConvertFrom-Json } else { $null }
if ($runtimeInfrastructureHash -ne $stockInfrastructureSha256 -and (!$previous -or $previous.infrastructure_sha256 -ne $runtimeInfrastructureHash)) {
    throw "Refusing to replace an unknown Renode Infrastructure.dll ($runtimeInfrastructureHash)."
}
if ($runtimeRenodeHash -ne $stockRenodeSha256 -and (!$previous -or $previous.renode_sha256 -ne $runtimeRenodeHash)) {
    throw "Refusing to replace an unknown Renode.dll ($runtimeRenodeHash)."
    }
$builtInfrastructure = Join-Path $sourceRoot 'src/Infrastructure/src/bin/Release/net8.0/Infrastructure.dll'
$builtRenode = Join-Path $sourceRoot 'output/bin/Release/Renode.dll'
if (-not (Test-Path -LiteralPath $builtRenode)) { throw "Renode executable assembly missing from source build output: $builtRenode" }
Copy-Item -LiteralPath $builtInfrastructure -Destination $runtimeInfrastructure -Force
Copy-Item -LiteralPath $builtRenode -Destination $runtimeRenode -Force
$newInfrastructureHash = (Get-FileHash -Algorithm SHA256 -LiteralPath $runtimeInfrastructure).Hash
$newRenodeHash = (Get-FileHash -Algorithm SHA256 -LiteralPath $runtimeRenode).Hash
$manifest = [ordered]@{ infrastructure_sha256 = $newInfrastructureHash; renode_sha256 = $newRenodeHash }
[System.IO.File]::WriteAllText($patchManifest, ($manifest | ConvertTo-Json -Compress), [System.Text.Encoding]::ASCII)
$patchSha256 = @(
    (Get-FileHash -Algorithm SHA256 -LiteralPath $patchPath).Hash
    (Get-FileHash -Algorithm SHA256 -LiteralPath $controlPatchPath).Hash
) -join ','
$summary = @(
    'Renode 1.17.0 local control endpoints build: PASS'
    "Renode source commit: $sourceCommit"
    "Infrastructure source commit: $infrastructureCommit"
    "Renode resources commit: $resourcesCommit"
    "Patch SHA-256 (infrastructure, root): $patchSha256"
    "Infrastructure.dll SHA-256: $newInfrastructureHash"
    "Renode.dll SHA-256: $newRenodeHash"
)
$summary | Set-Content -LiteralPath (Join-Path (Split-Path -Parent $buildLog) 'renode-loopback-summary.txt') -Encoding utf8
Write-Host "Renode 1.17.0 local control endpoint patch built and installed. Infrastructure.dll SHA-256: $newInfrastructureHash; Renode.dll SHA-256: $newRenodeHash"
