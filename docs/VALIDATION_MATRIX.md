# Validation Matrix

Use this matrix when evaluating ARServer on a lab bench, FAT bench, or simulated environment.

| Area | What to validate | Expected result |
|---|---|---|
| Application start | Portable package opens on Windows | Main workspace loads without Visual Studio |
| Mock mode | Add sample IED and select signals | Values update and can be mapped |
| IED connection | Connect to test relay on MMS port | Connection status and discovery are visible |
| SCL import | Import CID/SCD/SCL file | SCADA-ready signals appear in wizard |
| IEC Reference visibility | Open signal selection wizard | IEC Reference is visible near the Signal column |
| Modbus map | Bind selected values to addresses | No overlapping addresses for active points |
| Modbus read | Read from external Modbus master | Values match ARServer runtime cache |
| Float32 handling | Read analog values from HMI | Word order and scale are correct |
| MQTT publish | Subscribe to configured topics | Value, quality, status, and state topics update |
| Fast CB | Enable Fast CB with few status points | Status points are prioritized before analog points |
| Stale handling | Disconnect IED during runtime | Quality/stale indication changes visibly |
| Restart behavior | Save project, close, reopen | IED workspace and mappings are preserved |
| Safety | Attempt Modbus write | Write is rejected by design |

## Bench recommendation

Start with a small selected set:

- one breaker position;
- one trip/start flag;
- one analog measurement;
- one quality/status point.

Then increase point count gradually while watching refresh behavior, stale indication, and CPU/network load.
