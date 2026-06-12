# ARServer

**Native IEC 61850 to Modbus TCP + MQTT Gateway for Windows**

ARServer helps engineers connect IEC 61850 IEDs to practical SCADA, HMI, dashboard, and data collection tools through a clear gateway workflow:

```text
IED / Relay → IEC 61850 MMS → ARServer runtime cache → Modbus TCP and/or MQTT
```

The project is released under **Apache-2.0** and contains a native IEC 61850 MMS client implementation inside this repository. No separate IEC 61850 driver package is required for the normal IP discovery, SCL import, polling, Modbus TCP, or MQTT workflow.

## What ARServer is for

ARServer is designed for field engineers, FAT teams, panel builders, system integrators, HMI developers, and automation engineers who need a practical bridge between IEC 61850 IED data and tools that understand Modbus TCP or MQTT.

Typical use cases:

- Build a quick Modbus TCP map from IEC 61850 relay points.
- Expose CB position, alarm, protection status, and measurements to open HMI tools.
- Publish selected relay values to MQTT for dashboard or historian experiments.
- Validate IEC 61850 object references from IP discovery or SCL/CID/SCD files.
- Create a repeatable gateway configuration for bench test, FAT, demo, or troubleshooting.

## Main features

- Native IEC 61850 MMS association over TCP port 102.
- IP-only online discovery for supported IEDs.
- SCL/CID/SCD/ICD import for engineered model workflows.
- Smart signal recommendation for CB position, protection, alarms, status, and measurements.
- Probe selected signals before saving them to runtime.
- Runtime polling with value, IEC device timestamp, quality, and type.
- Modbus TCP server output with user-controlled register mapping.
- MQTT publisher output with value, quality, local timestamp, and device timestamp.
- Report-aware planning from SCL DataSet/RCB information.
- Diagnostics log for connection, discovery, validation, runtime, Modbus, and MQTT activity.
- Local project save/load.

## Quick workflow

### Option A — Add IED by IP

1. Open ARServer.
2. Click **+ Add IED**.
3. Choose **Add by IP**.
4. Enter the relay IP address and MMS port. The usual MMS port is `102`.
5. Click **Connect & Discover**.
6. Wait until discovery returns IEC 61850 candidates.
7. Select the signals you want to publish.
8. Use **Probe Selected** to verify that the IED accepts the selected object.
9. Assign Modbus addresses and enable MQTT when needed.
10. Click **Add to Runtime**.
11. Start the runtime.
12. Point your HMI or SCADA tool to the ARServer Modbus TCP endpoint.

### Option B — Open SCL / CID / SCD / ICD

1. Click **+ Add IED**.
2. Choose **Open SCL** or import the file from the wizard.
3. Select a `.cid`, `.scd`, `.icd`, `.iid`, `.sed`, or `.xml` file.
4. Confirm or override the runtime IP address.
5. Review the recommended IEC 61850 signals.
6. Select the tags required by your HMI/SCADA.
7. Probe selected tags when the IED is reachable.
8. Assign Modbus and MQTT routing.
9. Save to runtime and start.

Use **Open SCL** when you have the engineering file. Use **Add by IP** when you only have a live relay endpoint.

## Runtime grid

The live IEC 61850 grid is arranged for operation, not only engineering browsing:

```text
IEC Object | Value | Timestamp | Quality | Type
```

- **IEC Object**: IEC 61850 object reference used by the gateway.
- **Value**: decoded live value such as `Closed`, `Open`, `True`, `False`, or numeric values.
- **Timestamp**: device timestamp from the IEC 61850 `t` attribute when readable.
- **Quality**: decoded quality from the IEC 61850 `q` attribute when readable.
- **Type**: inferred data type such as `Dbpos`, `Boolean`, `Float`, `Integer`, or `Quality`.

If device timestamp or quality is not available for a point, ARServer leaves it blank or marks it clearly instead of inventing a value.

## Modbus TCP output

ARServer runs a local Modbus TCP server. HMI/SCADA software can connect to ARServer instead of connecting directly to the relay.

Default endpoint:

```text
Bind address: 0.0.0.0
Port: 502
Unit ID: 1
```

Recommended workflow:

1. Select IEC 61850 signals.
2. Assign register addresses.
3. Start runtime.
4. Open the HMI/SCADA tool.
5. Connect the HMI/SCADA Modbus TCP client to the ARServer PC IP and configured port.
6. Read the mapped registers.

## MQTT output

MQTT can be enabled per binding. Published payloads include operational context such as value, quality, local timestamp, and device timestamp.

Typical use:

- lightweight dashboard,
- historian experiment,
- integration with automation middleware,
- lab telemetry,
- proof-of-concept data export.

## SCL vs IP discovery

| Workflow | Best when | Strength |
|---|---|---|
| Add by IP | You only know the relay IP | Fast discovery from live IED |
| Open SCL/CID/SCD | You have engineering files | More deterministic object, DataSet, FCDA, and RCB planning |

Both workflows end in the same runtime: selected IEC 61850 values are cached and published to Modbus TCP and/or MQTT.

## Current protocol scope

Implemented native scope:

- TCP connection to port 102.
- TPKT and COTP transport.
- ACSE/MMS association.
- MMS Confirmed-Read for selected object references.
- MMS GetNameList-based online discovery.
- IEC object candidate mapping from MMS names.
- Companion quality and timestamp reads.
- SCL import and DataSet/RCB-aware planning.

Planned native scope:

- wider data type coverage,
- multi-variable read optimization,
- online DataSet/RCB verification,
- report activation with polling fallback,
- richer quality/timestamp mapping to Modbus registers,
- exportable mapping reports.

## FAQ

### Is ARServer free and open source?

Yes. ARServer is distributed under the Apache-2.0 license.

### Does ARServer need a separate IEC 61850 driver?

No for the normal workflow in this repository. IP discovery, SCL import, native MMS association, selected reads, Modbus TCP output, and MQTT output are handled by ARServer code.

### Which port does IEC 61850 MMS use?

IEC 61850 MMS usually uses TCP port `102`. Some lab networks or relay settings may use a different port, so the port is editable.

### Should I use Add by IP or Open SCL?

Use **Add by IP** for fast live discovery. Use **Open SCL** when you have the project engineering file and want more deterministic signal planning.

### Why does discovery return many signals?

IEDs expose many logical nodes and data attributes. ARServer recommends SCADA-friendly points first, such as CB position, protection operation/start, alarms, status, and measurements.

### Why should I probe selected signals?

Discovery can show candidates, but a probe confirms that the relay accepts the final MMS object reference and returns a decodable value.

### Why is the value good but timestamp blank?

Some IEDs expose value but do not expose or allow reading the companion `t` attribute for that object. ARServer does not invent device timestamps.

### Why is quality blank or bad?

Quality comes from the companion `q` attribute when available. If it is unreadable or the IED returns an invalid status, ARServer shows the condition instead of hiding it.

### Can ARServer connect to multiple IEDs?

The runtime supports per-IED sessions and per-IED bindings. Use separate IED entries and assign register blocks clearly to avoid overlapping Modbus addresses.

### Can FUXA connect to ARServer?

Yes. Configure FUXA as a Modbus TCP client and point it to the PC running ARServer, using the configured port and Unit ID.

### Can other SCADA/HMI tools connect?

Yes, any tool that can read Modbus TCP registers can read ARServer's Modbus output. MQTT subscribers can also consume the enabled MQTT topics.

### Does ARServer write commands to the relay?

The current focus is read-only gateway operation. Control/write support should be added only after strict safety checks, confirmation workflow, and test evidence.

### Does ARServer support reports now?

ARServer keeps RCB and DataSet planning inside the Edit IED Wizard. This matches the real workflow: select signals, choose one reporting plan, build Modbus/MQTT mapping, then save the IED. IP discovery and polling do not auto-probe or enable RCBs. The wizard can run a read-only RCB attribute probe, but report activation is planned for a later phase after DataSet directory, receive loop, and safe enable/disable sequencing are implemented. Polling remains the safe baseline.

### What should I check when connection fails?

Check IP address, port 102, Windows firewall, network route, relay MMS service setting, VLAN, and whether another client session is limiting access.

### What should I check when Modbus clients read zero values?

Confirm runtime is running, the selected IEC value is live, the binding is enabled, the register address is correct, and the HMI uses the same Unit ID and address convention.

## Build from source

Requirements:

- Windows 10/11
- Visual Studio 2022
- .NET 8 SDK

Build:

```powershell
git clone https://github.com/masarray/arserver.git
cd arserver
dotnet restore
dotnet build -c Release
```

Run from Visual Studio or from the generated output folder.

## Repository notes

- `Services/` contains gateway services, runtime, Modbus, MQTT, SCL import, discovery mapping, and IEC client boundaries.
- `Protocol/` contains native OSI, ASN.1 BER, ACSE, MMS, and IEC 61850 protocol building blocks.
- `Models/` contains project, binding, signal, relay, report, and runtime snapshot models.
- `docs/` contains user documentation, quick start, troubleshooting, validation, and roadmap files.

## License

Apache-2.0. See [LICENSE](LICENSE).
