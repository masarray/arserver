# Smart Full Discovery Audit

Audit result: the previous package was not yet a true IEDScout-style full discovery implementation because it still contained a vendor/profile fallback that synthesized a contiguous MMXU range. That could make one Moxa case look better, but it was not a correct generic IEC 61850 discovery strategy.

This patch changes the direction to a generic full-LN inventory strategy:

- ARServer no longer hardcodes a contiguous MMXU instance range.
- Discovery now builds a logical-node inventory from every available discovery surface:
  - ARIEC61850 live model logical devices / logical nodes / data objects.
  - MMS GetNameList NamedVariable results.
  - MMS GetNameList NamedVariableList / DataSet names.
  - Report inventory DataSet and RCB references.
  - Public strings exposed by the ARIEC61850 discovery object, inspected generically by reflection so newer ARIEC61850 snapshots can be consumed without ARServer-side vendor profiles.
- The wizard remains signal-centric. It shows SCADA value candidates, while q/t and engineering attributes remain sidecar/diagnostic attributes.
- The report planner remains context-aware: DataSet/RCB coverage is a transport plan, not a user burden. Static-report gaps stay as polling fallback.

Important limitation:

For exact IEDScout parity, ARIEC61850 itself must expose the full MMS directory/type tree for every logical node and data object. If an IED only returns shallow NamedVariable names and the current ARIEC61850 snapshot does not expose child type information, ARServer can only infer safe fallback points for discovered LN shells. It should not invent undiscovered LN instances.

Validation target:

When connected to a gateway that exposes MMXU1, MMXU2, ..., MMXUn in the actual MMS directory, ARServer should discover each logical-node instance from the directory/hints and then create SCADA candidates per discovered instance. It should not create MMXU instances that are not present in the IED directory.
