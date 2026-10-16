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
