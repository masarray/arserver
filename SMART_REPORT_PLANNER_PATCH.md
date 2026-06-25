# Smart Auto Report Planner Patch

This patch changes ARServer from manual RCB/DataSet selection into a signal-centric IEC 61850 gateway workflow.

## Key changes

- The IED wizard now keeps the user focused on SCADA signals. `q`, `t`, `Health`, `Beh`, `Mod`, and report-control attributes are treated as sidecar/diagnostic attributes, not selectable SCADA points.
- ARServer preserves per-signal report hints discovered from the IED/SCL instead of forcing every selected signal through one visible RCB.
- Runtime planning is static-safe by default: no dynamic DataSet creation, no automatic RCB DataSet reassignment, and no engineering writes unless an advanced/dynamic mode explicitly requests it.
- Signals that are readable by MMS but not proven inside a static DataSet are marked as polling fallback instead of being forced into the wrong report lane.
- MMXU/GGIO logical-node fallback targets are generated per logical-node instance, so `MMXU1` through `MMXU24` remain separate signals instead of being deduplicated into the first MMXU.
- Binding status/read mode now records whether the signal is report-covered, report-candidate/static-verify, or MMS polling fallback.
- Right-clicking a signal row in the wizard opens Signal Properties with IEC reference, FC, type, q/t companion references, DataSet, RCB, runtime source, and reason.
- Bridge runtime no longer overwrites report-lane values as `Bad` just because a polling fallback read has not returned yet. It keeps cached report values and uses Pending/awaiting statuses where appropriate.

## Intended behavior for static-report-only IEDs

- If a selected signal is inside a discovered static DataSet and an RCB points to that DataSet, ARServer uses the report lane and keeps polling fallback.
- If a selected signal is visible/readable in the MMS data model but not included in any static DataSet, ARServer uses MMS polling fallback.
- Advanced manual RCB forcing is still available, but it is explicitly labeled as advanced and should not be the normal user flow.

## Build note

The sandbox used to prepare this patch does not have the .NET SDK installed, so `dotnet build` could not be executed here. XAML XML parsing and C# brace-balance checks were performed.
