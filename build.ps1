param([ValidateSet('build','test','publish','installer','run','smoke')][string]$Task='build')
$ErrorActionPreference='Stop'
$projectRoot=$PSScriptRoot
$env:TEMP=Join-Path $projectRoot '.build/temp'
$env:TMP=$env:TEMP
$env:NUGET_PACKAGES=Join-Path $projectRoot '.build/nuget'
$env:NUGET_HTTP_CACHE_PATH=Join-Path $projectRoot '.build/http'
New-Item -ItemType Directory -Force -Path $env:TEMP,$env:NUGET_PACKAGES,$env:NUGET_HTTP_CACHE_PATH,(Join-Path $projectRoot '.build/feed') | Out-Null
Set-Location $projectRoot
switch ($Task) {
 'build' { dotnet build iTunesPod.sln -c Release }
 'test' { dotnet test iTunesPod.sln -c Release }
 'publish' { dotnet publish src/iTunesPod.App -c Release -r win-x64 --self-contained true -o artifacts/win-x64-wpf; if ($LASTEXITCODE -eq 0) { & (Join-Path $projectRoot 'collect-notices.ps1'); & (Join-Path $projectRoot 'bundle-tools.ps1') } }
 'installer' { dotnet publish src/iTunesPod.App -c Release -r win-x64 --self-contained true -o artifacts/win-x64-wpf; if ($LASTEXITCODE -eq 0) { & (Join-Path $projectRoot 'collect-notices.ps1'); & (Join-Path $projectRoot 'bundle-tools.ps1') }; if ($LASTEXITCODE -eq 0) { $compiler=(Get-Command ISCC.exe -ErrorAction Stop).Source; & $compiler (Join-Path $projectRoot 'packaging/iTunesPod.iss') } }
 'run' { dotnet run --project src/iTunesPod.App -c Release }
 'smoke' { dotnet run --project src/iTunesPod.App -c Release -- --smoke-test (Join-Path $projectRoot 'artifacts/validation/ui') }
}
exit $LASTEXITCODE

