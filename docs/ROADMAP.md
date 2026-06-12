# ARServer Roadmap

ARServer is an open-source Windows engineering gateway for IEC 61850 MMS, Modbus TCP, and MQTT workflows. The long-term direction is a user-oriented substation gateway: easy enough for HMI/FUXA/SCADA integration, but strict enough for relay testing, FAT/SAT, and protocol evidence.

## Product direction after HMI/SCADA/gateway review

ARServer should behave like a proper SCADA acquisition gateway, not like a screen that directly reads a relay every time an operator opens a page.

Reference concepts studied:

- FUXA: modern web HMI/SCADA connects industrial devices through standard protocols such as Modbus, OPC UA, MQTT, and Siemens S7. ARServer should therefore expose clean Modbus/MQTT outputs for HMI tools instead of forcing HMI users to understand IEC 61850.
- Rapid SCADA: acquisition is built around communication lines, devices, and channels/tags. Communication lines work independently and in parallel. ARServer should keep per-IED client sessions isolated and publish stable cached tags.
- Apache PLC4X: the strongest architectural lesson is a shared API over multiple industrial protocols. ARServer should keep IEC 61850 acquisition behind interfaces and keep Modbus/MQTT output independent from the source engine.
- Eclipse Milo / open62541: mature protocol stacks separate low-level wire protocol, stack/session handling, SDK/object model, and application services. ARServer native IEC 61850 must follow the same layering instead of mixing ASN.1/MMS code into UI or runtime code.
- Node-RED / edge gateway workflows: practical gateways read raw industrial data, transform it into human-readable tags, then publish to MQTT/UNS or Modbus. ARServer should keep transformation, scaling, quality, and stale detection explicit.
- Mango Automation style data-source/data-point model: device/source health and point reliability are first-class runtime states. ARServer should expose Good/Bad/Stale/Pending and avoid fake fallback values.

## Architectural principles

1. **HMI reads from ARServer cache, never directly from the relay.**
   Modbus TCP and MQTT outputs are fed from an internal runtime cache. A FUXA/SCADA read must not trigger a live MMS read.

2. **IEC 61850 is a driver, not the application.**
   UI, project storage, mapping, Modbus, MQTT, diagnostics, and reports must not depend on libiec61850 internals.

3. **SCL-first native clean-room path.**
   IP discovery may temporarily use user-supplied libiec61850 runtime. The clean-room path starts with Open SCL/CID/SCD -> select signal -> start session -> polling/report planner.

4. **Report preferred, polling fallback.**
   RCB/reporting is the correct event-grade path, but polling must stay as integrity/watchdog fallback until native report activation and decoding are proven against real relays.

5. **No silent mock fallback.**
   If native/external MMS cannot read a point, mark it Bad/Not readable. Do not publish simulated values in real runtime.

6. **Protocol forensic visibility.**
   Runtime should explain whether a point is updated by polling, report fallback polling, report live, stale, not readable, or disconnected.

## Native clean-room roadmap

### N0 — Clear-room foundation and report-aware runtime planner — started

Status: **started in this branch**

Implemented/started:

- `NativeCleanRoomIec61850Client` added as the GPL-free native client boundary.
- `Protocol/Osi/TpktClient` added for TCP/TPKT framing foundation.
- `Protocol/Osi/CotpConnectRequest` placeholder added for the next OSI handshake step.
- `Protocol/Asn1/BerReader` and `Protocol/Asn1/BerWriter` added for BER TLV parsing/writing foundation.
- `Protocol/Mms/MmsObjectReference` added to normalize IEC object references toward MMS variable names.
- `ReportRuntimePlanner` added to group selected SCL bindings by RCB/DataSet.
- `ReportControlPlan` model added.
- `BridgeRuntime` now builds and logs report plans before acquisition starts.
- Report-capable points are marked as `Report planned / polling fallback` and successful polling shows `Report fallback polling/Live`.

Purpose:

- Keep existing runtime stable.
- Make SCL report metadata operationally visible.
- Prepare native MMS read and native RCB phases without copying GPL code.

Limitations:

- Native MMS confirmed-read encoder/decoder is not enabled yet.
- Native online IP discovery is intentionally not part of N0.
- Native RCB activation is planned but not yet active.

### N1 — Native OSI association — in progress

Status: **TCP/TPKT/COTP implemented; ACSE/MMS initiate probe added in this branch**

Implemented/started:

- TCP port 102 connect.
- TPKT send/receive.
- COTP connection request/confirm.
- ISO Session + Presentation + ACSE AARQ + MMS Initiate-Request interoperability profile.
- ACSE/MMS initiate response probe with response hex preview in diagnostics.
- Runtime state now distinguishes:
  - `Transport Ready`
  - `MMS Associated`
  - `MMS initiate failed`
  - `Confirmed-Read pending`

Acceptance target:

- Native client reaches an associated MMS session against at least one test server/IED.
- Failure log shows exact layer: TCP, TPKT, COTP, ACSE, Presentation, or MMS Initiate.
- No libiec61850 DLL is loaded or referenced by the native SCL runtime path.

Current limitation:

- MMS Confirmed-Read is still not implemented. If association succeeds, values are intentionally shown as `Native MMS associated / read pending`, not Good/live.

### N2 — SCL-driven native MMS polling

Goal:

- Read selected SCL signal references using MMS confirmed-read.
- Decode basic IEC 61850 value types:
  - Boolean
  - Integer
  - Dbpos / DPC
  - Enum
  - Float32
  - Quality bit-string
  - Timestamp

Acceptance:

- Open SCL -> select signals -> start runtime -> values update through native clean-room polling.
- Bad/not-readable/stale status is explicit.
- Modbus/MQTT continue to publish from cache only.

### N3 — Report Control Block verification

Goal:

- Read RCB attributes before activation:
  - `RptID`
  - `DatSet`
  - `ConfRev`
  - `OptFlds`
  - `TrgOps`
  - `BufTm`
  - `IntgPd`
  - `RptEna`
  - `Resv` / `ResvTms`
  - `EntryID`
- Compare online RCB `DatSet` with imported SCL DataSet.
- Detect unavailable/already-owned RCB.

Acceptance:

- Runtime can show `RCB verifying`, `RCB available`, `RCB owned by another client`, `DataSet mismatch`, or `ConfRev mismatch`.
- Polling fallback remains active.

### N4 — Native report activation and event lane

Goal:

- Reserve RCB when needed.
- Apply safe optional fields/trigger options when allowed.
- Enable `RptEna`.
- Decode incoming reports.
- Update the same runtime cache used by polling.
- Keep polling as watchdog/integrity fallback.

Acceptance:

- Position/protection points can update through reports.
- Runtime distinguishes `Report live`, `Report stale`, `Polling watchdog`, and `Polling fallback`.
- On stop, runtime disables `RptEna` and releases reservation safely.

### N5 — Native online discovery

Goal:

- Replace IP discovery dependency progressively:
  - server directory
  - logical device directory
  - logical node directory
  - data directory
  - variable access attributes

Acceptance:

- IP connect/discovery can run without libiec61850 for supported devices.
- Discovery results still use the same `SignalDefinition` and binding flow.

### N6 — Engineering-grade validation and release readiness

Goal:

- Protocol trace summary.
- Per-IED runtime stats.
- Acquisition latency and failure counters.
- Mapping validation report.
- Test matrix with multiple vendor SCL files and real/simulated IEDs.

Acceptance:

- Release notes clearly state supported native features.
- External libiec61850 path is optional compatibility mode only.
- Clean-room implementation notes remain documented.

## Current focus

- Keep external-runtime discovery available for field users.
- Build native clean-room SCL polling path without breaking existing Modbus/MQTT runtime.
- Make reporting metadata useful before native RCB activation.
- Preserve professional HMI/SCADA behavior: cached output, quality state, stale state, and per-IED isolation.

## Non-goals

- ARServer is not a protection relay control system.
- ARServer is not a replacement for engineering validation, cybersecurity hardening, or redundant station-level architecture.
- ARServer does not turn polling into deterministic event capture. Event-grade workflows should use reporting/RCB where available.
- ARServer will not copy, translate, or port GPL implementation details into the native clean-room stack.

## Phase N1 - Native clean-room transport handshake

Status: started in this branch.

### What changed

- Added native COTP connection handling on top of the existing TPKT client.
- Native clean-room sessions now require TCP + TPKT + COTP Connection Confirm before `IsConnected` becomes true.
- SCL-imported runtime plans are routed to the native clean-room client path, while IP-only discovery remains on the external runtime/mock path until native online browse is implemented.
- Native read calls now normalize IEC 61850 object references into MMS domain/item form and report a precise blocked state instead of silently behaving like a mock source.
- Runtime diagnostics now expose native preflight errors and native "transport ready, MMS not enabled yet" messages.

### Engineering meaning

This phase does not claim full MMS client support yet. It moves the clean-room stack from a TCP socket placeholder to a real IEC 61850 transport preflight:

```text
SCL import -> selected signal -> runtime start
  -> native TCP connect
  -> TPKT frame exchange
  -> COTP connection request / connection confirm
  -> ACSE + MMS Initiate pending
  -> MMS Confirmed-Read pending
  -> polling cache remains protected from fake values
```

### Next implementation slice

Phase N2 should add the first ACSE AARQ/AARE and Presentation Context negotiation skeleton, still with strict no-fake-value behavior. After ACSE is accepted, add MMS Initiate. Only after MMS Initiate succeeds should Confirmed-Read be enabled for selected SCL points.

### Phase N1.2 — Honest native runtime state
- Native clean-room path now distinguishes transport readiness from MMS application readiness.
- The relay card no longer shows `MMS stream active` for null/native-pending reads.
- SCL runtime can still be started for transport verification, but live values remain Bad until ACSE/MMS Initiate and Confirmed-Read are implemented.


### Phase N3 — First native MMS Confirmed-Read slice

- Adds initial single-variable Confirmed-Read request/response path for SCL-selected points.
- Keeps runtime safety: failed/unsupported read remains Bad/null, never fake-live.
- Focus field validation on simple status data first: `Pos.stVal`, `Beh.stVal`, `Mod.stVal`, then quality/time.

### Phase N4 — Adaptive native read + segmented COTP receive

Status: implemented in this branch.

- COTP receive now reassembles segmented DT responses until EOT.
- Native read now records per-attempt diagnostics.
- Confirmed-Read now validates response PDU shape and invokeID correlation.
- FC-aware object names remain primary; a conservative no-FC alternate can be attempted after access/object-style failure.
- Quality bit-string and UTC time values now get readable formatting.
- Runtime still publishes no fake values and does not silently fall back to external GPL runtime.

## Phase N5 - Native ACSE Association Profiler

The native clean-room path now separates transport readiness from MMS association readiness.

Implemented:
- Adaptive ACSE association profiles: `BalancedApTitle` first, `LegacyMinimal` fallback.
- ISO Session Abort `0x19` is now correctly treated as failure.
- Native `IsConnected` means ACSE/MMS associated, not only TCP/COTP connected.
- Field diagnostics include profile-by-profile association attempt summaries.
- Runtime blocks Modbus/MQTT publication when the native IEC 61850 path is not MMS-associated.

Next:
- Import SCL OSI selector parameters.
- Convert static association profiles into configurable clean-room BER encoders.
- Continue Confirmed-Read decoder validation with real relay traces.

## Phase N6 - Native MMS Read Resilience and Payload Profiler

Phase N6 improves the first native Confirmed-Read slice after field feedback from an IED that accepted ACSE/MMS association but closed TCP immediately after the first read attempt.

Implemented guardrails:

- MMS association is still required before runtime reads.
- Confirmed-Read now records the exact request payload profile and hex preview used for each attempt.
- A peer-closed TCP read fault is treated as a protocol/profile failure, not as a live value.
- The native session can re-establish ACSE/MMS between diagnostic read profiles so one rejected payload does not poison the whole runtime session.
- Read payload profiles are explicit and auditable:
  - PresentationDataValues
  - PresentationDataValuesWithSpecificationResult
  - SessionDataOnly
  - RawMmsPdu
- No fake values and no silent libiec61850 fallback are introduced.

Next focus after N6:

1. Capture which payload profile the target IED accepts or rejects.
2. If every payload profile closes the TCP session, tune the ISO Presentation P-DATA wrapper.
3. If the IED returns AccessResult.failure, tune IEC object-name mapping from SCL/FCDA.
4. Once one `stVal` reads successfully, expand decoder coverage for `q`, `t`, analog values, and DataAttribute structures.

## Phase N8 — Native IP Discovery MVP

Status: implemented as the first clean-room online browse slice.

Native IP-only discovery now follows the same high-level service shape used by mature IEC 61850 clients: associate first, read the server directory/domain names, browse each MMS domain for named variables, map MMS names back into IEC 61850 object candidates, then let the user select the SCADA tags that will be routed to Modbus TCP/MQTT.

Implemented scope:

- MMS `GetNameList` request encoder for VMD/domain browse.
- MMS `GetNameList` response decoder with invokeID validation.
- Native domain browse: VMD-specific domain list.
- Native domain variable browse: domain-specific named variables.
- Optional domain variable-list browse foundation for future DataSet/RCB work.
- Smart MMS item mapping, for example `Q0XCBR1$ST$Pos` → `LD/Q0XCBR1.Pos.stVal [ST]`.
- Smart candidate generation for CB/DS position, protection general flags, and MMXU/MMXN cVal magnitudes.
- IP-only wizard now defaults to native clean-room discovery instead of requiring an external runtime.

Guardrails:

- No GPL runtime fallback is used when native mode is selected.
- Discovery candidates are not fake values; runtime polling still proves each selected tag by native Confirmed-Read.
- Open SCL remains the deterministic engineered workflow when CID/SCD is available.

Next hardening:

- Add GetVariableAccessAttributes/GetDataDirectory-style type expansion for structured objects.
- Probe-read selected discovery candidates before committing to runtime.
- Use discovered named variable lists as the base for RCB/DataSet/reporting.


## Phase N9 — Native value quality/timestamp sidecar hardening

Implemented:

- Runtime-side slow polling of IEC 61850 companion `q` and `t` attributes for selected ST/MX tags.
- Conservative inference from value object to companion object, e.g. `Pos.stVal` → `Pos.q`/`Pos.t` and `cVal.mag.f` → phase-level `q`/`t`.
- Cached sidecar quality/timestamp so value polling remains fast and stable.
- Occasional diagnostic logging only; missing `q`/`t` does not break value acquisition.

Next:

- Add discovery-time live validation so users can see which candidates read Good before committing to runtime.
- Expand native type discovery.
- Start RCB/DataSet/reporting planning on top of proven native discovery and read services.


## Phase N9.1 — Runtime Grid Quality/Timestamp Presentation

- Re-arranged the live IEC 61850 grid into an operator/debug friendly order: IEC Object, Value, Timestamp, Quality, Type.
- Added a dedicated `DeviceTimestamp` runtime field so IEC 61850 `t` sidecar values are not confused with the local PC update time.
- MQTT JSON now carries both local timestamp and device timestamp when available.
