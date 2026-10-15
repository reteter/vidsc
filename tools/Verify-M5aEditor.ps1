param(
    [Parameter(Mandatory)][string]$SourcePath,
    [string]$AppDirectory = 'artifacts/release/app',
    [Parameter(Mandatory)][string]$OutputDirectory,
    [switch]$RunVisible
)
$ErrorActionPreference = 'Stop'
$repo = Split-Path $PSScriptRoot -Parent
$app = [IO.Path]::GetFullPath((Join-Path $repo $AppDirectory))
$work = [IO.Path]::GetFullPath($OutputDirectory)
$source = [IO.Path]::GetFullPath($SourcePath)
if (!$work.StartsWith((Join-Path $repo 'artifacts') + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) {
    throw 'Verification output must stay in repository-owned artifacts.'
}
if (Test-Path -LiteralPath $work) { throw 'Use a fresh output directory for each diagnostic attempt.' }
if (!(Test-Path -LiteralPath $source -PathType Leaf)) { throw 'Explicit synthetic source is missing.' }
New-Item -ItemType Directory -Path $work | Out-Null
$references = Get-ChildItem -LiteralPath $app -Filter '*.dll' -File | ForEach-Object {
    $name = [Security.SecurityElement]::Escape($_.BaseName)
    $path = [Security.SecurityElement]::Escape($_.FullName)
    "<Reference Include=`"$name`"><HintPath>$path</HintPath></Reference>"
}
$runner = [Security.SecurityElement]::Escape((Join-Path $PSScriptRoot 'verification/M5aEditor.cs'))
@"
<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><OutputType>Exe</OutputType><TargetFramework>net10.0-windows</TargetFramework><UseWPF>true</UseWPF><ImplicitUsings>enable</ImplicitUsings><Nullable>enable</Nullable><PlatformTarget>x64</PlatformTarget><UseAppHost>false</UseAppHost><UseSharedCompilation>false</UseSharedCompilation></PropertyGroup><ItemGroup>$references<Compile Include="$runner"/></ItemGroup></Project>
"@ | Set-Content -LiteralPath (Join-Path $work 'M5aEditor.csproj') -Encoding utf8
& dotnet build (Join-Path $work 'M5aEditor.csproj') --configuration Release --output $app *> (Join-Path $work 'build.log')
if ($LASTEXITCODE) { throw "Editor verifier compile failed; see $work/build.log" }
Get-ChildItem -LiteralPath $app -Filter 'Vidsc.*.dll' -File | Get-FileHash -Algorithm SHA256 |
    Select-Object @{n='File';e={Split-Path $_.Path -Leaf}},Hash | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $work 'managed-identity.json')
if (!$RunVisible) { Write-Host "Verifier compiled. Visible execution requires prior user agreement and -RunVisible. Evidence: $work"; return }
$previousWork = $env:VIDSC_WORK_DIR
$previousProof = $env:VIDSC_M5A_PROOF_DIR
$previousSource = $env:VIDSC_M5A_PROOF_SOURCE
$previousPath = $env:PATH
try {
    $env:VIDSC_WORK_DIR = Join-Path $work 'state'
    $env:VIDSC_M5A_PROOF_DIR = $work
    $env:VIDSC_M5A_PROOF_SOURCE = $source
    $env:PATH = ($env:PATH -split ';' | Where-Object { $_ -notmatch '(?i)\\ffmpeg\\|\\Python\d*($|\\)|\\miniconda' }) -join ';'
    & dotnet (Join-Path $app 'M5aEditor.dll')
    if ($LASTEXITCODE) { throw "Editor lifecycle failed; evidence: $work" }
    if (!(Test-Path -LiteralPath (Join-Path $work 'summary.json'))) { throw 'Editor proof did not produce a success record.' }
    Write-Host "PASS bounded packaged M5a editor lifecycle. Evidence: $work"
}
finally {
    $env:VIDSC_WORK_DIR = $previousWork; $env:VIDSC_M5A_PROOF_DIR = $previousProof
    $env:VIDSC_M5A_PROOF_SOURCE = $previousSource; $env:PATH = $previousPath
}
