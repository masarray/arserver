# Adaptive LN Sibling Discovery Fix

This patch fixes cases where a live IED exposes only the first logical-node instance through the current ARIEC61850 model builder, while commercial browsers such as IEDScout can browse additional instances such as `MMXU3`, `MMXU4`, `MMXU5`, and so on.

## What changed

1. Added adaptive logical-node sibling proof-probing.
   - The engine no longer hardcodes `MMXU1..MMXU24`.
   - It observes real discovered logical-node patterns such as `MMXU1`.
   - It then probes adjacent IEC 61850 logical-node instances using real MMS reads.
   - A sibling instance is added only when the IED proves it exists by returning at least one readable value.
   - The probe stops after consecutive misses, so the runtime does not blindly generate endless fake signals.

2. Added VAA sibling expansion in the clean-room MMS browser.
   - If GetNameList returns a shallow root like `MMXU1`, the discovery engine can probe `MMXU2`, `MMXU3`, etc. using GetVariableAccessAttributes and expand the actual type tree when the IED accepts the object.

3. Improved MMS item parser.
   - Discovery artifacts can now be parsed whether they use `$` or `.` separators.
   - Examples normalized correctly:
     - `MMXU3$MX$PhV$phsA$cVal$mag$f`
     - `MMXU3.MX.PhV.phsA.cVal.mag.f`
     - `GD7700LD01/MMXU3$MX$PhV$phsA$cVal$mag$f`

## Why this is not hardcoding

The engine does not assume that a Moxa IED has 24 MMXU nodes. It only starts adaptive sibling discovery from logical-node classes and instance patterns that were actually discovered from the IED, then confirms every new instance with a real MMS read or GetVariableAccessAttributes response.

