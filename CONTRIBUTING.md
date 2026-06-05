# Contributing to ARServer

Contributions are welcome from automation engineers, protection engineers, system integrators, software developers, and users who test ARServer in lab environments.

## Good contribution areas

- SCL/CID/SCD import improvements.
- Modbus mapping validation.
- MQTT payload examples.
- Fast acquisition diagnostics.
- UI improvements for engineering workflows.
- Documentation, screenshots, and quick-start examples.
- Mock data sets that can be shared publicly.

## Before opening an issue

Please check:

- the latest release;
- existing issues;
- [docs/TROUBLESHOOTING.md](docs/TROUBLESHOOTING.md);
- whether the problem appears in mock mode or only with a real IED.

## Issue report checklist

Include:

- ARServer version;
- Windows version;
- connection mode: mock or real IED;
- steps to reproduce;
- expected behavior;
- actual behavior;
- screenshots or sanitized log excerpts.

Do not upload confidential engineering files, private customer data, relay passwords, public IP addresses, VPN details, or full substation network diagrams.

## Pull request checklist

Before submitting a pull request:

1. Keep changes focused.
2. Build the solution locally.
3. Update documentation when behavior changes.
4. Add or update screenshots when UI changes are visible.
5. Keep wording user-facing and product-oriented.
6. Do not commit build output, secrets, runtime DLLs, capture files, or customer data.

Recommended local checks:

```powershell
dotnet restore ARServer.sln
dotnet build ARServer.sln -c Release
pwsh ./scripts/publish-windows-portable.ps1 -Version 0.0.0-local-test -SkipBuild:$false
pwsh ./scripts/verify-release-package.ps1 -PackagePath ./artifacts/release/ARServer-v0.0.0-local-test-win-x64-portable.zip
```

## Documentation style

Write for users who want to understand, download, run, build, and contribute.

Good wording:

- “Download the portable package.”
- “Build from source.”
- “Use Fast CB for few critical status points.”
- “Validate mappings before field use.”

Keep documentation focused on what users can understand, run, validate, and contribute.
