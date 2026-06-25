# Moxa Multi-MMXU Discovery Fix

This patch fixes the case where a Moxa/GD-style IEC 61850 gateway exposes many `MMXU` logical nodes (`MMXU1`..`MMXU24`) in the MMS data model, but the ARIEC61850 live-model snapshot only builds high-confidence read targets for `MMXU1`.

## Changes

- Keeps logical-node instances when ARIEC61850 exposes `LnClass=MMXU` plus numeric `Inst/InstanceNumber` instead of a full `MMXU2` name.
- Resolves logical-device MMS domains from existing data-object references, so synthetic points stay under `GD7700LD01/...` instead of `LD01/...`.
- Adds a conservative Moxa/GD multi-MMXU profile fallback:
  - if a Moxa/GD-style gateway only shows `MMXU1`, ARServer now synthesizes `MMXU2`..`MMXU24` core SCADA measurement points,
  - each synthetic point is still live-probed and mapped by the auto report planner,
  - uncovered/unreadable signals remain polling fallback or can be left unselected.

## Why

Static reporting and shallow MMS discovery are different things. A signal can exist in the MMS data model even when it is not fully expanded during online discovery and even when it is not part of any static DataSet. The wizard must still show the real SCADA signal candidates, while the planner decides report vs polling fallback.
