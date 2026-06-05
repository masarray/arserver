# Release Packaging

ARServer includes GitHub Actions and local PowerShell scripts for creating a Windows x64 portable package.

## Portable package name

```text
ARServer-v<version>-win-x64-portable.zip
```

Example:

```text
ARServer-v1.0.1-public-beta-win-x64-portable.zip
```

## Package contents

The portable ZIP contains:

- `ArServer.exe` single-file Windows application executable;
- `README_QUICK_START.txt`;
- `LICENSE`;
- `NOTICE` if present;
- `THIRD_PARTY_NOTICES.md` if present;
- `LICENSES/` if present.

The package does not include user-supplied IEC 61850 runtime components.

## Create package locally

Run from the repository root:

```powershell
powershell -ExecutionPolicy Bypass -File ./scripts/publish-windows-portable.ps1 -Version 1.0.1-public-beta
```

Verify the package:

```powershell
powershell -ExecutionPolicy Bypass -File ./scripts/verify-release-package.ps1 -PackagePath ./artifacts/release/ARServer-v1.0.1-public-beta-win-x64-portable.zip -RequireSingleFileApp
```

## GitHub Actions release workflow

The workflow is available at:

```text
.github/workflows/release-package.yml
```

It supports manual execution from the **Actions** tab with these inputs:

- `version`
- `publish_release`
- `prerelease`
- `draft`
- `release_notes_file`

The workflow can also run on tags matching:

```text
v*
```

When a GitHub Release is published, the workflow uploads:

- portable ZIP;
- `SHA256SUMS.txt`.

## Release notes body

Release notes are written for users and explain:

- what changed;
- who benefits;
- how to try the release;
- any safety or validation notes.

Keep release notes focused on what users can download, try, validate, and report.
