# Shared package identity checks; no build, download or launch side effects.
function Get-ReleaseSourceCommit {
    param([string]$ProjectRoot)
    $commit = & git -C $ProjectRoot rev-parse --verify HEAD
    if ($LASTEXITCODE -ne 0) { throw 'Cannot identify the package source commit.' }
    # SDK default globs can include new source files, so untracked, non-ignored inputs matter too.
    $changes = @(& git -C $ProjectRoot status --porcelain=v1 --untracked-files=all --ignore-submodules=none)
    if ($LASTEXITCODE -ne 0) { throw 'Cannot verify package source cleanliness.' }
    if ($changes.Count) {
        throw 'Release packaging requires a clean tracked worktree and no untracked, non-ignored files. Commit or remove source changes first; the previous release is preserved.'
    }
    return ($commit | Select-Object -First 1).Trim()
}

function Assert-NoPackageReparsePoints {
    param([string]$Root)
    $items = @((Get-Item -LiteralPath $Root)) + @(Get-ChildItem -LiteralPath $Root -Recurse -Force)
    if (@($items | Where-Object { $_.Attributes -band [IO.FileAttributes]::ReparsePoint }).Count) {
        throw 'Package reparse points are not supported.'
    }
}

function Assert-PinnedFiles {
    param([string]$Root, [object]$Hashes)
    Assert-NoPackageReparsePoints -Root $Root
    foreach ($property in $Hashes.PSObject.Properties) {
        $path = Join-Path $Root $property.Name
        if (!(Test-Path -LiteralPath $path -PathType Leaf) -or
            (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash -ne $property.Value) {
            throw "Pinned runtime identity mismatch: $($property.Name)"
        }
    }
}

function Get-SceneRuntimeDigest {
    param([string]$Root)
    Assert-NoPackageReparsePoints -Root $Root
    # Bytecode is interpreter-generated and may contain build-machine source paths. The shipped
    # source, metadata, configuration and native binaries define this immutable input snapshot.
    $lines = @(Get-ChildItem -LiteralPath $Root -Recurse -File -Force | Where-Object {
        $_.Extension -ne '.pyc' -and $_.FullName -notmatch '[\\/]__pycache__[\\/]'
    } | ForEach-Object {
        $relative = [IO.Path]::GetRelativePath($Root, $_.FullName).Replace('\', '/')
        $relative + ':' + (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
    })
    [Array]::Sort($lines, [StringComparer]::Ordinal)
    $bytes = [Text.Encoding]::UTF8.GetBytes(($lines -join "`n") + "`n")
    return [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($bytes)).ToLowerInvariant()
}

function Write-ReleaseInventory {
    param([string]$Root, [string]$Commit, [string]$SdkVersion,
        [ValidateSet('relocatable', 'legacy', 'missing')][string]$SceneRuntimeKind = 'missing')
    Assert-NoPackageReparsePoints -Root $Root
    $files = @(Get-ChildItem -LiteralPath $Root -Recurse -File -Force | Sort-Object FullName | ForEach-Object {
        $relative = [IO.Path]::GetRelativePath($Root, $_.FullName).Replace('\', '/')
        if ($relative -eq 'package-inventory.json') { return }
        [ordered]@{ path=$relative; sizeBytes=$_.Length; sha256=(Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant() }
    })
    [ordered]@{ schemaVersion=1; sourceCommit=$Commit; sdkVersion=$SdkVersion; sceneRuntimeKind=$SceneRuntimeKind;
        runtimeIdentifier='win-x64'; frameworkDependent=$true; extraction='none; expanded directory bundle';
        redistributionEligible=$false; files=$files } | ConvertTo-Json -Depth 5 |
        Set-Content -LiteralPath (Join-Path $Root 'package-inventory.json') -Encoding utf8
}

function Test-ReleaseInventory {
    param([string]$Root, [switch]$AllowLegacySceneRuntime)
    $rootPath = [IO.Path]::GetFullPath($Root).TrimEnd('\','/')
    $manifest = Get-Content -LiteralPath (Join-Path $rootPath 'package-inventory.json') -Raw | ConvertFrom-Json
    if ($manifest.schemaVersion -ne 1 -or !$manifest.frameworkDependent -or $manifest.runtimeIdentifier -ne 'win-x64') {
        throw 'Unsupported package identity.'
    }
    $seen = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
    Assert-NoPackageReparsePoints -Root $rootPath
    foreach ($file in $manifest.files) {
        $path = [IO.Path]::GetFullPath((Join-Path $rootPath $file.path))
        $canonical = [IO.Path]::GetRelativePath($rootPath, $path).Replace('\', '/')
        if (!$path.StartsWith($rootPath + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase) -or
            $canonical -cne $file.path -or !$seen.Add($canonical)) {
            throw 'Unsafe or duplicate package inventory path.'
        }
        if (!(Test-Path -LiteralPath $path -PathType Leaf) -or (Get-Item -LiteralPath $path -Force).Length -ne $file.sizeBytes -or
            (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash -ne $file.sha256) {
            throw "Package inventory mismatch: $($file.path)"
        }
    }
    foreach ($file in Get-ChildItem -LiteralPath $rootPath -Recurse -File -Force) {
        $relative = [IO.Path]::GetRelativePath($rootPath, $file.FullName).Replace('\', '/')
        if ($relative -ne 'package-inventory.json' -and !$seen.Contains($relative)) { throw "Unlisted package file: $relative" }
    }
    foreach ($runtime in @('coreclr.dll','hostfxr.dll','System.Private.CoreLib.dll')) {
        if (@(Get-ChildItem -LiteralPath $rootPath -Recurse -File -Force -Filter $runtime).Count) { throw "Unexpected bundled runtime/configuration: $runtime" }
    }
    foreach ($config in Get-ChildItem -LiteralPath $rootPath -Recurse -File -Force -Filter 'pyvenv.cfg') {
        $relative = [IO.Path]::GetRelativePath($rootPath, $config.FullName).Replace('\', '/')
        # The legacy development fallback is host-dependent; production callers remain strict.
        $kind = $manifest.PSObject.Properties['sceneRuntimeKind']
        if (!$AllowLegacySceneRuntime -or $null -eq $kind -or $kind.Value -ne 'legacy' -or
            $relative -cne 'scene/venv/pyvenv.cfg') {
            throw 'Unexpected bundled runtime/configuration: pyvenv.cfg'
        }
    }
    return $manifest
}
