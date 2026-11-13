$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$evidence = Join-Path $root 'artifacts/evidence/T07'
$dotnet = Join-Path $root '.toolchain/dotnet/dotnet.exe'
$godot = Get-ChildItem (Join-Path $root '.toolchain/godot') -Filter 'Godot_v4.7.2-stable_mono_win64_console.exe' -Recurse | Select-Object -First 1 -ExpandProperty FullName
$buildLog = Join-Path $evidence 'shop-snap-build.log'
$stdout = Join-Path $evidence 'shop-snap-smoke.log'
$stderr = Join-Path $evidence 'shop-snap-smoke.stderr.log'
$env:DOTNET_ROOT = Join-Path $root '.toolchain/dotnet'
$env:DOTNET_ROOT_X64 = $env:DOTNET_ROOT
& $dotnet build (Join-Path $root 'godot/RoboWorld.Godot.csproj') --configuration Debug --nologo *> $buildLog
if ($LASTEXITCODE -ne 0) { Get-Content $buildLog; throw 'Godot shop pose editor build failed.' }
$arguments = @('--headless', '--path', (Join-Path $root 'godot'), '--', '--shop-snap-smoke')
$process = Start-Process -FilePath $godot -ArgumentList $arguments -PassThru -NoNewWindow -RedirectStandardOutput $stdout -RedirectStandardError $stderr
if (-not $process.WaitForExit(45000)) {
    Stop-Process -Id $process.Id -Force -ErrorAction SilentlyContinue
    throw 'Godot shop snap smoke exceeded 45 seconds.'
}
if ($process.ExitCode -ne 0) { throw "Godot shop snap smoke exited with code $($process.ExitCode). See $stderr" }
$log = Get-Content -LiteralPath $stdout -Raw
if ($log -notmatch 'SHOP_GENERIC_CYLINDER_GEOMETRY visual_collision_match=PASS') { throw 'Generic cylinder rendering does not match collision geometry.' }
if ($log -notmatch 'SHOP_COMPONENT_ADDED id=shop-rw-beam-structural .*parent=chassis\.top child_port=bottom') { throw 'UI shop action did not snap a beam to the chassis top.' }
if ($log -notmatch 'SHOP_COMPONENT_RESIZED id=shop-rw-beam-structural size_m=0\.060,0\.600,0\.060') { throw 'UI shop action did not save edited SI dimensions.' }
if ($log -notmatch 'SHOP_SNAP_PACKAGE_ROUNDTRIP .*parent_port=top child_port=bottom size_m=0\.060,0\.600,0\.060 connections=1') { throw 'Shop assembly graph did not survive project package save/reopen.' }
if ($log -notmatch 'SHOP_JOINT_PACKAGE_ROUNDTRIP .*child_port=bottom child_frame_y=-0\.300') { throw 'Shop child-link joint and its resized connector did not survive project package save/reopen.' }
if ($log -notmatch 'SHOP_FIXED_PACKAGE_ROUNDTRIP .*child_port=bottom') { throw 'Shop fixed child-link joint did not survive project package save/reopen.' }
if ($log -notmatch 'SHOP_COMPONENT_REMOVED id=shop-rw-beam-structural components=3 links=2 joints=2') { throw 'Shop removal did not remove its dependent mechanical subtree.' }
if ($log -notmatch 'SHOP_REMOVE_PACKAGE_ROUNDTRIP components=10 links=3 joints=2 mounts=0') { throw 'Removed shop subassembly returned after project package reopen.' }
if ($log -notmatch 'SHOP_FREE_COMPONENT_POSE_PACKAGE_ROUNDTRIP .*position_m=0\.420,0\.360,-0\.270 rotation_xyzw=0\.0000,0\.3827,0\.0000,0\.9239') { throw 'Free catalogue part position and rotation did not survive project package save/reopen.' }
if ($log -notmatch 'SHOP_ELECTRICAL_CONNECTION_ADDED .*from=mcu-board\.uart to=shop-rw-mcu-stm32f407vg\.uart interface=uart pin=none bus=shop_uart') { throw 'Electrical graph editor did not add the selected typed UART wire.' }
if ($log -notmatch 'SHOP_ELECTRICAL_CONNECTION_REMOVED id=wire-mcu-board-uart-shop-rw-mcu-stm32f407vg-uart') { throw 'Electrical graph editor did not remove the selected UART wire.' }
if ($log -notmatch 'SHOP_ELECTRICAL_PACKAGE_ROUNDTRIP components=12 .*from=mcu-board\.uart to=shop-rw-mcu-stm32f407vg\.uart interface=uart bus=shop_uart') { throw 'Added electrical connection did not survive project package save/reopen.' }
Write-Host 'Catalogue add, free pose editing, rigid/fixed/revolute snap, SI sizing, subassembly removal, typed electrical editing and package round-trip: PASS'
Get-Content -LiteralPath $stdout | Select-String 'SHOP_COMPONENT_ADDED|SHOP_COMPONENT_RESIZED|SHOP_JOINTED_COMPONENT_ADDED|SHOP_JOINT_PACKAGE_ROUNDTRIP|SHOP_FIXED_COMPONENT|SHOP_SNAP_PACKAGE_ROUNDTRIP|SHOP_COMPONENT_REMOVED|SHOP_REMOVE_PACKAGE_ROUNDTRIP|SHOP_FREE_COMPONENT|SHOP_ELECTRICAL_CONNECTION_ADDED|SHOP_ELECTRICAL_CONNECTION_REMOVED|SHOP_ELECTRICAL_PACKAGE_ROUNDTRIP'
