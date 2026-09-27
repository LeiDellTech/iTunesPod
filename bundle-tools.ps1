$ErrorActionPreference='Stop'
$releaseRoot=Join-Path $PSScriptRoot 'artifacts/win-x64-wpf'
$toolDirectory=Join-Path $releaseRoot 'tools'
$noticeDirectory=Join-Path $releaseRoot 'licenses/ffmpeg'
New-Item -ItemType Directory -Force -Path $toolDirectory,$noticeDirectory | Out-Null
$records=@()
foreach($toolName in @('ffmpeg','ffprobe')) {
    $toolSource=(Get-Command "$toolName.exe" -CommandType Application -ErrorAction Stop).Source
    $toolTarget=Join-Path $toolDirectory "$toolName.exe"
    Copy-Item -LiteralPath $toolSource -Destination $toolTarget
    $toolVersion=(& $toolTarget -version | Select-Object -First 1)
    if($LASTEXITCODE -ne 0){throw "Cannot verify $toolName"}
    $records+=@{Name=$toolName;Version=$toolVersion;SHA256=(Get-FileHash -LiteralPath $toolTarget -Algorithm SHA256).Hash}
    $distributionRoot=Split-Path -Parent (Split-Path -Parent $toolSource)
    foreach($noticeName in @('LICENSE','README.txt')) {
        $noticeSource=Join-Path $distributionRoot $noticeName
        if(!(Test-Path -LiteralPath $noticeSource)){throw "Missing FFmpeg distribution notice: $noticeSource"}
        Copy-Item -LiteralPath $noticeSource -Destination (Join-Path $noticeDirectory $noticeName)
    }
}
$records | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $toolDirectory 'versions.json') -Encoding utf8
@'
FFmpeg and FFprobe are separate, replaceable GPL v3 executable programs.
This package includes the unmodified Gyan Windows distribution binaries.
See LICENSE and README.txt for the license, exact FFmpeg source commit and build configuration.
FFmpeg source: https://github.com/FFmpeg/FFmpeg/commit/38b88335f9
Windows build and source information: https://www.gyan.dev/ffmpeg/builds/
Do not remove these notices when redistributing the tool directory.
'@ | Set-Content -LiteralPath (Join-Path $noticeDirectory 'SOURCE-INFORMATION.txt') -Encoding utf8
