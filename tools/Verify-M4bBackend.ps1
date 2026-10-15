param(
    [Parameter(Mandatory)][string]$ManifestPath,
    [string]$AppDirectory = 'artifacts/release/app',
    [string]$Cells = 'all',
    [string]$HelperDirectory = '-',
    [string]$ManagedDirectory = '',
    [string]$OutputDirectory = ''
)
$ErrorActionPreference = 'Stop'
$repo = Split-Path $PSScriptRoot -Parent
Set-Location $repo
$app = [IO.Path]::GetFullPath($AppDirectory)
if (!$ManagedDirectory) { $ManagedDirectory = $app }
if (!$OutputDirectory) { $OutputDirectory = Join-Path $repo ('artifacts/m4b/adapter-' + [Guid]::NewGuid().ToString('N')) }
$work = [IO.Path]::GetFullPath($OutputDirectory)
if (Test-Path -LiteralPath $work) { throw 'Verifier requires a fresh owned output directory.' }
New-Item -ItemType Directory -Path $work | Out-Null
$references = @('Vidsc.Core','Vidsc.Media','FlyleafLib') | ForEach-Object {
    $path = [Security.SecurityElement]::Escape((Join-Path ([IO.Path]::GetFullPath($ManagedDirectory)) ($_.ToString()+'.dll')))
    "<Reference Include=`"$_`"><HintPath>$path</HintPath></Reference>"
}
$runner = [Security.SecurityElement]::Escape((Join-Path $PSScriptRoot 'verification/M4bBackend.cs'))
@"
<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><OutputType>Exe</OutputType><TargetFramework>net10.0-windows</TargetFramework><UseWPF>true</UseWPF><ImplicitUsings>enable</ImplicitUsings><Nullable>enable</Nullable><UseSharedCompilation>false</UseSharedCompilation></PropertyGroup><ItemGroup>$references<Compile Include="$runner"/></ItemGroup></Project>
"@ | Set-Content -LiteralPath (Join-Path $work 'M4bBackend.csproj') -Encoding utf8
& dotnet run --project (Join-Path $work 'M4bBackend.csproj') --configuration Release -- `
    $app ([IO.Path]::GetFullPath($ManifestPath)) $work $Cells $HelperDirectory (Join-Path $PSScriptRoot 'verification/m4b_damage.py')
if ($LASTEXITCODE) { throw "M4b adapter verification failed; evidence retained in $work" }
Write-Host "PASS selected production adapter cells; evidence retained in $work"
