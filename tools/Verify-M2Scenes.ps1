param([string]$PythonPath)
$ErrorActionPreference = 'Stop'
$repo = Split-Path $PSScriptRoot -Parent
if (!$PythonPath) { $PythonPath = Join-Path $repo 'artifacts/runtime/scene/python/python.exe' }
$PythonPath = [IO.Path]::GetFullPath($PythonPath)
$driver = Join-Path $repo 'tools/scene/vidsc_scene_job.py'
$output = Join-Path $repo ('artifacts/m2-scenes/' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $output -Force | Out-Null
& $PythonPath (Join-Path $repo 'tools/scene/test_m2_timing.py') --driver $driver --work-dir $output
if ($LASTEXITCODE) { throw 'Scene decoded-timing fixture check failed.' }
$mediaProject = Join-Path $repo 'src/Vidsc.Media/Vidsc.Media.csproj'
@"
<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><OutputType>Exe</OutputType><TargetFramework>net10.0-windows</TargetFramework><ImplicitUsings>enable</ImplicitUsings></PropertyGroup><ItemGroup><ProjectReference Include="$mediaProject" /></ItemGroup></Project>
"@ | Set-Content -LiteralPath (Join-Path $output 'SceneContract.csproj') -Encoding utf8
@'
using Vidsc.Media.Inspection;
using Vidsc.Media.Scene;
try {
    var source = args[2];
    var request = new SceneJobRequest("offset-vfr-adapter",InspectionSourceIdentity.Capture(source).IdentityKey,
        Generation:7,SourceOriginSeconds:10,MinSceneLenFrames:1);
    var worker = new SceneWorker(args[0],args[1]);
    var result = await worker.RunAsync(source,.03,.7,"content",default,request:request);
    if(!result.Success || result.Scenes.Count<2 || result.CacheIdentity is null || result.Scenes.Any(s=>s.StartPts is null)
        || result.Scenes[^1].EndIsPresentationBoundary) throw new Exception(result.Error ?? "Scene adapter contract failed");
    var stale = await worker.RunAsync(source,.03,.7,"content",default,request:request with {SourceIdentity="stale"});
    if(stale.Success || stale.Process is not null) throw new Exception("Stale scene source launched worker");
    Console.WriteLine($"PASS sceneAdapter=offset-vfr scenes={result.Scenes.Count} sourceIdentity=verified staleSource=rejected finalEnd=unverified");
} catch(Exception error) { Console.Error.WriteLine(error); return 1; }
return 0;
'@ | Set-Content -LiteralPath (Join-Path $output 'Program.cs') -Encoding utf8
& dotnet run --project (Join-Path $output 'SceneContract.csproj') --configuration Release -- $PythonPath $driver (Join-Path $output 'scene-offset-vfr.mkv')
if ($LASTEXITCODE) { throw 'Scene C# adapter contract failed.' }
