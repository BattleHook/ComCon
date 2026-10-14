# ComCon

ComCon is a standalone Windows COM control tool built with .NET 10 and WPF.
It owns a radio's physical serial connection and provides RAW bridging for a
Legacy CAT application such as Commander, or command-capable local IPC for
independent clients. Ham Shack Console is not a runtime dependency.

**Publication draft:** the first planned release is `v0.1.0-beta.1` for Windows
11 x64. No downloadable release, installer or completed HTML guide is claimed
by this document. Packaging and release acceptance are still in preparation.

## What ComCon provides

- Up to four independently managed profiles, subject to port-conflict checks.
- RAW mode that forwards bytes unchanged between a physical radio port and
  the ComCon endpoint of a virtual COM pair.
- Command IPC for CI-V and Kenwood ASCII, with one owning client session per
  active profile. Clients supply commands and interpret model-specific replies.
- Explicit start/stop confirmations, profile status and visible errors.
- Local configuration loading, validation and saving.

An IPC-only client does not need a virtual COM pair unless a Legacy application
also uses one. ComCon is not a generic TCP or HTTP radio-control API.

## Important limitations

**RAW is not a safety filter:** it forwards all bytes, including transmit and
tune commands. A connected pipe, successful write, cached value or restarted
bridge does not prove radio state, command completion or safe handover.

CI-V and Kenwood implementation does not certify every radio model or firmware.
Yaesu RAW/framing is experimental and hardware-unverified; Yaesu command IPC
is unavailable. Offline tests do not replace real-device acceptance.
Do not have other applications open the same physical radio port directly.
Automatic startup-session restoration is not implemented.

## Installation and first use

The planned distribution is an **unsigned, self-contained portable ZIP** for
Windows 11 x64, accompanied by SHA-256 checksums. It will include the required
.NET runtime; a separate runtime installation will not be required for that
package. An installer is deferred.

Until a verified package and guide are published, build from source as described
below. Launch the resulting ComCon UI executable only when you intend to use it;
building or publishing alone does not launch or deploy the application.

Before deliberately starting a bridge:

1. Identify the correct physical radio port, serial settings and transport mode.
2. For Legacy/RAW use, verify the virtual pair and which endpoint each
   application opens. ComCon opens the physical port and its virtual endpoint;
   the Legacy application opens the paired peer.
3. Create or load a configuration, validate it and save to a writable location.
4. Review the start confirmation and the actual profile/session status.
   Stop or fault status is not evidence of physical-radio resynchronization.

Virtual-COM drivers are not bundled or installed automatically. The planned
guide will use a com0com pair-configuration example, assuming a suitable
installed driver. A currently verified Windows 11 x64 driver package/signature
has not been established for this release. Do not disable Windows signing or
security protections to install an unverified driver.

## Help and independent IPC clients

The English HTML guide is in [Documentation/Help/index.html](Documentation/Help/index.html)
and is bundled as `Help\index.html` for offline use (an online copy may follow
later). It covers installation, configuration, operation, troubleshooting,
update/uninstall and connecting your own IPC client, with an illustrative C#
example in [ipc-client-example.cs](Documentation/Help/ipc-client-example.cs).
The example is documentation only and is not compiled or tested by the build.

The main window has **Help (F1)** and **About** buttons. Help and F1 open the
bundled guide in the default browser; if the file is missing, ComCon shows an
error and does not fall back to an online copy. About shows the version,
attribution, license, third-party notices, repository/issue links and an
optional Ko-fi button. A one-time welcome per Windows user (stored in
`%LOCALAPPDATA%\ComCon\FirstUse.json`) offers Continue, Open Help and optional
Ko-fi support. ComCon never requires donations and opens a browser only when
you click a link.

## Build and hardware-free tests

Use Windows and a .NET 10 SDK. C# remains at version 12; nullable checks and
warnings-as-errors are enabled. Run from the source root:

```powershell
dotnet restore .\ComConApp.sln --verbosity normal
dotnet build .\ComConApp.sln --configuration Release --no-restore --verbosity normal
dotnet test .\tests\ComCon.Core.Tests\ComCon.Core.Tests.csproj --configuration Release --no-build --no-restore --filter "TestCategory!=IsolatedCom&FullyQualifiedName!~ComConIsolatedSerial" --verbosity normal --logger "console;verbosity=normal"
```

The tests use MSTest/VSTest. Keep the exclusions above for routine offline runs;
do not enable isolated serial/COM tests without a separately prepared setup.
Framework-dependent builds need the matching .NET runtime; the UI needs the
.NET 10 Windows Desktop runtime. The Windows PowerShell interoperability tests
also need Windows PowerShell.

Tests use synthetic local profile/bridge fixtures and reviewed, hash-pinned
test-only helper snapshots. No sibling HSC checkout or station configuration
is required. Preserve the snapshots' license, provenance and byte hashes.
The fixtures are not included in application packages.

`ActualHscSafetyHelperSharesSubmissionDeadlineWithCommanderQueue` starts a real
Windows PowerShell child process and asserts tight timing windows. Under the CPU
load of a full suite run it can fail; this is a test timing artefact, not an
application fault. Before each release, run it on its own and confirm both cases
pass:

```powershell
dotnet test .\tests\ComCon.Core.Tests\ComCon.Core.Tests.csproj --configuration Release --no-build --filter "FullyQualifiedName~ActualHscSafetyHelperSharesSubmissionDeadlineWithCommanderQueue" --verbosity normal
```

Source layout:

- [Core](src/ComCon.Core): configuration, routing, protocol and transport logic.
- [Windows integration](src/ComCon.Windows): pipes and serial endpoints.
- [WPF UI](src/ComCon.UI): profile configuration and bridge controls.
- [Tests](tests/ComCon.Core.Tests): regression and interoperability coverage.
- [Offline demo](tools/ComCon.OfflineDemo): in-memory simulation tooling.

## Support

The planned public repository is **BattleHook/ComCon**. Support will use its
GitHub Issues once publication opens, without a guaranteed response time.
Report the version, transport mode, relevant error and reproduction steps;
review diagnostic material for private station data before sharing it.

## Optional support

ComCon is free to use. If it is useful to you, you can optionally support its
development with a one-time donation on [Ko-fi](https://ko-fi.com/battlehook).
You choose the amount; donations are not required and do not unlock features.

[![ko-fi](https://ko-fi.com/img/githubbutton_sm.svg)](https://ko-fi.com/U7W728RRWG)

## Attribution and licensing

Developed by **BattleHook (PA1RGX)**. The original code and documentation are
released under the [MIT License](LICENSE). Redistributed .NET runtime and
package components, referenced tools and artwork provenance are described in
[THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md); the portable package also
contains their license texts in a `licenses` folder. No exclusive copyright is
claimed in AI-generated artwork.
