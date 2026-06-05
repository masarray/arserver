# ARServer — IEC 61850 MMS to Modbus TCP and MQTT Gateway for Windows

[![CI](https://github.com/masarray/arserver/actions/workflows/ci.yml/badge.svg)](https://github.com/masarray/arserver/actions/workflows/ci.yml)
[![Release Package](https://github.com/masarray/arserver/actions/workflows/release-package.yml/badge.svg)](https://github.com/masarray/arserver/actions/workflows/release-package.yml)
[![GitHub Pages](https://github.com/masarray/arserver/actions/workflows/pages.yml/badge.svg)](https://github.com/masarray/arserver/actions/workflows/pages.yml)
[![Latest Release](https://img.shields.io/github/v/release/masarray/arserver?include_prereleases&label=release)](https://github.com/masarray/arserver/releases)
[![License](https://img.shields.io/github/license/masarray/arserver)](LICENSE)
![Platform](https://img.shields.io/badge/platform-Windows%2010%2F11-blue)
![.NET](https://img.shields.io/badge/.NET-8.0-512BD4)

**ARServer** is a free, open-source Windows desktop gateway that helps automation engineers route **IEC 61850 MMS** values into **Modbus TCP** registers and **MQTT** topics for HMI, SCADA, FAT/SAT, relay testing, commissioning, and substation automation labs.

It is built for practical field workflows: add an IED, inspect IEC object references, select SCADA-ready signals, validate the Modbus map, publish MQTT topics, and run a local gateway without a cloud subscription or license key.

> **Free and open source:** ARServer core is licensed under **Apache-2.0**. The application can run in mock/demo mode out of the box. Real IED communication requires IEC 61850 MMS runtime components supplied by the user under their own valid license terms.

<p align="center">
  <a href="https://github.com/masarray/arserver/releases">
    <img src="docs/assets/screenshots/arserver-start.webp" alt="ARServer Windows IEC 61850 MMS to Modbus TCP and MQTT gateway start workspace" width="920">
  </a>
</p>

## What is this?

ARServer is a local Windows gateway for engineers who need a readable bridge between IEC 61850 devices and common supervision layers.

It focuses on three jobs:

1. **Explore IEC 61850 MMS data** from relay or bay-controller models.
2. **Map selected values to Modbus TCP** so HMI/SCADA tools can read familiar coils, discrete inputs, input registers, and holding registers.
3. **Publish selected values to MQTT** so dashboards, web HMI, historians, or integration tools can subscribe to clean topic payloads.

ARServer is not a cloud service and does not require an online account. It runs locally on Windows.

## Why use it?

IEC 61850 is powerful, but many FAT benches, HMI tools, and integration labs still need quick visibility through Modbus TCP or MQTT. ARServer gives engineers a transparent workflow instead of a black-box converter.

Use it when you need to:

- validate relay signals during **FAT/SAT**;
- prototype a **FUXA**, HMI, SCADA, or dashboard integration;
- expose selected IEC 61850 values to **Modbus TCP** clients;
- publish the same values to **MQTT** topics;
- keep IEC object references, value quality, and mapping decisions visible;
- test breaker/status refresh using fast acquisition settings;
- prepare repeatable evidence for engineering review and troubleshooting.

## Features

- **IEC 61850 IED workspace** — add IED endpoints, inspect discovered signals, and keep the IEC Reference visible during selection.
- **SCL/CID/SCD import** — load engineering files and select SCADA/HMI-friendly signals.
- **Modbus TCP server** — publish selected signals as coils, discrete inputs, input registers, or holding registers.
- **MQTT publisher** — send value, quality, status, and optional JSON state payloads to an external MQTT broker.
- **Fast CB acquisition** — prioritize breaker position, switch status, Boolean points, trip/start flags, and protection status over slower analog values.
- **Adjustable MMS polling** — set acquisition target down to 10 ms for expert bench evaluation of one/few critical tags.
- **Runtime cache architecture** — Modbus and MQTT outputs publish from ARServer's cache, so HMI polling does not directly trigger relay reads.
- **Read-only safety posture** — Modbus write functions are rejected by design.
- **Mock mode** — explore UI, mapping, Modbus, and MQTT behavior without a real relay.
- **Portable Windows release** — GitHub Actions can publish a ready-to-run Windows x64 ZIP package.

## Screenshots

| Start workspace | Live IEC values |
|---|---|
| ![ARServer start workspace for adding IEC 61850 IED endpoints](docs/assets/screenshots/arserver-start.webp) | ![ARServer live IEC 61850 values with quality and IEC object reference](docs/assets/screenshots/arserver-iec-values.webp) |

| Modbus TCP map | MQTT topics |
|---|---|
| ![ARServer Modbus TCP server mapping table](docs/assets/screenshots/arserver-modbus-server.webp) | ![ARServer MQTT topic publishing workspace](docs/assets/screenshots/arserver-mqtt-topics.webp) |

## Quick start

The fastest way to try ARServer is to download the Windows portable package from GitHub Releases.

1. Open the [latest release](https://github.com/masarray/arserver/releases/latest).
2. Download `ARServer-vX.Y.Z-win-x64-portable.zip`.
3. Extract the ZIP to a writable folder, for example `C:\Tools\ARServer`.
4. Run `Start-ARServer.bat` or `ArServer.exe`.
5. Use mock mode to explore the workflow, or add your IEC 61850 MMS runtime components for real IED testing.
6. Add an IED, select signals, review the Modbus map, enable Modbus TCP and/or MQTT, then start runtime.

Detailed steps are available in [docs/QUICK_START.md](docs/QUICK_START.md).

## Download / Install / Run

### Portable release

Use the portable ZIP when you want to try ARServer without installing Visual Studio.

```text
ARServer-vX.Y.Z-win-x64-portable.zip
```

The package includes:

- the published Windows application;
- `Start-ARServer.bat` launcher;
- quick-start notes;
- `LICENSE`, `NOTICE`, and third-party notices.

### Build and run from source

Requirements:

- Windows 10/11;
- .NET 8 SDK;
- Visual Studio 2022 or newer with .NET desktop workload, or `dotnet` CLI.

```powershell
git clone https://github.com/masarray/arserver.git
cd arserver
dotnet restore ARServer.sln
dotnet build ARServer.sln -c Release
```

Run from Visual Studio, or start the built executable from:

```text
bin\Release\net8.0-windows\
```

## How it works

```text
IEC 61850 IED / relay
        │
        │  MMS polling / selected points
        ▼
ARServer acquisition engine
        │
        ├── Fast CB lane for breaker/status/Boolean points
        ├── Runtime cache with value, quality, timestamp, and stale state
        ├── Modbus TCP server for HMI/SCADA polling
        └── MQTT publisher for broker-based dashboards
```

Important behavior:

- Modbus and MQTT outputs read from the internal runtime cache.
- FUXA/SCADA Modbus polling does not directly trigger IEC 61850 reads.
- Fast polling values are scheduler targets, not guaranteed device response times.
- For event-grade protection workflows, use the right event/report architecture in the IED design. Fast polling is useful for bench monitoring and HMI refresh evaluation, not a substitute for protection event engineering.

## Typical workflow

1. **Add IED** — connect by IP address and MMS port.
2. **Discover or import** — use live discovery or SCL/CID/SCD files.
3. **Select signals** — choose SCADA-ready status, position, analog, and quality points.
4. **Review IEC Reference** — confirm the exact object path before publishing.
5. **Bind Modbus map** — assign discrete inputs/registers with clear address ranges.
6. **Configure MQTT** — select which values publish to broker topics.
7. **Set acquisition** — choose MMS polling target and Fast CB mode where needed.
8. **Start runtime** — validate live quality, stale state, and output behavior.

## Build from source

```powershell
dotnet restore ARServer.sln
dotnet build ARServer.sln -c Release
```

To create a local Windows portable package:

```powershell
pwsh ./scripts/publish-windows-portable.ps1 -Version 1.0.0-public-beta
pwsh ./scripts/verify-release-package.ps1 -PackagePath ./artifacts/release/ARServer-v1.0.0-public-beta-win-x64-portable.zip
```

See [docs/RELEASE_PACKAGING.md](docs/RELEASE_PACKAGING.md) for release automation details.

## Documentation

- [Quick start](docs/QUICK_START.md)
- [Troubleshooting](docs/TROUBLESHOOTING.md)
- [Release packaging](docs/RELEASE_PACKAGING.md)
- [Validation matrix](docs/VALIDATION_MATRIX.md)
- [Deployment and GitHub Pages](docs/DEPLOYMENT.md)
- [Roadmap](docs/ROADMAP.md)
- [Release notes](docs/RELEASE_NOTES_v1.0.0.md)
- [Security policy](SECURITY.md)
- [Contributing guide](CONTRIBUTING.md)

## GitHub repository SEO

Recommended public repository metadata:

- **Description:** `Open-source IEC 61850 MMS to Modbus TCP and MQTT gateway for Windows HMI, SCADA, relay testing, FAT/SAT, and substation automation labs.`
- **Website:** `https://masarray.github.io/arserver/`
- **Topics:** `iec61850`, `iec-61850`, `mms`, `modbus-tcp`, `mqtt`, `scada`, `hmi`, `fuxa`, `substation-automation`, `relay-testing`, `fat-sat`, `wpf`, `dotnet`, `windows-desktop`, `industrial-automation`, `gateway`

Apply the metadata with GitHub CLI:

```powershell
pwsh ./scripts/Apply-GitHubRepoSeo.ps1 -WhatIf
pwsh ./scripts/Apply-GitHubRepoSeo.ps1
```

## Roadmap / planned improvements

Planned improvements are tracked in [docs/ROADMAP.md](docs/ROADMAP.md). Current priorities include:

- stronger acquisition diagnostics;
- clearer mapping validation;
- richer evidence export;
- improved multi-IED scheduling;
- optional event/report-oriented acquisition where available;
- more sample configurations and screenshots.

## Contributing

Contributions are welcome. Useful contributions include:

- relay model compatibility reports;
- SCL import edge cases;
- Modbus mapping validation improvements;
- MQTT payload examples;
- UI/UX improvements for engineering workflows;
- documentation and screenshot updates;
- test benches or mock datasets that can be shared publicly.

Please read [CONTRIBUTING.md](CONTRIBUTING.md) before opening a pull request. Do not upload private substation files, customer names, network addresses, relay passwords, or confidential SCL files to public issues.

## License

ARServer core is licensed under the [Apache License 2.0](LICENSE).

Third-party notices are available in [THIRD_PARTY_NOTICES.md](THIRD_PARTY_NOTICES.md). Optional runtime components supplied by users remain governed by their own license terms and are not redistributed in ARServer source or release packages.
