#Requires -Version 7.0
$ErrorActionPreference = 'Stop'
. "$PSScriptRoot/Release-Package.ps1"
$root = Join-Path (Split-Path $PSScriptRoot -Parent) ('artifacts/package-tests/' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $root -Force | Out-Null
Set-Content -LiteralPath (Join-Path $root 'fixture.txt') -Value 'known package bytes'
Write-ReleaseInventory -Root $root -Commit 'fixture' -SdkVersion 'fixture'
$null = Test-ReleaseInventory -Root $root
Set-Content -LiteralPath (Join-Path $root 'fixture.txt') -Value 'tampered package bytes'
$rejected = $false
try { $null = Test-ReleaseInventory -Root $root } catch { $rejected = $true }
if (!$rejected) { throw 'Tampered package was accepted.' }
Write-ReleaseInventory -Root $root -Commit 'fixture' -SdkVersion 'fixture'
Set-Content -LiteralPath (Join-Path $root 'unexpected.txt') -Value 'unexpected'
$rejected = $false
try { $null = Test-ReleaseInventory -Root $root } catch { $rejected = $true }
if (!$rejected) { throw 'Unlisted file was accepted.' }
Write-ReleaseInventory -Root $root -Commit 'fixture' -SdkVersion 'fixture'
$hidden = Join-Path $root 'hidden.txt'
Set-Content -LiteralPath $hidden -Value 'hidden injection'
[IO.File]::SetAttributes($hidden, [IO.FileAttributes]::Hidden)
$rejected = $false
try { $null = Test-ReleaseInventory -Root $root } catch { $rejected = $true }
if (!$rejected) { throw 'Unlisted hidden file was accepted.' }
Write-ReleaseInventory -Root $root -Commit 'fixture' -SdkVersion 'fixture'
$null = Test-ReleaseInventory -Root $root
$forbidden = Join-Path $root 'coreclr.dll'
Set-Content -LiteralPath $forbidden -Value 'forbidden framework runtime'
[IO.File]::SetAttributes($forbidden, [IO.FileAttributes]::Hidden)
Write-ReleaseInventory -Root $root -Commit 'fixture' -SdkVersion 'fixture'
$rejected = $false
try { $null = Test-ReleaseInventory -Root $root } catch { $rejected = $true }
if (!$rejected) { throw 'Listed hidden framework runtime was accepted.' }
Write-Host 'PASS release inventory: intact/hidden files, tampered bytes, unlisted files and forbidden hidden runtime.'

$legacyRoot = Join-Path $root 'legacy-package'
$venv = Join-Path $legacyRoot 'scene/venv'
New-Item -ItemType Directory -Path $venv -Force | Out-Null
Set-Content -LiteralPath (Join-Path $venv 'pyvenv.cfg') -Value 'home = host-python'
foreach ($kind in @('relocatable', 'missing', 'legacy')) {
    Write-ReleaseInventory -Root $legacyRoot -Commit 'fixture' -SdkVersion 'fixture' -SceneRuntimeKind $kind
    $rejected = $false
    try { $null = Test-ReleaseInventory -Root $legacyRoot } catch { $rejected = $true }
    if (!$rejected) { throw "Strict package verification accepted $kind pyvenv.cfg." }
    if ($kind -eq 'legacy') {
        $null = Test-ReleaseInventory -Root $legacyRoot -AllowLegacySceneRuntime
    }
    else {
        $rejected = $false
        try { $null = Test-ReleaseInventory -Root $legacyRoot -AllowLegacySceneRuntime } catch { $rejected = $true }
        if (!$rejected) { throw "Legacy opt-in accepted $kind pyvenv.cfg." }
    }
}
Set-Content -LiteralPath (Join-Path $legacyRoot 'pyvenv.cfg') -Value 'unexpected configuration'
Write-ReleaseInventory -Root $legacyRoot -Commit 'fixture' -SdkVersion 'fixture' -SceneRuntimeKind 'legacy'
$rejected = $false
try { $null = Test-ReleaseInventory -Root $legacyRoot -AllowLegacySceneRuntime } catch { $rejected = $true }
if (!$rejected) { throw 'Legacy opt-in accepted pyvenv.cfg outside scene/venv.' }
Write-Host 'PASS scene inventory: explicit legacy fallback accepted; strict, wrong-kind and wrong-path controls rejected.'

# Exercise the actual early build preflight in a tiny owned repository, without publish or runtimes.
$sourceRoot = Join-Path $root 'source-repo'
$fixtureTools = Join-Path $sourceRoot 'tools'
New-Item -ItemType Directory -Path $fixtureTools -Force | Out-Null
Copy-Item -LiteralPath "$PSScriptRoot/Build-Release.ps1", "$PSScriptRoot/Release-Package.ps1", "$PSScriptRoot/Promotion.ps1" -Destination $fixtureTools
Set-Content -LiteralPath (Join-Path $sourceRoot '.gitignore') -Value '/artifacts/'
$sourceFile = Join-Path $sourceRoot 'Source.cs'
Set-Content -LiteralPath $sourceFile -Value '// committed source'
function Invoke-FixtureGit {
    param([string[]]$Arguments)
    $output = & git -C $sourceRoot @Arguments 2>&1
    if ($LASTEXITCODE -ne 0) { throw "Fixture git failed: $output" }
}
Invoke-FixtureGit @('init', '--quiet')
Invoke-FixtureGit @('add', '.')
Invoke-FixtureGit @('-c', 'user.name=Fixture', '-c', 'user.email=fixture@example.invalid', '-c', 'commit.gpgsign=false', 'commit', '--quiet', '-m', 'Fixture inputs')
$fixtureCommit = Get-ReleaseSourceCommit -ProjectRoot $sourceRoot
$expectedCommit = (& git -C $sourceRoot rev-parse HEAD).Trim()
if ($LASTEXITCODE -ne 0 -or $fixtureCommit -ne $expectedCommit) { throw 'Clean source provenance did not match HEAD.' }
$sentinels = @('artifacts/release/app/previous.txt', 'artifacts/staging/build/previous.txt', 'artifacts/staging/release/previous.txt')
foreach ($relative in $sentinels) {
    $path = Join-Path $sourceRoot $relative
    New-Item -ItemType Directory -Path (Split-Path $path -Parent) -Force | Out-Null
    Set-Content -LiteralPath $path -Value 'preserved bytes'
}
foreach ($change in @('unstaged', 'staged', 'untracked')) {
    if ($change -eq 'untracked') {
        Set-Content -LiteralPath (Join-Path $sourceRoot 'NewSource.cs') -Value '// new default-glob input'
    }
    else {
        Set-Content -LiteralPath $sourceFile -Value '// changed source'
        if ($change -eq 'staged') { Invoke-FixtureGit @('add', 'Source.cs') }
    }
    $failureOutput = & (Join-Path $PSHOME 'pwsh.exe') -NoProfile -File (Join-Path $fixtureTools 'Build-Release.ps1') 2>&1 | Out-String
    if ($LASTEXITCODE -eq 0 -or $failureOutput -notmatch 'Release packaging requires a clean tracked worktree') {
        throw "Build did not reject $change source in early preflight: $failureOutput"
    }
    foreach ($relative in $sentinels) {
        if ((Get-Content -LiteralPath (Join-Path $sourceRoot $relative) -Raw).Trim() -ne 'preserved bytes') {
            throw "Dirty $change build changed existing output."
        }
    }
    if ($change -eq 'untracked') { Remove-Item -LiteralPath (Join-Path $sourceRoot 'NewSource.cs') }
    else { Invoke-FixtureGit @('restore', '--source=HEAD', '--staged', '--worktree', 'Source.cs') }
}
if ((Get-ReleaseSourceCommit -ProjectRoot $sourceRoot) -ne $fixtureCommit) { throw 'Source fixture did not return to its clean identity.' }
Write-Host 'PASS source provenance: clean HEAD identity, early staged/unstaged/untracked rejection and existing outputs preserved.'
