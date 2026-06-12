# ARServer Roadmap

ARServer is moving toward a complete native IEC 61850 gateway workflow for practical HMI, SCADA, Modbus TCP, and MQTT use.

## Design direction

The product direction is simple:

```text
IED / Relay → native IEC 61850 MMS client → runtime cache → Modbus TCP / MQTT
```

The HMI or SCADA system should not poll the relay directly for every screen refresh. ARServer acts as a gateway layer with selected signals, clear mapping, cached values, device timestamp, quality, and output diagnostics.

## Implemented milestones

### N1 — Native transport foundation

- TCP port 102 connection.
- TPKT frame handling.
- COTP connection request/confirm.
- Runtime status separated between transport and application association.

### N2 — ACSE/MMS association

- ISO session and presentation handshake.
- ACSE association request.
- MMS initiate request/response probe.
- Diagnostics for association state.

### N3/N4 — Confirmed-Read

- Single-variable MMS Confirmed-Read.
- IEC object to MMS domain/item mapping.
- response invoke ID validation.
- first-pass decoder for status, Boolean, integer, float, string, quality, and timestamp-like values.

### N7 — Correct Presentation P-DATA envelope

- Confirmed-Read wrapped in Presentation P-DATA.
- Response unwrap before MMS decode.
- Field-proven read path for CB position values.

### N8 — Native IP discovery

- Online MMS discovery by IP.
- Domain browse.
- domain variable browse.
- MMS name to IEC object candidate mapping.
- SCADA-friendly candidate recommendation.

### N9 — Quality and timestamp sidecar

- Companion `q` and `t` reads when available.
- Runtime snapshot carries local timestamp and device timestamp separately.
- MQTT payload includes value, quality, local timestamp, and device timestamp.

### N10 — Probe before runtime commit

- Wizard-level probe selected signal.
- Probe validates value and attempts companion quality/timestamp reads.
- Runtime grid arranged as `IEC Object | Value | Timestamp | Quality | Type`.

### N11R — Report plan in Edit IED Wizard

- Native IP discovery now detects online DataSet and Report Control Block candidates without probing RCB attributes automatically.
- Signal discovery/polling is independent from reporting; RCB failures cannot break tag discovery.
- Edit IED Wizard now includes a Report Plan step for RCB selection, DataSet selection, and safe planning metadata.
- Read-only RCB attribute probe is explicit: the user selects one RCB, then ARServer reads DatSet/RptID/ConfRev/IntgPd/RptEna when the IED allows it.
- Runtime remains safe: reporting is selected/planned only, while MMS polling remains the active data path until explicit report activation is implemented.

## Next milestones

### N12 — DataSet directory engine

- Read DataSet member directory for a selected DataSet/RCB.
- Map each member to IEC object reference, functional constraint, and selected runtime signal coverage.
- Show Covered / Partial / Not selected / Unknown coverage in Report Plan step.

### N13 — Discovery hardening

- Better filtering for common LN classes.
- Better handling of vendor-specific MMS names.
- More deterministic type inference.
- Discovery report export.

### N14 — Multi-point read optimization

- Group reads by relay and functional constraint.
- Reduce request count for large mappings.
- Maintain fast lane for CB/status/protection points.
- Add timeout and retry profiles per IED.

### N15 — Report verification

- Online DataSet browse.
- Online ReportControl browse.
- Compare selected signals against DataSet members.
- Show RCB ownership/readiness before activation.

### N16 — Report activation with polling fallback

- Enable report-preferred runtime mode.
- Decode InformationReport values.
- Preserve polling fallback for stale or failed reports.
- Show report state in diagnostics.

### N17 — Mapping/report documentation

- Export Modbus register map.
- Export selected IEC object list.
- Export validation summary.
- Create FAT-friendly evidence report.

## Product principles

- Read-only operation first.
- Never invent field values.
- Keep device timestamp separate from local PC timestamp.
- Make Modbus and MQTT mapping explicit.
- Prefer SCL when engineering files are available.
- Use IP discovery for quick online setup.
- Keep diagnostics useful for field troubleshooting.
- Keep the repository Apache-2.0 and self-contained.
