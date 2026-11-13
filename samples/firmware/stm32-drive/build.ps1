$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent (Split-Path -Parent (Split-Path -Parent $PSScriptRoot))
$tool = Get-ChildItem (Join-Path $root '.toolchain/arm') -Filter arm-none-eabi-gcc.exe -Recurse | Select-Object -First 1 -ExpandProperty FullName
if (-not $tool) { throw 'Pinned Arm GNU compiler not found; complete T00 provisioning first.' }
$src = $PSScriptRoot
$out = Join-Path $src 'build'
New-Item -ItemType Directory -Force $out | Out-Null
$sourceFiles = @((Join-Path $src 'main.c'), (Join-Path $src 'startup.s'), (Join-Path $src 'stm32f407vg.ld'))
$hashText = ($sourceFiles | ForEach-Object { (Get-FileHash -Algorithm SHA256 -LiteralPath $_).Hash }) -join "`n"
$hasher = [System.Security.Cryptography.SHA256]::Create()
try { $sha = $hasher.ComputeHash([System.Text.Encoding]::UTF8.GetBytes($hashText)) } finally { $hasher.Dispose() }
$sourceHash = ([BitConverter]::ToString($sha)).Replace('-', '')
$buildId = $sourceHash.Substring(0, 8).ToLowerInvariant()
Set-Content -Encoding ascii -LiteralPath (Join-Path $out 'build_id.h') -Value "#define FW_BUILD_ID `"$buildId`""
$elf = Join-Path $out 'firmware.elf'
$commonArgs = @(
    '-mcpu=cortex-m4', '-mthumb', '-Og', '-g3', '-ffreestanding', '-fno-builtin', '-fno-exceptions',
    '-ffunction-sections', '-fdata-sections', '-Wall', '-Wextra', '-Werror'
)
Write-Output "Compiler: $tool"
& $tool --version | Select-Object -First 1
$startupObject = Join-Path $out 'startup.o'
$mainObject = Join-Path $out 'main.o'
$compileStartup = @($commonArgs) + @('-c', (Join-Path $src 'startup.s'), '-o', $startupObject)
$compileMain = @($commonArgs) + @('-include', (Join-Path $out 'build_id.h'), '-c', (Join-Path $src 'main.c'), '-o', $mainObject)
$linkArgs = @('-mcpu=cortex-m4', '-mthumb', '-nostdlib', '-Wl,--gc-sections', "-Wl,-T,$(Join-Path $src 'stm32f407vg.ld')", "-Wl,-Map,$(Join-Path $out 'firmware.map')", $startupObject, $mainObject, '-o', $elf)
foreach ($commandArgs in @($compileStartup, $compileMain, $linkArgs)) {
    $formattedArgs = $commandArgs | ForEach-Object { if ($_ -match '\s') { '"' + $_ + '"' } else { $_ } }
    Write-Output "Command: `"$tool`" $($formattedArgs -join ' ')"
    & $tool @commandArgs
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
}
$elfHash = (Get-FileHash -Algorithm SHA256 -LiteralPath $elf).Hash
"Build ID: $buildId" | Tee-Object -FilePath (Join-Path $out 'build-metadata.txt')
"ELF SHA-256: $elfHash" | Tee-Object -FilePath (Join-Path $out 'build-metadata.txt') -Append
"Source input SHA-256: $sourceHash" | Tee-Object -FilePath (Join-Path $out 'build-metadata.txt') -Append
