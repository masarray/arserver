# AGENTS.md — ARServer Production Engineering Contract

These rules apply to every AI/code agent working in this repository. ARServer is a native Windows IEC 61850 MMS → runtime cache → Modbus TCP / MQTT engineering gateway. Protocol correctness, deterministic published values, failure containment, UI responsiveness, bounded memory, and long-running runtime stability are product requirements from the first implementation.

## 1. Prime directive

Do not begin with a deliberately naive, disposable, prototype-only implementation when the production architecture is knowable.

Choose the smallest production-quality solution that satisfies the requirement without speculative complexity.

Priority order:
1. engineering/protocol/data correctness;
2. deterministic gateway output and failure containment;
3. regression compatibility;
4. runtime/UI responsiveness;
5. bounded CPU, memory, queue depth, and network work;
6. maintainability and testability.

Never make a WPF screen look correct while runtime cache, MMS state, Modbus registers, MQTT topics, quality, or timestamps are wrong.

## 2. Mandatory engineering loop

For non-trivial work:

RECONNAISSANCE -> REPRODUCE/BASELINE -> ROOT CAUSE -> INVARIANTS -> ARCHITECTURE IMPACT -> IMPLEMENT -> REGRESSION TEST -> FAILURE TEST -> LOAD/PERFORMANCE CHECK -> RELEASE BUILD -> GATEWAY WORKFLOW VALIDATION

Before editing:
- trace the complete path from IEC 61850 source/SCL -> discovery/read/report/polling -> runtime cache -> Modbus/MQTT -> UI/diagnostics;
- identify the authoritative owner of IED/session/tag/runtime state;
- identify public/persisted mapping and project-file contracts;
- locate existing tests and lifecycle/shutdown paths;
- define behavior that must not regress;
- determine root cause before introducing retry loops, timers, caches, duplicate services, or UI workarounds.

If an attempted fix fails, stop and re-audit assumptions. Do not stack workaround on workaround.

Three symptom patches in the same subsystem are a circuit breaker: re-audit ownership, state flow, and architecture before a fourth patch.

## 3. Architecture boundaries

Preferred direction:

WPF / presentation
-> application/session orchestration
-> authoritative runtime cache + mapping model
-> IEC 61850 / Modbus / MQTT service adapters
-> sockets/files/OS infrastructure

Rules:
- WPF controls must not own protocol state machines, socket loops, Modbus register truth, or MQTT publish truth;
- IEC 61850 decoding/state belongs outside UI code;
- Modbus and MQTT consume validated runtime-cache snapshots; they do not create independent truth;
- maintain one authoritative value/quality/device-timestamp state per mapped point;
- SCL/import/discovery enriches planning but cannot silently overwrite live truth;
- avoid global mutable state and duplicate caches unless ownership/invalidation is explicit;
- do not create a second protocol implementation merely to bypass a defect.

## 4. Result-oriented failure handling

Exceptions must not be normal control flow for expected or recoverable protocol/runtime conditions.

Expected conditions such as malformed/unsupported data, unavailable optional attribute, timeout, disconnect, stale value, negative service response, invalid mapping, unavailable MQTT broker, Modbus client disconnect, or project-file validation failure should use explicit typed Result/Try/status contracts where practical.

For C# prefer:
- nullable reference types and explicit guards;
- `TryParse` / `TryXxx` patterns for routine parsing/validation;
- coherent typed result/error records for operations whose failure detail matters;
- cancellation tokens and explicit timeout outcomes.

Do not create a different ad-hoc `Result<T>` shape in every service. Keep a stable error taxonomy per domain/boundary.

Socket, XML, filesystem, .NET, MQTT, or third-party exceptions may still occur. Catch them at the nearest meaningful infrastructure/application boundary and convert them to structured failures.

Do not scatter broad `try/catch` inside polling/decoding loops and do not silently swallow errors.

## 5. Defensive protocol/data handling

Treat every IED response, SCL/XML file, network frame, project file, user mapping, Modbus request, MQTT event, and persisted setting as fallible.

Validate before use:
- nullability/missing fields;
- lengths, indexes, counts, array/buffer bounds;
- BER/MMS lengths and type/tag expectations;
- enum/range values;
- numeric overflow and finite floating-point values;
- timestamps and quality availability;
- schema/version compatibility;
- timeout/cancellation/disconnect;
- oversized or malformed external data.

Missing device timestamp or quality remains blank/unknown/explicitly unavailable. Never invent engineering values.

One malformed point or remote response must not crash the entire gateway when isolation is technically possible.

## 6. Runtime cache invariants

The runtime cache is the shared authoritative bridge between IEC 61850 input and output adapters.

Each point must preserve, where available:
- IEC object identity;
- decoded value/type;
- quality;
- device timestamp;
- local receipt/update timestamp;
- stale/valid state;
- sequence/version needed for consistent publication.

Modbus/MQTT/UI consumers must observe coherent snapshots. Avoid partially updated multi-field state.

Do not allow MQTT or Modbus output code to mutate IEC source truth.

Mapping changes should be prepared/validated before becoming active. Failed candidate mapping must retain last-known-good runtime configuration where practical.

## 7. Zero UI blocking

The WPF dispatcher exists for rendering and interaction.

Never perform synchronous long-running:
- MMS association/discovery/polling;
- SCL/XML import;
- DNS/network connection;
- MQTT reconnect/publish batches;
- project load/save of large data;
- bulk mapping/probe work;
- report/export work

on the UI thread.

For 60 Hz UI, ~16.7 ms is the total frame budget, not permission for each operation to consume a frame.

Use async I/O for I/O-bound operations and bounded background execution for CPU-heavy work. Marshal only minimal validated state to UI.

Do not use arbitrary `Task.Delay` to conceal a race.

## 8. Polling, publishing, batching, and backpressure

High-frequency IED updates must not cause one WPF render, one log line, and multiple independent allocations per sample/update.

Use bounded queues/channels, coalescing, batching, latest-value semantics, or backpressure according to data semantics.

Rules:
- no unbounded queue for IEC updates, MQTT work, Modbus events, UI updates, or diagnostics;
- do not spawn one Task/thread per point update;
- UI presentation frequency is independent from acquisition frequency;
- MQTT publish policy is explicit and bounded;
- Modbus reads should consume coherent snapshots efficiently rather than rebuild maps per request;
- lossless engineering events retain ordering when required; presentation-only intermediate values may be coalesced.

## 9. Asynchronous internal diagnostics

Critical/high-rate paths must not synchronously format/write expensive diagnostics.

Emit compact structured events/counters to a bounded asynchronous diagnostic channel. Human-readable formatting, persistence, UI rows, and export occur on a background consumer.

The diagnostic pipeline must be:
- bounded;
- non-blocking to critical protocol/runtime work;
- deduplicated/rate-limited/aggregated during repeated failures;
- observational only.

A repeated timeout/disconnect storm should become one aggregated condition with count/first/last occurrence, not thousands of UI/log operations.

A full, slow, or failed diagnostic sink must never block MMS polling, Modbus response, MQTT state progression, or application shutdown.

## 10. Protocol/session/reconnect discipline

MMS association, polling/reporting, MQTT connection, and any reconnect-capable transport must use explicit states and bounded recovery.

Every retry policy defines:
- timeout;
- backoff/cadence;
- maximum or bounded steady-state behavior;
- cancellation/shutdown behavior;
- terminal/degraded state.

Unexpected/late data must not corrupt a newer session.

Do not repair a lifecycle race with sleeps.

## 11. Modbus and MQTT output correctness

Published output must be deterministic and derived from validated cache state.

For Modbus:
- validate register/address ranges and data width/endianness contracts;
- avoid overlapping mappings unless explicitly supported;
- define behavior for unavailable/stale/invalid source values;
- do not block source polling on slow clients.

For MQTT:
- bound connection/publish work and payload size;
- validate topic construction;
- avoid publishing secrets/private project data inadvertently;
- retain/QoS behavior must remain explicit;
- broker outage must not grow an unbounded pending queue.

## 12. Memory and lifecycle

Long-running gateway stability matters more than short benchmark bursts.

Every socket, stream, timer, worker, event subscription, cancellation source, MQTT client, Modbus listener/client, IED session, and file handle must have an explicit owner and shutdown path.

Repeated connect/disconnect/project reload must not monotonically grow resources or duplicate subscriptions.

Prefer bounded snapshots/views over duplicating entire models for each output adapter.

Use pooling only when profiling demonstrates meaningful allocation pressure and the lifetime model remains clear.

## 13. Performance contract

For performance-sensitive changes measure relevant signals where practical:
- startup-to-interactive time;
- IED association/discovery latency;
- selected-point polling throughput/latency;
- runtime queue depth;
- UI frame/jank behavior;
- MQTT pending/publish rate;
- Modbus request latency;
- CPU and allocation rate;
- working set over long-running sessions;
- reconnect/recovery latency.

For the same qualified scenario, >10% regression in a relevant metric requires explicit explanation/review. This is a visibility threshold, not an automatic rejection when correctness/capability justifies the trade-off.

Do not claim optimization without evidence.

## 14. Regression protection

Every bug fix should protect the exact failure mode with a deterministic test/check where practical.

Important scenarios include:
- malformed MMS/SCL;
- IED disconnect/reconnect;
- stale/quality/timestamp propagation;
- mapping reload;
- MQTT broker loss;
- Modbus client churn;
- cancellation/shutdown;
- duplicate callbacks/subscriptions;
- long-running queue/resource stability.

Do not change project formats, mapping semantics, Modbus address behavior, MQTT topic/payload contracts, or default runtime behavior without compatibility analysis.

## 15. Definition of done

A task is not complete because it compiles.

Validate as applicable:
RELEASE BUILD
+ STATIC/UNIT TESTS
+ REGRESSION TEST
+ MALFORMED/FAILURE-PATH TESTS
+ IEC 61850 SIMULATOR/LOOPBACK OR AUTHORIZED LAB CHECK
+ MODBUS CHECK
+ MQTT CHECK
+ UI RESPONSIVENESS CHECK
+ QUEUE/RESOURCE/PERFORMANCE CHECK
+ STARTUP/SHUTDOWN/RECONNECT WORKFLOW

Never claim a validation step was run when it was not.

## 16. Completion report

Report:
- Changed;
- Root cause;
- architecture/state ownership decision;
- Result/error contract affected;
- invariants preserved;
- regression protection;
- measured performance impact or why not performance-sensitive;
- exact validation executed;
- genuine remaining limitations.

## Final rule

Think like the engineer responsible for a gateway running continuously between protection IEDs and SCADA-facing consumers for years, not like a prototype generator making one demo update successfully.

Keep one source of truth. Keep queues bounded. Keep UI and outputs decoupled from acquisition. Make failures explicit. Preserve engineering timestamps/quality. Fix root causes. Prevent regressions.