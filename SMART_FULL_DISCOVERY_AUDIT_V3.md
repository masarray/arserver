# ARServer Smart Full Discovery Audit V3

## Why MMXU2..MMXU24 could still be missing

The previous fixes were still too dependent on one high-level discovery artifact. If the first MMS `GetNameList` page was treated as final, a large IED could appear to expose only the early logical nodes, for example `MMXU1`, even though IEDScout continues browsing and shows `MMXU2..MMXU24`.

## Fixes in this patch

1. **MMS GetNameList pagination fixed**
   - MMS `moreFollows` has ASN.1 default TRUE.
   - ARServer previously treated an omitted `moreFollows` as FALSE.
   - That can stop discovery after page 1.
   - The decoder now defaults to TRUE and the session continues while new names are returned.
   - If a trailing page fails after names were already collected, ARServer keeps the collected names instead of throwing away the discovery result.

2. **GetVariableAccessAttributes type-tree expansion added**
   - Supplemental clean-room discovery now calls MMS `GetVariableAccessAttributes` for discovered domain variables.
   - This expands LN/FC/DO/DA structure similarly to what an IED browser does.
   - Expanded names are merged into the native snapshot before signal mapping.

3. **No hardcoded MMXU instance range**
   - This patch does not synthesize `MMXU1..MMXU24` by range.
   - Logical node instances must come from MMS directory, variable attributes, report inventory, DataSet inventory, or ARIEC61850 live model objects.

4. **Wizard remains signal-centric**
   - Report/DataSet selection is not a user decision in the wizard.
   - Step 3 is a read-only Auto Report Plan review.
   - Main toolbar button is renamed to Auto Plan.

5. **Selection workflow improved**
   - Grid supports Extended row selection.
   - Header checkbox selects/deselects visible publishable signals.
   - Space toggles all highlighted rows.
   - Ctrl/Shift selection can be used before toggling.

## Expected test result

For a Moxa/GD style IED that IEDScout shows with `MMXU1..MMXU24`, the wizard should now populate from the actual MMS directory/type tree. If it still shows only `MMXU1`, check the Native Discovery log:

- If raw variables remain low, the IED is not returning the full directory to ARServer's MMS association.
- If raw variables are high but SCADA candidates are low, the mapper still needs refinement.
- If VAA/type-tree expansion errors appear, the next fix should focus on the MMS GetVariableAccessAttributes request/decoder profile for that IED.
