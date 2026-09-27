# Third-party components — iTunesPod 0.4 WPF

Independent third-party application, not affiliated with Apple. Apple Music https://music.apple.com/cn/new is a visual layout reference. No Apple logo, website screenshots, website source code, commercial font files or online catalogue are distributed. User music artwork is read from local media and cached on their computer.

| Component | Version | License / source | Use |
|---|---|---|---|
| .NET / WPF Windows Desktop runtime | 8.0.24 | MIT, https://github.com/dotnet/wpf and https://github.com/dotnet/runtime | Native Windows desktop UI |
| NAudio | 2.2.1 | MIT, https://github.com/naudio/NAudio | Windows audio decoding and playback |
| TagLibSharp | 2.3.0 | LGPL-2.1-only, https://github.com/mono/taglib-sharp | Media tags and embedded artwork |
| Microsoft.Data.Sqlite | 8.0.24 | MIT, https://github.com/dotnet/efcore | Local index |
| System.Management | 8.0.0 | MIT, https://github.com/dotnet/runtime | Associated-volume identity and notifications |
| SQLitePCLRaw / SQLite | See packages.lock.json | Apache-2.0 / SQLite public domain | Native SQLite interop |
| iOpenPod device catalogue and timezone names | a20242428b93b672d14b37230772dbad75139c69 | MIT, Copyright (c) 2025 John Gibbons | Adapted USB ID, model, serial suffix and city/timezone mapping data |
| xUnit / Microsoft.NET.Test.Sdk / coverlet | See test project | Upstream licenses | Development tests |
| MiSans | locally installed | Xiaomi MiSans Font Intellectual Property License Agreement 1.0, https://hyperos.mi.com/font/en/download/ | Application typography; the font software is not redistributed in this package |
| Apple iPod product imagery | model reference page | https://support.apple.com/zh-cn/103823 | Device identification gallery; images are background-extracted for presentation |

The device catalogue is adapted from https://github.com/TheRealSavi/iOpenPod/blob/a20242428b93b672d14b37230772dbad75139c69/src/iopenpod/device/lookup.py . Full MIT notice is retained in licenses/iOpenPod-MIT.txt. Device probing and database parsing are independent C# implementations. Reference support does not establish device write certification.

TagLibSharp is unmodified and distributed as a separate replaceable assembly. Its source is available at https://github.com/mono/taglib-sharp/tree/TaglibSharp-2.3.0.0 . The LGPL text is included. Do not statically merge, trim or single-file publish without reviewing replacement/relinking requirements.

The active build has no Avalonia or FluentAvalonia dependencies. Historical 0.2 source is retained outside the active solution under .build/legacy-avalonia-v02. Runtime dependency notices are collected by collect-notices.ps1 into artifacts/win-x64-wpf/licenses.

MiSans is selected globally at SemiBold weight when it is installed on Windows. In accordance with its license, the application states its use here and does not separately redistribute, sell, sublicense, or modify the font software. Windows supplies its normal font fallback if MiSans is unavailable.

The iPod model gallery is organized from Apple's official identification page and cross-checked against MacDB (https://macdb.cn/). Product imagery remains the property of its respective owner. It is included only to identify supported hardware in this independent device-management application.


FFmpeg / FFprobe 8.1.2-full_build-www.gyan.dev are bundled as unmodified, separately invoked and replaceable executable programs. This GPL v3 build includes third-party codecs and libraries; its original LICENSE and README (configuration and exact upstream source commit 38b88335f9) are retained in licenses/ffmpeg. Build source information: https://www.gyan.dev/ffmpeg/builds/ . Tool versions and SHA-256 hashes are recorded in tools/versions.json.

HASH58, iTunesDB, Play Counts, smart-rule and artwork/photo structures were researched against pinned iOpenPod protocol definitions. C# implementations preserve opaque records and use independent checksum substitutions. The 204 city-to-IANA timezone names are adapted from the same pinned itunesdb_shared/device_time.py; the iOpenPod MIT notice applies to these factual tables.
