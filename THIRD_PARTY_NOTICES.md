# Third-Party Notices

This file records third-party components and assets that may be included in ARServer source or release packages.

## MQTTnet

- Purpose: MQTT client publishing support.
- License: MIT License.
- Notice file: `LICENSES/MQTTNET-MIT.txt`.

## Lucide Icons

- Purpose: selected SVG icon shapes used in the application interface and documentation.
- License: ISC License.
- Notice file: `LICENSES/LUCIDE-ISC.txt`.

## User-supplied IEC 61850 MMS runtime components

Real IED communication requires compatible IEC 61850 MMS runtime components supplied by the user or distributor. These components are not bundled in ARServer source or release packages and remain governed by their own license terms.

## Release packaging rule

Public ARServer portable packages include:

- ARServer application files;
- `LICENSE`;
- `NOTICE`;
- `THIRD_PARTY_NOTICES.md`;
- `LICENSES/` when present;
- quick-start documentation.

Public ARServer portable packages do not include user secrets, private engineering files, relay exports, packet captures, or user-supplied runtime binaries unless the distributor has the right to redistribute them.
