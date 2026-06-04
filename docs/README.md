# ARServer Landing Page Assets

This folder contains the static product landing page and compressed WebP assets used by the repository README and website.

## Runtime Notes

ARServer itself is a Windows desktop gateway. Real IEC 61850 communication requires user-supplied `iec61850dotnet.dll` and `iec61850.dll` beside `ArServer.exe` under those DLLs' own license terms. MQTT output requires an external MQTT broker such as Mosquitto, EMQX, or HiveMQ.

Installer builds are published at:

https://github.com/masarray/arserver/releases
