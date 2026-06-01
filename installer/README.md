# ARServer Windows Installer

This folder contains the Inno Setup script used to build the Windows installer.

Build flow:

1. Publish the app to `artifacts/publish/win-x64`.
2. Compile `installer/ARServer.iss` with Inno Setup 6.
3. Zip the generated setup executable for GitHub Releases.

The installer includes the GPL-3.0 license, third-party notices, and README.
