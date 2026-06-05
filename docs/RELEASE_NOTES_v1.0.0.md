# ARServer v1.0.0 Public Beta

ARServer v1.0.0 public beta packages the project as a user-facing Windows desktop gateway for IEC 61850 MMS, Modbus TCP, and MQTT integration labs.

## Highlights

- Single-executable Windows portable release package for users who want to try ARServer without Visual Studio.
- IEC 61850 IED workspace with signal selection and visible IEC Reference column.
- Modbus TCP server mapping for HMI/SCADA polling.
- MQTT topic publishing for broker-based dashboards.
- Adjustable MMS polling target down to 10 ms for expert bench evaluation.
- Fast CB acquisition mode for breaker/status/Boolean points.
- Read-only Modbus safety behavior.
- Product landing page and GitHub-ready documentation.

## Try the release

1. Download `ARServer-v1.0.0-public-beta-win-x64-portable.zip`.
2. Extract the ZIP to a writable Windows folder.
3. Run `ArServer.exe`.
4. Use mock mode first, then add real IEC 61850 runtime components when evaluating with an IED.
5. Add an IED, select signals, validate the Modbus map, configure MQTT if needed, and start runtime.

## Validation notes

For fast breaker/status response evaluation, use a small selected point set and enable **Fast CB**. The polling interval is a scheduler target; actual response depends on the IED, network, selected point count, and runtime load.

## License

ARServer core is free and open source under Apache-2.0.
