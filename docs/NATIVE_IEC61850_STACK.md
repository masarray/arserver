# Native IEC 61850 Stack Notes

ARServer includes a native IEC 61850 MMS client implementation for its gateway workflow.

## Layer model

```text
TCP socket
  ↓
TPKT
  ↓
COTP
  ↓
ISO Session / Presentation
  ↓
ACSE association
  ↓
MMS Initiate
  ↓
MMS directory browse and Confirmed-Read
  ↓
IEC object mapper
  ↓
Runtime cache
  ↓
Modbus TCP / MQTT
```

## Current native services

- Connect to IED MMS endpoint.
- Establish ACSE/MMS association.
- Browse online model by IP.
- Import SCL/CID/SCD/ICD engineering files.
- Map MMS names into IEC object candidates.
- Read selected values.
- Read companion quality and timestamp when available.
- Feed runtime cache for Modbus TCP and MQTT.

## Runtime safety

ARServer does not publish invented IEC values. If a selected object cannot be read, the runtime marks the point unavailable or Bad and writes diagnostics.

Device timestamp comes from the IED `t` attribute when readable. Local timestamp remains separate and is used only to show when ARServer updated its cache.

## Recommended test sequence

1. Connect by IP.
2. Confirm ACSE/MMS associated.
3. Confirm discovery returns domains and candidates.
4. Probe one CB position object.
5. Probe a Boolean/alarm point.
6. Probe one measurement point.
7. Save to runtime.
8. Start runtime.
9. Confirm live value, timestamp, quality, and type.
10. Confirm Modbus TCP read from an HMI client.
11. Confirm MQTT publish when enabled.

## Reporting direction

Report support should be added after online DataSet and ReportControl verification is mature. The recommended runtime mode is report-preferred with polling fallback, not report-only.
