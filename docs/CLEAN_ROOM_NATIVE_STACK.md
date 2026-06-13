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


## Phase N7 correction: MMS Read envelope

Field testing showed `PresentationDataValues => transport fault` after ACSE/MMS association. The root cause was not the IED IP, COTP, or SCL import. The native read packet was still too compact:

- the ISO Presentation P-DATA `fully-encoded-data` / PDV-list wrapper was missing; and
- the MMS Read-Request structure missed the `variableAccessSpecification [1]` layer and the `SEQUENCE OF VariableSpecification` wrapper used by strict IEC 61850 MMS servers.

Phase N7 rewrites the clean-room Confirmed-Read encoder to use this post-association shape:

```text
COTP DT user-data
  01 00                         session data transfer
  01 00                         presentation user-data selector
  61 ...                        fully-encoded-data
    30 ...                      PDV-list
      02 01 03                  presentation-context-id = 3
      A0 ...                    single-ASN1-type
        A0 ...                  MMS Confirmed-RequestPDU
          02 ...                invokeID
          A4 ...                Read-Request
            A1 ...              variableAccessSpecification
              A0 ...            listOfVariable
                30 ...          SEQUENCE OF VariableSpecification
                  A0 ...        name
                    A1 ...      ObjectName.domain-specific
                      1A ...    domainId
                      1A ...    itemId
```

The response decoder now also unwraps the same Presentation P-DATA wrapper before decoding the MMS Confirmed-Response. No GPL source code was copied; this phase is based on public protocol traces, public API behavior, and clean-room implementation.

## N8 clean-room discovery implementation notes

The native discovery phase uses public MMS service semantics rather than libiec61850 source code. The implemented browse path is intentionally narrow and auditable:

1. ACSE/MMS association using the existing native association profiler.
2. MMS `GetNameList` for VMD-specific domain names.
3. MMS `GetNameList` for domain-specific named variables.
4. Local mapping from MMS names to IEC 61850 object candidates.
5. Existing native Confirmed-Read is still responsible for proving live values.

Clean-room boundary:

- Do not port libiec61850 source files, generated ASN.1 files, private helper names, or internal discovery algorithms.
- Keep wire encoders/decoders small, readable, and traceable to public MMS/IEC 61850 service behavior.
- Treat native discovery as candidate generation, not as value truth. A tag becomes operational only after native read succeeds at runtime.


## N9 quality/timestamp sidecar policy

The native clean-room runtime now reads companion IEC 61850 quality (`q`) and timestamp (`t`) attributes as slow sidecar reads after a value read succeeds. This is intentionally implemented in the runtime layer, not as a hard requirement in the low-level MMS decoder, because IED models vary and not every discovered object candidate exposes the same companion shape.

Design rules:

- The selected value tag remains the source of Modbus/MQTT value truth.
- `q` and `t` are enrichment attributes only; failure to read them must not make an otherwise valid value disappear.
- `q`/`t` reads are throttled and cached per binding.
- The runtime never fabricates `Good`; it starts with successful-read `Good` and upgrades/replaces it with decoded IED quality when available.
- Companion reference inference is conservative and limited to common ST/MX shapes such as `Pos.stVal`, `Op.general`, and `cVal.mag.f`.

This phase prepares the cache model for future reporting, where value, quality, timestamp, sequence, and reason-for-inclusion will arrive together in `InformationReport` PDUs.

## N10 live validation boundary

The wizard probe-read feature is a runtime usability layer, not a shortcut around the clean-room protocol boundary. It uses the currently associated IEC client and the native Confirmed-Read path already implemented in the stack. Discovery candidates remain only candidates until a probe or runtime poll proves that the IED accepts the MMS object reference.

Probe-read policy:

- Read selected value object first.
- Attempt `q` and `t` only as optional companion reads.
- Do not fabricate value, quality, or timestamp.
- Preserve failed candidates as visible diagnostics so the operator can remove or keep them knowingly.

This prepares the application for reporting because the UI/cache model now explicitly separates value, device timestamp, local update time, and quality.
