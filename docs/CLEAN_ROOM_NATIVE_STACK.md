# Native Clean-Room IEC 61850 Stack Notes

This document records the implementation boundary for ARServer's native IEC 61850 MMS work.

## Rule

The native stack must not copy, translate, port, or derive implementation details from GPL libraries. It is written as an independent implementation with explicit layers and testable protocol boundaries.

## Allowed sources

- Public protocol behavior observed from user-owned devices/test servers.
- User-owned SCL/CID/SCD/ICD files.
- Standards and documentation that the maintainer is legally allowed to use.
- Fresh code written for this repository.

## Disallowed sources

- libiec61850 source code.
- Decompiled libiec61850 binaries or wrappers.
- GPL code translated into C#.
- Function-by-function clones of GPL internal design.

## Layering

```text
ARServer Runtime / Cache
  -> IIec61850Client
  -> NativeCleanRoomIec61850Client
  -> IEC 61850 object mapper
  -> MMS service encoder/decoder
  -> ACSE / Presentation
  -> COTP
  -> TPKT
  -> TCP port 102
```

## Current implementation status

- Native client boundary exists.
- TPKT framing foundation exists.
- COTP connection request/confirm exists.
- ACSE/MMS initiate probe exists using an isolated interoperability profile.
- BER reader/writer foundation exists.
- MMS object reference normalization exists.
- Report planner exists at runtime level.
- Native Confirmed-Read interoperability slice is enabled for SCL-selected points. Native RCB activation is not enabled yet.

## Runtime safety

Native MMS read must not return fake values. A failed or unsupported native read returns null so runtime marks the binding Bad/Not readable.

## Phase N1 implementation note

The native stack now performs a clean-room COTP transport handshake. A native IEC 61850 session is considered connected only after the relay accepts the COTP connection confirm. This is intentionally stricter than a plain TCP socket test because a device can accept TCP/102 but still reject ISO-on-TCP/COTP negotiation.

Current boundary:

- implemented: TCP, TPKT framing, COTP Connection Request, COTP Connection Confirm validation, ACSE/MMS initiate probe, response hex preview, IEC object reference normalization into MMS domain/item form.
- pending: full ASN.1 AARE parser, MMS Confirmed-Read encoder/decoder, RCB activation, report decoding.

Runtime safety rule remains unchanged: native code must not synthesize values. Until MMS Confirmed-Read is implemented, selected SCL bindings remain unavailable rather than being published as mock/live data.

### Phase N1.2 — Honest native runtime state
- Native clean-room path now distinguishes transport readiness from MMS application readiness.
- The relay card no longer shows `MMS stream active` for null/native-pending reads.
- SCL runtime can still be started for transport verification, but live values remain Bad until ACSE/MMS Initiate and Confirmed-Read are implemented.



### Phase N2 association probe note

The native stack now attempts the first application-layer association after COTP. This is still treated as a probe, not as a complete MMS client. If the relay accepts the initiate exchange, ARServer shows `MMS Associated` and each selected SCL tag remains `Confirmed-Read pending` until the native read encoder/decoder is implemented.

If the relay rejects the association, the UI keeps the endpoint at `Transport Ready` and logs the response preview. This makes field testing useful without publishing fake values or silently falling back to GPL runtime code.

### Phase N3 — First native MMS Confirmed-Read slice

The native stack now contains an initial single-variable MMS Confirmed-Read encoder/decoder for SCL-selected points.

Implemented boundary:

- MMS domain-specific object name builder from IEC 61850 reference + FC.
- Single-variable Confirmed-Read request encoder.
- Post-association Presentation Data wrapper for read requests.
- Response decoder for common MMS data encodings:
  - boolean
  - signed/unsigned integer and enum-like values
  - bit-string, including a first-pass Dbpos display mapping
  - MMS floating-point single/double
  - visible string
  - time values as safe hex previews
- Runtime still returns `null` on unsupported/failed read so the binding remains Bad rather than fake-live.

This is an interoperability slice, not a final universal MMS decoder. Field captures from real IEDs should be used to tune presentation wrapping, response parsing, and data attribute mappings per vendor.

### Phase N4 — Adaptive native read and segmented COTP receive

The native stack now has a stronger field-test loop for real IEDs:

- COTP DT receive reassembles segmented responses until EOT.
- MMS Confirmed-Read validates Confirmed-Response PDU shape and invokeID.
- Each read records an attempt summary so diagnostics can show which object name profile was tried.
- Primary object mapping remains IEC 61850 FC-aware, for example `Q0CSWI1$ST$Pos$stVal`.
- A conservative no-FC alternate can be attempted only after an object/access-style failure, for example `Q0CSWI1$Pos$stVal`.
- Decoder can now format quality bit-string and UTC time values.

Safety rule remains unchanged: unsupported reads return null and keep runtime quality Bad. The native stack does not synthesize values and does not silently call the external GPL runtime path.

## Phase N5 association guardrail

Native clear-room connection readiness is now defined as successful ACSE/MMS association.
A TCP 102 socket plus COTP connection confirm is not enough to publish live data.

Association profiles are explicit and auditable:
- `BalancedApTitle`: populated AP-title / AE-qualifier profile.
- `LegacyMinimal`: fallback compact AARQ profile.

The stack treats ISO Session Abort `0x19` as a real association failure. This avoids a dangerous false-positive where an abort frame contains bytes that look like Presentation/ACSE markers.
