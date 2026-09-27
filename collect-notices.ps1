$ErrorActionPreference='Stop'
$packageRoot=Join-Path $PSScriptRoot '.build/nuget'
$outputRoot=Join-Path $PSScriptRoot 'artifacts/win-x64-wpf/licenses'
New-Item -ItemType Directory -Force -Path $outputRoot | Out-Null
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'THIRD-PARTY-NOTICES.md') -Destination (Join-Path $PSScriptRoot 'artifacts/win-x64-wpf/THIRD-PARTY-NOTICES.md')
@'
iTunesPod 0.4.0 · Windows x64
解压整个目录，双击 iTunesPod.App.exe。不要单独移动 EXE。
自带 .NET 8.0.24、FFmpeg 和 FFprobe，无需另装运行时和转换工具。
音乐导入/监视、歌曲信息与封面编辑、试听、播放列表/M3U/智能规则、RSS 播客、视频分类与转码、设备照片相册及备忘录已接入。
Nano 4 的同步流程包含内容去重、预览、HASH58 签名、媒体与封面写入、复读、快照、失败回滚和恢复日志。
电脑源文件标签和设备内容分别保存；删除源文件前备份。设备删除不会删除电脑源文件。
每次设备写入前自动保存恢复备份。快照不包含全部音乐，完整备份包含媒体，可继续未完成的备份。
本版面向 Nano 4 / 数据库版本 115；未知规则和未知数据库写入会阻止操作。
实机已完成短音频写入、HASH58 复核、媒体复读和恢复往返测试；测试后数据库、元数据和媒体集合均精确恢复。耳机播放、照片显示、安全弹出后的物理状态和断电恢复尚待人工验收。
本机资料保存在 %LOCALAPPDATA%\iTunesPod。网络封面默认关闭，RSS 和手工下载需要网络。
Ctrl+F 搜索；Enter 播放；双击编辑；Ctrl/Shift 多选；Ctrl+Z 撤销同步草稿。
此产品为独立第三方应用，与 Apple 无官方关联。
'@ | Set-Content -LiteralPath (Join-Path $PSScriptRoot 'artifacts/win-x64-wpf/使用说明.txt') -Encoding utf8
$lock=Get-Content -LiteralPath (Join-Path $PSScriptRoot 'src/iTunesPod.App/packages.lock.json') -Raw | ConvertFrom-Json
foreach ($framework in $lock.dependencies.PSObject.Properties) {
 foreach ($dependency in $framework.Value.PSObject.Properties) {
  if ($dependency.Value.type -eq 'Project') { continue }
  $packageId=$dependency.Name.ToLowerInvariant()
  $packageVersion=$dependency.Value.resolved
  $packageDirectory=Join-Path $packageRoot "$packageId/$packageVersion"
  if (!(Test-Path -LiteralPath $packageDirectory)) { continue }
  $noticeDirectory=Join-Path $outputRoot "$packageId-$packageVersion"
  New-Item -ItemType Directory -Force -Path $noticeDirectory | Out-Null
  Get-ChildItem -LiteralPath $packageDirectory -File -Recurse | Where-Object { $_.Name -match 'license|copying|notice|\.nuspec$|ofl' } | ForEach-Object {
   $relativeNotice=$_.FullName.Substring($packageDirectory.Length).TrimStart('\','/')
   $noticeTarget=Join-Path $noticeDirectory $relativeNotice
   New-Item -ItemType Directory -Force -Path (Split-Path -Parent $noticeTarget) | Out-Null
   Copy-Item -LiteralPath $_.FullName -Destination $noticeTarget
  }
 }
}
Invoke-WebRequest -Uri 'https://raw.githubusercontent.com/mono/taglib-sharp/TaglibSharp-2.3.0.0/COPYING' -OutFile (Join-Path $outputRoot 'TagLibSharp-LGPL-2.1.txt')

Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'licenses/iOpenPod-MIT.txt') -Destination (Join-Path $outputRoot 'iOpenPod-MIT.txt')


$desktopRuntimeLicense=Join-Path $packageRoot 'microsoft.windowsdesktop.app.runtime.win-x64/8.0.24/LICENSE'
if(Test-Path -LiteralPath $desktopRuntimeLicense){Copy-Item -LiteralPath $desktopRuntimeLicense -Destination (Join-Path $outputRoot 'WPF-runtime-MIT.txt')}
