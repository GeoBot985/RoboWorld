[CmdletBinding()]
param(
    [Parameter(Mandatory)] [string]$PackageRoot,
    [Parameter(Mandatory)] [string]$CandidateDirectory
)
$ErrorActionPreference = 'Stop'
$package = [IO.Path]::GetFullPath($PackageRoot)
$candidate = [IO.Path]::GetFullPath($CandidateDirectory)
$manifestPath = Join-Path $package 'examples/project.json'
$profilePath = Join-Path $candidate 'candidate-profile-VALIDATED.json'
$modelPath = Join-Path $candidate 'candidate-model-VALIDATED.json'
$reportPath = Join-Path $candidate 'candidate-qualification-report.json'
foreach ($path in @($manifestPath,$profilePath,$modelPath,$reportPath)) { if (-not (Test-Path -LiteralPath $path)) { throw "Required validated Fast profile input missing: $path" } }
$profile = Get-Content -Raw -LiteralPath $profilePath | ConvertFrom-Json
$model = Get-Content -Raw -LiteralPath $modelPath | ConvertFrom-Json
$report = Get-Content -Raw -LiteralPath $reportPath | ConvertFrom-Json
$qualification = if ($null -ne $report.qualification) { $report.qualification } else { $report }
if ($profile.fidelityLabel -ne 'VALIDATED' -or $qualification.fidelityLabel -ne 'VALIDATED' -or $qualification.status -ne 'VALIDATED_FOR_MEASURED_NORMAL_OPERATION_ENVELOPE') { throw 'Candidate has no builder-validated normal-operation profile.' }
if ($qualification.candidateModelSha256 -ne $profile.modelDocumentSha256) { throw 'Qualification evidence model hash differs from the profile.' }
$profileId = [string]$profile.profileId
$destination = Join-Path $package "examples/controller-profiles/$profileId"
New-Item -ItemType Directory -Force -Path $destination | Out-Null
Copy-Item -LiteralPath $profilePath -Destination (Join-Path $destination 'profile.json') -Force
Copy-Item -LiteralPath $modelPath -Destination (Join-Path $destination 'model.json') -Force
$profileSha = (Get-FileHash -LiteralPath (Join-Path $destination 'profile.json') -Algorithm SHA256).Hash.ToLowerInvariant()
$parsed = Get-Content -Raw -LiteralPath $manifestPath | ConvertFrom-Json
if (-not ($parsed.robot_files -contains 'robots/rover-01.robot.json')) { throw 'Package starter project does not include rover-01.' }
if ($null -eq $parsed.settings) { $parsed | Add-Member -NotePropertyName settings -NotePropertyValue ([pscustomobject]@{}) }
$parsed.settings | Add-Member -Force -NotePropertyName controller_execution -NotePropertyValue ([pscustomobject]@{
    mode = 'fast'
    profiles = [pscustomobject]@{ 'rover-01/mcu-drive' = [pscustomobject]@{ profile_id = $profileId; profile_sha256 = $profileSha } }
})
$manifestJson = $parsed | ConvertTo-Json -Depth 100
[IO.File]::WriteAllText($manifestPath, $manifestJson + "`n", [Text.UTF8Encoding]::new($false))
$checksumsPath = Join-Path $package 'SHA256SUMS.csv'
if (Test-Path -LiteralPath $checksumsPath) {
    Get-ChildItem -LiteralPath $package -File -Recurse | Where-Object { $_.FullName -ne $checksumsPath } | Sort-Object FullName | ForEach-Object {
        [pscustomobject]@{ path=[IO.Path]::GetRelativePath($package, $_.FullName).Replace('\','/'); sha256=(Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant(); bytes=$_.Length }
    } | Export-Csv -NoTypeInformation -Encoding utf8 $checksumsPath
}
$exe = Join-Path $package 'RoboWorld.exe'
$firmware = Join-Path $package 'samples/firmware/stm32-drive/build/firmware.elf'
$evidenceDirectory = Join-Path (Split-Path -Parent $PSScriptRoot) 'artifacts/evidence/C04'
New-Item -ItemType Directory -Force -Path $evidenceDirectory | Out-Null
$evidence = [ordered]@{
    status = 'FAST_PROFILE_INSTALLED_IN_EXPORTED_RELEASE_PACKAGE'
    created_utc = [DateTime]::UtcNow.ToString('o')
    package_root = $package
    profile_id = $profileId
    saved_project_mode = 'fast'
    project_controller_reference = 'rover-01/mcu-drive'
    profile_sha256 = $profileSha
    model_content_sha256 = $profile.modelDocumentSha256
    package_executable_sha256 = (Get-FileHash -LiteralPath $exe -Algorithm SHA256).Hash
    project_manifest_sha256 = (Get-FileHash -LiteralPath $manifestPath -Algorithm SHA256).Hash
    firmware_sha256 = (Get-FileHash -LiteralPath $firmware -Algorithm SHA256).Hash
    qualification_report = [IO.Path]::GetRelativePath((Split-Path -Parent $PSScriptRoot), $reportPath)
}
$evidencePath = Join-Path $evidenceDirectory 'fast-release-profile-install.json'
$evidence | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath $evidencePath -Encoding utf8
Write-Output "FAST_PROFILE_PACKAGE_READY profile=$profileId package=$package evidence=$evidencePath"
