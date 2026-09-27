# Contributing to iTunesPod

Thanks for helping improve iTunesPod. Bug reports, documentation fixes, usability feedback, and carefully validated device research are welcome.

## Before opening an issue

- Search existing issues first and include the app version, Windows version, and a short reproduction path.
- Describe what you expected and what happened. Attach screenshots only after removing names, personal media, paths, serial numbers, and device identifiers.
- Never attach an iTunesDB, device backup, personal music, signing key, access token, or unredacted log.

## Development

Requirements: Windows, .NET 8 SDK, and Git.

```powershell
dotnet restore iTunesPod.sln
dotnet build iTunesPod.sln -c Release
dotnet test iTunesPod.sln -c Release
```

Keep changes focused, preserve the existing recovery and privacy boundaries, and update documentation when behavior or device support changes. Do not introduce external network calls that upload local library or device data.

## Device compatibility changes

Device writes are enabled only for hardware and database combinations that have been independently verified. A successful parse, matching model name, or simulator test is not write certification. Proposals for a new write profile should include repeatable test steps, device and firmware details, before/after hashes, failure-recovery results, and confirmation that private media and identifiers were removed from all evidence.

Do not ask users to test unverified writes on irreplaceable devices. Do not commit device dumps, serial numbers, user media, or private protocol captures.

## Pull requests

Include the motivation, user-visible behavior, and validation performed. Keep generated build output, local validation screenshots, and internal planning documents out of pull requests. CI runs the solution test suite on Windows.
