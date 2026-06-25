# Runtime Reference Echo Fix

This patch prevents IEC 61850 report object names such as `GD7700LD01/GGIO1$ST$Ind7$stVal` from being treated as process values.

## Fix
- `BridgeRuntime.ApplyReportUpdate` now detects reference-only report payloads.
- Reference echoes are ignored for Modbus/MQTT writes.
- The previous good value is preserved; otherwise the cell stays `Pending read`.
- MMS polling fallback supplies the real value (`True`, `False`, numeric, enum, etc.).
- Search boxes now have visible placeholders and the clear button only appears when text is entered.
