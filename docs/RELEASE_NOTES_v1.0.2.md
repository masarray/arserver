# Release Notes

## ARServer v1.0.2-public-beta

- Smart IEC 61850 discovery now applies AVR-aware data object mapping instead of relying on broad heuristic leaf names.
- AVR tap changer status uses the BSC `valWTr.posVal` structure, with legacy `TapChg.stVal` references normalized for compatibility.
- AVR live measurements and settings are separated so setting objects such as `BndCtr`, `RefPF`, `BlkLV`, `LimLodA`, `LDCR`, and `LDCX` are not published as bad runtime measurement tags.
- Failed probe reads are automatically excluded from runtime selection and Modbus/MQTT binding generation.
- SCL/IID imports classify `setMag`, `setVal`, `mag.f`, `ang.f`, and tap changer structures more accurately for SCADA selection.

## Validation focus

- Add AVR IED by IP.
- Discover or import IID/SCL model.
- Probe recommended AVR signals.
- Confirm unreadable settings remain out of runtime publishing by default.
- Start runtime and verify live value, timestamp, quality, Modbus, and MQTT output.
