$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$releaseParent = Join-Path $root 'artifacts/release'
$packageRoot = Join-Path $releaseParent 'RoboWorld'
$stage = Join-Path $releaseParent ('.RoboWorld-staging-' + [guid]::NewGuid().ToString('N'))
$godot = Get-ChildItem (Join-Path $root '.toolchain/godot') -Filter 'Godot_v4.7.2-stable_mono_win64_console.exe' -Recurse | Select-Object -First 1 -ExpandProperty FullName
$dotnet = Join-Path $root '.toolchain/dotnet/dotnet.exe'
$renode = Join-Path $root '.toolchain/renode/renode_1.17.0-portable'
$firmware = Join-Path $root 'samples/firmware/stm32-drive/build/firmware.elf'
$armFirmware = Join-Path $root 'samples/firmware/stm32-arm/build/firmware.elf'
$fullReleaseParent = [IO.Path]::GetFullPath($releaseParent).TrimEnd([IO.Path]::DirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar
$fullPackageRoot = [IO.Path]::GetFullPath($packageRoot)
$fullStage = [IO.Path]::GetFullPath($stage)
if (-not $fullPackageRoot.StartsWith($fullReleaseParent, [StringComparison]::OrdinalIgnoreCase) -or -not $fullStage.StartsWith($fullReleaseParent, [StringComparison]::OrdinalIgnoreCase)) { throw 'Resolved release paths escaped artifacts/release.' }
foreach ($target in @('stm32-drive', 'stm32-arm')) {
    & (Join-Path $root "samples/firmware/$target/build.ps1") *> (Join-Path $root "artifacts/evidence/T06/$target-firmware-build.log")
    if ($LASTEXITCODE -ne 0) { throw "$target firmware build failed." }
}
if (-not (Test-Path $godot) -or -not (Test-Path $dotnet) -or -not (Test-Path $renode) -or -not (Test-Path $firmware) -or -not (Test-Path $armFirmware)) { throw 'Pinned Godot, .NET SDK, Renode or project firmware is missing. Build the workspace and run DEV_SETUP.md first.' }

New-Item -ItemType Directory -Force -Path $stage | Out-Null
try {
    $env:DOTNET_ROOT = Join-Path $root '.toolchain/dotnet'
    $env:DOTNET_ROOT_X64 = $env:DOTNET_ROOT
    $env:PATH = "$($env:DOTNET_ROOT);$env:PATH"

    & $dotnet publish (Join-Path $root 'src/RoboWorld.RobotService/RoboWorld.RobotService.csproj') --configuration Release --runtime win-x64 --self-contained true --output (Join-Path $stage 'runtime/robot-service') --nologo
    if ($LASTEXITCODE -ne 0) { throw 'Self-contained Robot Service publish failed.' }

    $exportLog = Join-Path $root 'artifacts/evidence/T06/godot-export.log'
    $exportStderr = Join-Path $root 'artifacts/evidence/T06/godot-export.stderr.log'
    New-Item -ItemType Directory -Force -Path (Split-Path $exportLog) | Out-Null
    $godotProjectPath = '"' + (Join-Path $root 'godot') + '"'
    $exportExePath = '"' + (Join-Path $stage 'RoboWorld.exe') + '"'
    $exportProcess = Start-Process -FilePath $godot -ArgumentList @('--headless','--path',$godotProjectPath,'--export-release','"Windows Desktop"',$exportExePath,'--quit') -PassThru -NoNewWindow -RedirectStandardOutput $exportLog -RedirectStandardError $exportStderr
    $exportExited = $exportProcess.WaitForExit(60000)
    if (-not $exportExited) {
        Stop-Process -Id $exportProcess.Id -Force -ErrorAction SilentlyContinue
        $exportProcess.WaitForExit()
        $exportOutput = Get-Content -LiteralPath $exportLog -Raw -ErrorAction SilentlyContinue
        $exportOutput = [regex]::Replace($exportOutput, '\x1B\[[0-?]*[ -/]*[@-~]', '')
        if ($exportOutput -match '\[ DONE \] savepack' -and (Test-Path (Join-Path $stage 'RoboWorld.exe'))) {
            Write-Warning 'Godot completed savepack but did not exit; stopped the exporter after its verified completion marker.'
        } else {
            throw "Godot Windows export did not complete within 60 seconds. See $exportLog and $exportStderr"
        }
    }
    $exportOutput = Get-Content -LiteralPath $exportLog -Raw -ErrorAction SilentlyContinue
    $exportErrorOutput = Get-Content -LiteralPath $exportStderr -Raw -ErrorAction SilentlyContinue
    $exportOutput = [regex]::Replace($exportOutput, '\x1B\[[0-?]*[ -/]*[@-~]', '')
    if (($exportProcess.ExitCode -ne 0 -and $exportExited) -or
        $exportOutput -match '(?m)^\s*ERROR:' -or
        $exportErrorOutput -match '(?m)^\s*ERROR:|Failed to build project|Export \.NET Project:' -or
        -not (Test-Path (Join-Path $stage 'RoboWorld.exe'))) {
        throw "Godot Windows export failed. See $exportLog and $exportStderr"
    }

    Copy-Item -LiteralPath (Join-Path $root 'examples') -Destination (Join-Path $stage 'examples') -Recurse
    # Fast-profile preflight re-fingerprints the measured board/peripheral map
    # against this source file. Keep that authoritative source available in the
    # exported package even though the service itself is self-contained.
    $packagedServiceSources = Join-Path $stage 'src/RoboWorld.RobotService'
    New-Item -ItemType Directory -Force -Path $packagedServiceSources | Out-Null
    Copy-Item -LiteralPath (Join-Path $root 'src/RoboWorld.RobotService/RenodeMotorEventHost.cs') -Destination $packagedServiceSources
    foreach ($target in @('stm32-drive', 'stm32-arm')) {
        $sourceFirmwareRoot = Join-Path $root "samples/firmware/$target"
        $stagedFirmwareRoot = Join-Path $stage "samples/firmware/$target"
        New-Item -ItemType Directory -Force -Path (Join-Path $stagedFirmwareRoot 'build') | Out-Null
        foreach ($file in @('main.c','startup.s','stm32f407vg.ld','README.md','build.ps1')) { Copy-Item -LiteralPath (Join-Path $sourceFirmwareRoot $file) -Destination $stagedFirmwareRoot }
        foreach ($file in @('firmware.elf','build_id.h','build-metadata.txt')) { Copy-Item -LiteralPath (Join-Path $sourceFirmwareRoot "build/$file") -Destination (Join-Path $stagedFirmwareRoot 'build') }
    }
    Copy-Item -LiteralPath (Join-Path $root 'samples/controller') -Destination (Join-Path $stage 'samples/controller') -Recurse
    Copy-Item -LiteralPath (Join-Path $root 'toolchain.lock.json') -Destination (Join-Path $stage 'toolchain.lock.json')
    $renodeDestination = Join-Path $stage '.toolchain/renode/renode_1.17.0-portable'
    New-Item -ItemType Directory -Force -Path (Split-Path $renodeDestination) | Out-Null
    Copy-Item -LiteralPath $renode -Destination $renodeDestination -Recurse

    $licenses = Join-Path $stage 'licenses'
    New-Item -ItemType Directory -Force -Path $licenses | Out-Null
    Invoke-WebRequest -Uri 'https://raw.githubusercontent.com/godotengine/godot/4.7.2-stable/LICENSE.txt' -OutFile (Join-Path $licenses 'Godot-LICENSE.txt')
    Invoke-WebRequest -Uri 'https://raw.githubusercontent.com/godotengine/godot/4.7.2-stable/COPYRIGHT.txt' -OutFile (Join-Path $licenses 'Godot-COPYRIGHT.txt')
    Copy-Item -LiteralPath (Join-Path $env:ProgramFiles 'dotnet/LICENSE.txt') -Destination (Join-Path $licenses 'dotnet-LICENSE.txt')
    Copy-Item -LiteralPath (Join-Path $env:ProgramFiles 'dotnet/ThirdPartyNotices.txt') -Destination (Join-Path $licenses 'dotnet-ThirdPartyNotices.txt')
    Copy-Item -LiteralPath (Join-Path $renode 'licenses') -Destination (Join-Path $licenses 'renode') -Recurse
    $protobufLicense = @'
Google.Protobuf 3.36.2 — BSD 3-Clause License

Copyright 2015, Google Inc.

Redistribution and use in source and binary forms, with or without modification,
are permitted provided that the following conditions are met:
1. Redistributions of source code must retain the above copyright notice, this
   list of conditions and the following disclaimer.
2. Redistributions in binary form must reproduce the above copyright notice,
   this list of conditions and the following disclaimer in the documentation
   and/or other materials provided with the distribution.
3. Neither the name of Google Inc. nor the names of its contributors may be used
   to endorse or promote products derived from this software without specific
   prior written permission.

THIS SOFTWARE IS PROVIDED BY THE COPYRIGHT HOLDERS AND CONTRIBUTORS "AS IS"
AND ANY EXPRESS OR IMPLIED WARRANTIES, INCLUDING, BUT NOT LIMITED TO, THE IMPLIED
WARRANTIES OF MERCHANTABILITY AND FITNESS FOR A PARTICULAR PURPOSE ARE DISCLAIMED.
IN NO EVENT SHALL THE COPYRIGHT HOLDER OR CONTRIBUTORS BE LIABLE FOR ANY DIRECT,
INDIRECT, INCIDENTAL, SPECIAL, EXEMPLARY, OR CONSEQUENTIAL DAMAGES (INCLUDING,
BUT NOT LIMITED TO, PROCUREMENT OF SUBSTITUTE GOODS OR SERVICES; LOSS OF USE,
DATA, OR PROFITS; OR BUSINESS INTERRUPTION) HOWEVER CAUSED AND ON ANY THEORY OF
LIABILITY, WHETHER IN CONTRACT, STRICT LIABILITY, OR TORT (INCLUDING NEGLIGENCE
OR OTHERWISE) ARISING IN ANY WAY OUT OF THE USE OF THIS SOFTWARE, EVEN IF
ADVISED OF THE POSSIBILITY OF SUCH DAMAGE.
'@
    Set-Content -Encoding utf8 (Join-Path $licenses 'Google-Protobuf-BSD-3-Clause.txt') $protobufLicense

    $notices = @'
RoboWorld Windows package — third-party notices
Godot Engine 4.7.2-stable: MIT. See Godot-LICENSE.txt and the full upstream
copyright/license register in Godot-COPYRIGHT.txt.
Renode 1.17.0 portable: MIT and bundled dependencies. Preserve the complete
upstream notices under licenses/renode/.
.NET runtime bits used by the exported app and self-contained Robot Service:
Microsoft license and dependency notices are in dotnet-LICENSE.txt and
dotnet-ThirdPartyNotices.txt. The SDK is not distributed.
Google.Protobuf 3.36.2: BSD 3-Clause; see Google-Protobuf-BSD-3-Clause.txt.
RoboWorld source and sample firmware are authored for this project.
'@
    Set-Content -Encoding utf8 (Join-Path $licenses 'THIRD_PARTY_NOTICES.txt') $notices

    $quickstart = @'
RoboWorld — Windows quickstart

1. Extract this folder somewhere writable and double-click RoboWorld.exe.
2. The starter room and Living Rover open automatically. Robot Service and the
   bundled Renode runtime start inside the app; no service terminal is needed.
3. The Python controller is a separate program. Install Python 3.11 or later,
   open a terminal in this folder, and run:
     python samples/controller/main.py --host 127.0.0.1 --port <UART port> --motor 50 50
   Replace <UART port> with the ephemeral endpoint shown in the app. The command
   follows the virtual UART into the bundled STM32 firmware.
4. Use Save Project to write a .roboworld design package and Open Project to
   start a new app session from one.

The reference firmware ELF and source are in samples/firmware/stm32-drive/.
Only supported firmware, device and timing fidelity is claimed; see diagnostics
and the evidence reports. Godot runtime, Renode and license notices are in
licenses/. User-writeable runtime files are kept under %LOCALAPPDATA%\RoboWorld.
'@
    Set-Content -Encoding utf8 (Join-Path $stage 'QUICKSTART.txt') $quickstart

    if (Test-Path $fullPackageRoot) { Remove-Item -LiteralPath $fullPackageRoot -Recurse -Force }
    Move-Item -LiteralPath $stage -Destination $packageRoot
    $manifestPath = Join-Path $packageRoot 'SHA256SUMS.csv'
    Get-ChildItem -LiteralPath $packageRoot -File -Recurse | Where-Object { $_.FullName -ne $manifestPath } | Sort-Object FullName | ForEach-Object {
        $relative = $_.FullName.Substring($packageRoot.Length).TrimStart([IO.Path]::DirectorySeparatorChar, [IO.Path]::AltDirectorySeparatorChar).Replace('\','/')
        [pscustomobject]@{ path = $relative; sha256 = (Get-FileHash -Algorithm SHA256 -LiteralPath $_.FullName).Hash.ToLowerInvariant(); bytes = $_.Length }
    } | Export-Csv -NoTypeInformation -Encoding utf8 $manifestPath
    Write-Output "WINDOWS_PACKAGE_READY path=$packageRoot files=$((Import-Csv $manifestPath).Count)"
} finally {
    if (Test-Path $fullStage) { Remove-Item -LiteralPath $fullStage -Recurse -Force }
}
