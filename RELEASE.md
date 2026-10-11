# Release workflow

The maintained release directory on the author's machine is
`D:\KSP\KSP_Git\ArmorControl_Release`. This is a local convention, not a build prerequisite.
Current runtime version: **0.1.1**, assembly version **0.1.1.0**.

## Build and verify

Requirements: Windows, PowerShell 7, .NET 8 SDK with .NET Framework 4.6.1 targeting
support, Node.js 20+, and a legally obtained local KSP 1.12.5 installation.
Install MechJeb **2.14.3** (file version 2.14.3.0) and VesselView into that installation.
MJ's assembly identity is 2.5.1.0; it must not be confused with its release/file version.
Trajectories is optional at runtime and is not needed for compilation.
Do not commit or distribute KSP or third-party assemblies.

Run from any checkout of this repository:

```powershell
./Tools/ArmorControl.VerifyRelease.ps1 -KspRoot 'C:\Games\KSP' -ArtifactsDirectory 'C:\Build\ArmorControl'
```

When the checkout is installed under `KSP/GameData/ArmorControl`, `-KspRoot` can
be omitted. Otherwise it is required for compilation. Version defaults to the
value in `Source/ArmorControl/ArmorControl.csproj`.

Build/test outputs go to a checkout-specific temporary directory unless
`-ArtifactsDirectory` is supplied. The artifact directory must be outside
GameData: KSP recursively loads DLLs there, including test DLLs.
The output DLL is `<artifacts>/ArmorControl/bin/Release/ArmorControl.dll`.
Intermediate files are isolated per project. Tools do not modify the installed
plugin, start KSP, connect to a running game, or change a save.
Transport tests start their own temporary loopback-only server.

The gate runs production compilation; native HTTP/WebSocket, FIFO and multi-client
regressions; native catalog/configuration tests; production DLL serializer-dependency
and pinned-assembly checks; frontend syntax/localization/recorder/orbit regressions;
and ascent single-field tests against MechJeb stubs.
Stub tests are not in-game acceptance tests.

For an already built DLL, skip compilation but still run all checks:

```powershell
./Tools/ArmorControl.VerifyRelease.ps1 -SkipBuild -DllPath 'C:\Build\ArmorControl.dll' -Version 0.1.1
```

## Package

```powershell
./Tools/ArmorControl.PackageRelease.ps1 -KspRoot 'C:\Games\KSP' -Version 0.1.1 -ReleaseDirectory 'C:\Releases\ArmorControl'
```

Use `-ReleaseDirectory 'D:\KSP\KSP_Git\ArmorControl_Release'` on the author's machine.
Without it, packages go under the artifact directory's `Releases` folder.
`-ArtifactsDirectory`, `-SkipBuild` and `-DllPath` work as in the verification entry point.
The full test gate always runs; no verification-bypass switch is provided.

Outputs:

- `ArmorControl-0.1.1.zip`
- `ArmorControl-0.1.1.zip.sha256`
- `ArmorControl-0.1.1-CHANGELOG.md`

Existing output files are never overwritten. The package contains only:

```text
GameData/ArmorControl/
  Plugins/ArmorControl.dll
  prototype/                  # explicit web/Localization file allowlist
  settings.cfg                # from settings.example.cfg, never personal settings
  README.md
  README.zh-CN.md
  CHANGELOG.md
```

Source, Git data, screenshots, tests, build tools, intermediate outputs, and dependency
DLLs are not included. Packaging validates SHA-256, exact archive paths, web/catalog
contents, default settings, and the chosen DLL. Documentation contents are protected
by the archive checksum but need not equal documentation subsequently edited on main.
A package can be checked independently:

```powershell
./Tools/ArmorControl.VerifyPackage.ps1 -ArchivePath 'C:\Releases\ArmorControl\ArmorControl-0.1.1.zip' -DllPath 'C:\Build\ArmorControl.dll'
```

For installation testing, close KSP and copy only the packaged runtime files.
Preserve an existing personal `settings.cfg`; refresh the browser after restarting
KSP so its frontend matches the new DLL. Build scripts do not perform this deployment.

## Release provenance

The already published v0.1.1 ZIP uses the legacy GitHub release tag `EarlyBeta1`.
That historical tag points at pre-0.1.1 source (`059724b`); it is not a valid source
reference for the v0.1.1 binary. Do not move that tag or overwrite the existing release.
The correct v0.1.1 runtime source is commit `732ab64`; use the standard `v0.1.1`
tag at that commit. Tooling added subsequently is available on `main`.

Published ZIP SHA-256:
`5A6833AB85A84761CA4A2CA02CE635BF725C33D623B45E85E85A63A91D966511`.

As checked on 2026-10-11, [NetKAN](https://github.com/KSP-CKAN/NetKAN/blob/master/NetKAN/ArmorControl.netkan)
still only recommends MechJeb. Required metadata: `depends: MechJeb2 (= 2.14.3.0), VesselView`;
`recommends: Trajectories`. The user has already reported this upstream.
Do not assume CKAN installs all dependencies or resume the cancelled PR without a new request.
