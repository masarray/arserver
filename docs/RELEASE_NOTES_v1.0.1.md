# ARServer v1.0.1 Public Beta

This release provides a cleaner Windows portable package for users who want to try ARServer without Visual Studio. The application binary is now packaged as a single `ArServer.exe`, while the ZIP still includes quick-start notes, license files, and validation documents.

## What is included

- Single-executable Windows portable app package.
- IEC 61850 MMS workspace for selecting and validating SCADA-ready signals.
- Modbus TCP mapping for HMI and SCADA integration.
- MQTT topic publishing for broker-based dashboards.
- Fast CB acquisition mode for improved response when monitoring breaker/status changes.
- User-facing quick start, troubleshooting, validation, and release packaging documentation.

## How to try it

1. Download `ARServer-v1.0.1-public-beta-win-x64-portable.zip`.
2. Extract it to a writable Windows folder.
3. Run `ArServer.exe`.
4. Use mock mode first to explore the workflow without a real IED.
5. For real IED testing, add the required IEC 61850 MMS runtime components beside `ArServer.exe`, then start the app.

## Validation reminder

Before using ARServer in a field-connected environment, validate signal references, quality behavior, stale handling, Modbus address mapping, MQTT topic naming, and network exposure.
