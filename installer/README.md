# ARServer Windows Installer

This folder contains the Inno Setup script used to build the Windows installer.

Build flow:

1. Publish the app to `artifacts/publish/win-x64`.
2. Compile `installer/ARServer.iss` with Inno Setup 6.
3. Zip the generated setup executable for GitHub Releases.

The installer includes the Apache-2.0 license, NOTICE, third-party notices, third-party license texts, and README. It should not bundle libiec61850 DLLs unless the distributor has a separate redistribution right or commercial license.
