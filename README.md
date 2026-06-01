# ArServer — IEC 61850 to Modbus TCP Gateway

ArServer is a workspace-first IEC 61850 MMS to Modbus TCP gateway for FUXA/HMI use.

## v0.19 focus

- Pivoted UX into three product zones:
  - **IEC 61850 Explorer**: multi-IED list, connect/discover, SCADA signal wizard.
  - **Modbus Server**: published Modbus map, start/stop runtime, traffic status.
  - **Diagnostics**: event-based logs only; repetitive Modbus polling is summarized by counters/activity light.
- Added IED lifecycle actions:
  - Add IED
  - Open / Edit
  - Pause / Disconnect
  - Delete IED
- Duplicate IED guard:
  - Same IP:port updates/reconnects the existing relay chip instead of creating a duplicate.
  - Same IEDName on different IP gets a safe suffix based on IP last octet.
- Binding edit flow:
  - Existing binding rows stay editable on Modbus side.
  - New selected IEC 61850 signals can be added without recreating the full binding map.
  - Selected binding rows can be removed.
- Signal recommendation improved:
  - Position first: CSWI/XCBR/XSWI `Pos.stVal`.
  - Protection second: PTRC/PTOC/PDIF/PDIS operate/trip/start signals.
  - Measurement last: MMXU/MMXN **cVal.mag.f** current/voltage only.
  - `instCVal` is no longer auto-recommended when choosing SCADA/HMI tags, to avoid duplicate Phase A/B/C current/voltage tags.
  - Har/Min/Max/Mean/Avg/Dmd MMXU/MMXN remain searchable only via advanced/search, not default recommendations.
- Modbus strategy remains:
  - one Modbus TCP server endpoint: `0.0.0.0:502`, Unit ID 1 by default.
  - address separation must happen inside DI/IR/HR areas, not by misusing 10000/30000/40000 blocks as relay IDs.

## Real IEC 61850 engine

Copy the MZ Automation libiec61850 .NET/native DLLs beside `ArServer.exe`:

- `iec61850dotnet.dll`
- `iec61850.dll`

The app auto-detects them and enables real IEC 61850 mode.

## Recommended Modbus area policy

- Protection Boolean: Discrete Input / FC02 / `1xxxx`
- Position enum: Input Register / FC04 / `3xxxx`
- Analog Float32: Holding Register / FC03 / `4xxxx`
- Quality/age/sequence metadata: Holding Register / FC03 / `4xxxx`

For multi-IED final planning, use address offsets per relay inside each Modbus area, for example:

- IED-01: DI 10001+, IR 30001+, HR 40001+
- IED-02: DI 11001+, IR 31001+, HR 41001+
- IED-03: DI 12001+, IR 32001+, HR 42001+

## Notes

This build still uses one active IEC 61850 runtime session internally. The UI/model now follows the intended multi-IED gateway console pattern; the next engine milestone is an actual `RelaySessionManager` with independent IEC sessions and per-IED tag caches.

## v0.20 - IED workflow runtime guard

- Fixes a runtime exception where Add/Edit IED or Connect & Discover could dispose/replace the active IEC 61850 client while the runtime polling loop was still reading values.
- Connect & Discover now pauses runtime before replacing the MMS client.
- Add IED now pauses runtime in the current MVP architecture. Saved bindings remain intact; restart runtime after completing the IED workflow.
- BridgeRuntime now treats a disconnected IEC 61850 client as a runtime state, not a crash: bindings are marked `IEC Disconnected` and diagnostics are logged once instead of spamming every tag.
- This is still a single active IEC 61850 session MVP. The final multi-IED version should move reading into RelaySessionManager with one client/cache per IED.

## v0.21 — Explorer + Wizard UX correction

- First-open blank page replaced with a premium IEC 61850/Modbus gateway hero empty state.
- Add IED now opens a dedicated WPF wizard window instead of silently creating unclear draft slots.
- IEC 61850 Explorer now uses a vertical left IED explorer as the main multi-IED context.
- Selected IED becomes the workspace context: the center grid shows signals/values for the active IED.
- IED cards support Open, Edit, Pause, and Delete actions.
- Wizard prevents empty IP workflow; only successful Connect/Discover operations are stored in relay history.
- Discover and binding remain workspace actions after wizard connect: select recommended SCADA signals, add/create Modbus binding, save, then run Modbus server.
- Empty state image is embedded as `Assets/gateway-hero.png`.

Design intent: workspace-first, fast operator flow, compact multi-IED lifecycle, and no blank startup screen.

## v0.22 — Wizard/Explorer separation and crisp web-style controls

- First launch now forces the IEC 61850 Explorer tab to render immediately, so the app does not open into a blank workspace.
- The empty explorer uses the IEC 61850/Modbus gateway hero image as a polished first-use state.
- IEC 61850 Explorer is now a runtime viewing workspace only: left compact IED explorer + center live value grid + Edit IED Wizard button.
- Signal selection, filtering, add/remove signal, binding rebuild, binding validation, and save are moved into a dedicated `IedConfigurationWizardWindow`.
- Add IED workflow: connect/discover first, then automatically open the configuration wizard for signal and Modbus binding.
- Edit IED workflow: opens the configuration wizard for existing discovered signals/bindings instead of editing directly in the explorer page.
- Button templates were rebuilt so hover/pressed effects animate the vector/shape chrome while keeping text clear and sharp; no scaling of the text layer.
- The UI keeps viewing and editing separate to preserve a lightweight runtime workspace.

## v0.23 — Wizard Workflow, Live Value Snapshot, and Runtime-First Explorer

- Explorer is now strictly a runtime viewing workspace. Add/remove signal selection and Modbus binding are handled only inside the IED Configuration Wizard.
- IED Configuration Wizard is now a true 3-step workflow:
  1. Select IEC 61850 SCADA signals.
  2. Build and validate Modbus TCP binding.
  3. Add the IED configuration to the runtime workspace.
- Wizard cancellation restores the previous selection/binding state instead of silently modifying the runtime configuration.
- After real MMS discovery and after wizard save, ArServer performs a bounded initial value snapshot for selected signals so the Explorer does not show placeholder values such as “Online / not read yet”.
- Runtime updates now mirror back into the IEC 61850 Explorer signal grid by matching IEC object reference.
- Diagnostics in the IEC 61850 Explorer are parked at the bottom of the workspace and kept collapsed by default.
- Segmented navigation and runtime toggle no longer use gray hover overlays. Interaction is text-size tactile over the existing vector sliding pill, keeping the text crisp.
- CSWI/XCBR/XSWI are categorized as Position for clearer SCADA/HMI priority ordering: Position → Protection → Measurement.

## v0.24 - Noise Cleanup, Per-IED Control, App Icon

- Removed the non-essential `Successful relays` visual panel from the IEC 61850 Explorer workspace to reduce visual noise.
- Removed the redundant `Open` button from IED cards. Clicking the compact IED card itself selects/opens that IED workspace.
- Replaced the problematic `Pause` button with a clearer `Connect/Disconnect` action per IED card.
  - Disconnecting an IED no longer toggles the main Modbus Gateway Runtime Start/Stop state.
  - The main Runtime Start/Stop remains responsible for the Modbus TCP gateway server lifecycle.
  - Current MVP still has one active IEC client internally; the final multi-session engine should use one IEC session/cache per IED.
- Added a custom ArServer application icon in `Assets/app-icon.ico` and `Assets/app-icon.png`.
- Added the app icon to the window title bar, app bar, and project application icon metadata.

## v0.25 - Runtime polish, per-IED cleanup, and tray behavior

- Removed the buggy per-IED Connect/Disconnect action from the IED card. IED cards are now clean runtime selectors with Edit/Delete only; the main Gateway runtime remains controlled from the Modbus Server tab.
- Minimize now hides ArServer to the Windows tray. The gateway can keep running while the main window is hidden; double-click the tray icon or use Restore.
- IEC 61850 diagnostics expander is anchored to the bottom of the right workspace and no longer compresses the left IED Explorer panel.
- IEC value grid and Modbus mapping grid use proportional column widths and horizontal scrollbars are disabled to keep all key data visible in the workspace.
- Added separate activity indicators:
  - IED MMS activity pulses blue/green when IEC value traffic is observed.
  - Modbus server activity pulses when master polling is observed.
- Runtime toggle pill now uses clearer gateway semantics: Start = green, Stop = red.

## v0.25b namespace/build stability fix
- Removed Windows Forms tray integration to avoid namespace conflicts in WPF (`MessageBox`, `TabControl`, `Button`, `Color`, `Brush`).
- Removed `<UseWindowsForms>true</UseWindowsForms>` from the project file.
- Minimize now behaves as normal Windows minimize; no hide-to-tray behavior.
- Runtime polish from v0.25 is preserved: compact IED cards, activity indicators, optimized grids, and Start/Stop runtime color direction.

## v0.26 — UI icons, diagnostics safety, and debug-copy hardening

- Added clearer icon labels for primary commands, wizard actions, IED cards, and runtime buttons.
- Hardened exception routing: uncaught UI/background/AppDomain exceptions are routed to Diagnostics instead of interrupting the user workflow.
- If a wizard window is open during an exception, ArServer closes it and opens the Diagnostics tab so the operator can see the real error message.
- Data grids support full-row selection and Ctrl+C copy with headers for easier field debugging and support handoff.
- Diagnostics remain capped to avoid memory growth; repeated Modbus polling is still summarized via counters/activity indicators rather than log spam.


## v0.27 - ArServer brand refresh
- Replaced the old icon with the new premium AR monogram logo.
- Updated executable/window icon and header/wizard/about/help branding assets.
- Added app icon PNG/ICO and horizontal logo assets under Assets/.

## v0.28 Brand Icon Refresh

- Replaced the application logo/icon with the approved transparent AR monogram.
- Updated WPF branding assets:
  - Assets/app-icon.ico
  - Assets/app-icon.png
  - Assets/app-icon-256.png
  - Assets/arserver-logo-header.png
  - Assets/arserver-logo-horizontal.png
  - Assets/arserver-logo-light.png
  - Assets/arserver-monogram-transparent-1024.png
  - Assets/arserver-monogram-transparent-2048.png
- The new logo has transparent background and minimal edge margin for stronger app branding.

## v0.29 - Multi-IED Context Fix

- IEC 61850 Explorer now treats the selected IED card as the active workspace context.
- IED card selection loads that IED's saved logical-node/value snapshot into the center grid.
- Modbus Server publishes a flattened binding map from all saved IEDs instead of only the last active IED.
- IED card status was cleaned to IEC-only information: MMS state, tag count, and RCB/mode summary.
- IED cards now use one meaningful LED only: blue = MMS connected/idle, green flash = MMS activity, red = failed/disconnected.
- Selected IED card uses a stable border/background highlight without layout-changing animation to avoid panel jump/glitch.

## v0.30 - Smart Modbus Planner and IEC Read Hardening

- Added smart multi-IED Modbus address arrangement.
  - IED-01 uses DI 10001+, IR 30001+, HR 40001+.
  - IED-02 uses DI 11001+, IR 31001+, HR 41001+.
  - IED-03 uses DI 12001+, IR 32001+, HR 42001+.
- The planner shifts addresses inside each Modbus area, instead of moving a relay into a different Modbus area.
- Validation now normalizes the whole relay map before checking overlap, so users do not need to manually arrange hundreds of registers.
- Validation warnings are logged to Diagnostics instead of showing a blocking default warning dialog.
- IEC 61850 read operations are hardened for real IEDs: access/type mismatch on one DA returns a bad/null value and is handled in the grid instead of crashing the wizard/runtime.
- Coded status and Boolean reads now try generic `ReadValue` first before typed reads to reduce libiec61850 first-chance exceptions from vendor-specific data shapes.

## v0.31 - IEC 61850 CDC/FC read hardening

- Hardened real libiec61850 read path for CDC/FC mismatch and rejected direct reads.
- A single unreadable IEC 61850 DA such as OBJECT_NONE_EXISTENT, DATA_ACCESS_ERROR, or type mismatch no longer crashes the wizard/runtime.
- Runtime now marks affected tags as `Bad / Not readable`, continues reading the remaining tags, and throttles repeated warnings per tag.
- Initial snapshot now treats null/unreadable values as Bad instead of Good.
- Boolean status reads no longer convert string error responses to integer.

Engineering note: IEC 61850 online browsing can expose DO/DA paths that are not directly readable as SCADA tags, depending on CDC, FC, dataset/report configuration, vendor implementation, access control, and object optionality. ArServer now treats those cases as per-tag diagnostics instead of application exceptions.

## v0.36 — SCL/CID Import Foundation

ArServer now supports an engineered IEC 61850 workflow in addition to IP-only online discovery.

- Import `.cid`, `.scd`, `.icd`, `.iid`, `.sed`, or XML SCL files from the Add IED Wizard.
- Parse IED name, AccessPoint, MMS IP/port, Logical Devices, Logical Nodes, DataTypeTemplates, DataSets and ReportControl blocks.
- Generate SCADA-ready `SignalDefinition` entries from SCL DataTypeTemplates, including FC-aware leaf references.
- Mark report-capable signals when their IEC reference is covered by a DataSet/FCDA and candidate ReportControl.
- Display SCL/DataSet/RCB summary in the IED card.

Current runtime remains MMS polling. RCB enable/report subscription is intentionally not activated yet; SCL import prepares the correct DataSet/RCB foundation for the next runtime stage.
