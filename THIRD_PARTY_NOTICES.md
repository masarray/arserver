# Third-Party Notices

ARServer core is licensed under Apache-2.0. This file records third-party components or assets referenced by the project so release packages stay clean and auditable.

## MQTTnet

- Package: `MQTTnet`
- Version: `5.1.0.1559`
- License: MIT
- Project: <https://github.com/dotnet/MQTTnet>
- Purpose: MQTT client/publisher support for publishing IEC 61850 runtime values to an external MQTT broker.
- Distribution note: restored from NuGet during build; not vendored as source in this repository.

MQTTnet's MIT license is compatible with Apache-2.0 distribution.

## Lucide Icons

- Project: Lucide
- License: ISC
- Project: <https://github.com/lucide-icons/lucide>
- Purpose: small UI/documentation icon shapes where used.
- Distribution note: if Lucide SVG paths/assets are copied into ARServer UI or documentation, keep this notice and the ISC license text in the distribution.

Lucide's ISC license is permissive and compatible with Apache-2.0 distribution when the copyright and permission notice are preserved.

## libiec61850 / libIEC61850

- Project: libiec61850 / libIEC61850 by MZ Automation
- License: GPLv3 for the open-source library; commercial licensing is available from MZ Automation.
- Project: <https://github.com/mz-automation/libiec61850>
- Purpose: optional real IEC 61850 MMS runtime that ARServer can load through reflection if the user supplies compatible DLLs beside `ArServer.exe`.
- Distribution note: ARServer does not distribute libiec61850 source code or binaries. Do not include `iec61850.dll`, `iec61850dotnet.dll`, `libiec61850.dll`, or equivalent GPL/commercial runtime files in an Apache-only ARServer release unless the distributor has reviewed and satisfied the applicable license obligations.

This optional runtime separation is intentional: ARServer's Apache-2.0 source can build and run in mock mode without libiec61850. Real IED operation requires the user/distributor to provide the IEC 61850 runtime under its own license terms.
