# Smart Full Discovery Audit v2

## Findings

The previous patch still depended too much on whatever the ARIEC61850 high-level model builder returned. When that layer only produced the first readable MMXU branch, ARServer's wizard still showed only `MMXU1` even though the IED's MMS directory contained many logical-node instances.

This is not a valid gateway architecture. ARServer must first build an IED inventory as completely as IEDScout does, then filter that inventory into SCADA-friendly signals. It must never synthesize `MMXU1..24` from vendor assumptions.

## Fixes in this patch

1. Removed the idea of vendor/MMXU count profile from the active discovery path.
2. Added a supplemental read-only MMS `GetNameList` directory browse using the clean-room MMS session.
   - It merges domain named variables and named variable lists with the ARIEC61850 discovery snapshot.
   - If the IED rejects a second client association, discovery continues with the primary ARIEC61850 snapshot.
3. Added generic ARIEC61850 object-shape logical-node extraction.
   - This detects LN instances exposed as separate properties, such as `LnClass = MMXU` and `InstanceNumber = 2`.
   - It combines class and instance into `MMXU2` without hardcoding a range.
4. Wizard report selection UI is removed.
   - Step 3 is now read-only Auto Report Plan Review.
   - Users select signals only; ARServer maps signal -> DataSet -> RCB -> polling fallback automatically.
5. Signal selection UX improved.
   - Header checkbox selects/deselects all visible publishable signals.
   - Grid uses Extended row selection.
   - Ctrl/Shift row selection works through WPF selection.
   - Checkbox click applies to selected rows; Shift-click checkbox applies a block selection.

## Expected Moxa/GD7700 behavior

If the IED's MMS directory exposes `MMXU2`, `MMXU3`, ... as named variables or logical-node objects, the wizard should now show those instances. If it still shows only `MMXU1`, the remaining issue is in the connected ARIEC61850 discovery/runtime library or the clean-room GetNameList association was rejected by the IED. In that case Diagnostics should be checked for `supplemental names` in the Native Discovery summary.

