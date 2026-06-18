# ARServer

**Native IEC 61850 MMS to Modbus TCP + MQTT Gateway for Windows**

ARServer is an open-source Windows engineering gateway for turning selected IEC 61850 IED values into deterministic Modbus TCP registers and MQTT topics for HMI, SCADA, relay-bench, FAT/SAT, dashboard, and substation automation workflows.

```text
IED / Relay → IEC 61850 MMS → ARServer runtime cache → Modbus TCP and/or MQTT
```

[Product Landing Page](https://masarray.github.io/arserver/) · [Quick Start](docs/QUICK_START.md) · [Troubleshooting](docs/TROUBLESHOOTING.md) · [Validation Matrix](docs/VALIDATION_MATRIX.md) · [Roadmap](docs/ROADMAP.md)

---

## Why ARServer exists

Many HMI and dashboard tools are excellent at Modbus TCP or MQTT but do not provide a simple, transparent IEC 61850 engineering workflow. ARServer fills that gap by helping engineers:

- connect to a live IEC 61850 IED by IP address;
- import SCL/CID/SCD/ICD engineering files when available;
- choose SCADA-ready points such as breaker position, protection start/trip, alarms, status, and measurements;
- probe selected signals before committing them to runtime;
- publish a clear Modbus TCP map and optional MQTT topics from the same runtime cache;
- keep value, quality, device timestamp, local timestamp, and diagnostics visible.

The project is released under **Apache-2.0** and contains a native IEC 61850 MMS client implementation inside this repository. No separate IEC 61850 driver package is required for the normal IP discovery, SCL import, selected-read, Modbus TCP, or MQTT workflow.

---

## Main capabilities

| Area | Capability |
|---|---|
| IEC 61850 source | Native MMS association over TCP port `102` |
| Online setup | Add IED by IP and discover supported model candidates |
| Engineering setup | Import SCL, CID, SCD, ICD, IID, SED, or XML files |
| Signal planning | Recommend SCADA-friendly points and report-aware candidates |
| Validation | Probe selected signals before saving to runtime |
| Runtime | Poll selected values with value, timestamp, quality, and inferred type |
| Modbus TCP | Serve mapped values to HMI/SCADA clients through configurable registers |
| MQTT | Publish value, quality, local timestamp, and device timestamp to broker topics |
| Diagnostics | Show IEC activity, runtime status, Modbus polling, MQTT state, stale values, and communication issues |
| Project workflow | Save and load local gateway projects |

---

## Product landing page and Wiki

The public landing page lives in `docs/index.html` and is deployed through GitHub Pages.

- Landing page: <https://masarray.github.io/arserver/>
- GitHub Pages deployment notes: [docs/DEPLOYMENT.md](docs/DEPLOYMENT.md)
- Documentation front door: [docs/QUICK_START.md](docs/QUICK_START.md)

Useful documentation:

| Document | Purpose |
|---|---|
| [Quick Start](docs/QUICK_START.md) | First workflow from relay IP or SCL file to running gateway |
| [Troubleshooting](docs/TROUBLESHOOTING.md) | Connection, discovery, Modbus, MQTT, stale value, and firewall checks |
| [Validation Matrix](docs/VALIDATION_MATRIX.md) | Bench/FAT checklist for evaluating behavior safely |
| [Roadmap](docs/ROADMAP.md) | Implemented milestones and native IEC 61850 direction |
| [Deployment](docs/DEPLOYMENT.md) | GitHub Pages, SEO, and static site publishing notes |
| [Security Policy](SECURITY.md) | Safe security reporting guidance |

---

## Quick workflow

### Option A — Add IED by IP

Use this when you only have a live relay endpoint.

1. Open ARServer.
2. Click **+ Add IED**.
3. Choose **Add by IP**.
4. Enter the relay IP address and MMS port. The usual MMS port is `102`.
5. Click **Connect & Discover**.
6. Review the discovered IEC 61850 candidates.
7. Select the signals you want to publish.
8. Use **Probe Selected** to verify that the IED accepts the selected object.
9. Assign Modbus addresses and enable MQTT when needed.
10. Click **Add to Runtime**.
11. Start runtime.
12. Point your HMI or SCADA Modbus TCP client to the ARServer PC.

### Option B — Open SCL / CID / SCD / ICD

Use this when you have the engineering model.

1. Click **+ Add IED**.
2. Choose **Open SCL** or import the file from the wizard.
3. Select a `.cid`, `.scd`, `.icd`, `.iid`, `.sed`, or `.xml` file.
4. Confirm or override the runtime IP address.
5. Review recommended IEC 61850 signals, DataSet information, and ReportControl candidates.
6. Select the tags required by your HMI/SCADA.
7. Probe selected tags when the IED is reachable.
8. Assign Modbus and MQTT routing.
9. Save to runtime and start.

Use **Open SCL** when engineering files are available. Use **Add by IP** for fast online setup.

---

## Runtime grid

The live IEC 61850 grid is arranged for operation, not only engineering browsing:

```text
IEC Object | Value | Timestamp | Quality | Type
```

- **IEC Object** — IEC 61850 object reference used by the gateway.
- **Value** — decoded live value such as `Closed`, `Open`, `True`, `False`, or numeric values.
- **Timestamp** — device timestamp from the IEC 61850 `t` attribute when readable.
- **Quality** — decoded quality from the IEC 61850 `q` attribute when readable.
- **Type** — inferred data type such as `Dbpos`, `Boolean`, `Float`, `Integer`, or `Quality`.

If device timestamp or quality is not available for a point, ARServer leaves it blank or marks the condition clearly instead of inventing a value.

---

## Modbus TCP output

ARServer runs a local Modbus TCP server. HMI/SCADA software connects to ARServer instead of polling the relay directly.

Default endpoint:

```text
Bind address: 0.0.0.0
Port: 502
Unit ID: 1
```

Recommended area policy:

| Signal type | Suggested Modbus area |
|---|---|
| Protection and status booleans | Discrete Input / FC02 / `1xxxx` |
| Position enums | Input Register / FC04 / `3xxxx` |
| Analog Float32 values | Holding Register / FC03 / `4xxxx` |
| Quality, age, and sequence metadata | Holding Register / FC03 / `4xxxx` |

For multi-IED planning, keep address separation inside each Modbus area. Example:

```text
IED-01: DI 10001+, IR 30001+, HR 40001+
IED-02: DI 11001+, IR 31001+, HR 41001+
IED-03: DI 12001+, IR 32001+, HR 42001+
```

---

## MQTT output

MQTT output is implemented as a publisher to an external MQTT broker. This keeps ARServer small and interoperable while allowing production deployments to use hardened brokers.

Default MQTT settings:

```text
Broker: 127.0.0.1
Port: 1883
Topic root: arserver
QoS: 0
Retain last value: enabled
JSON state payload: enabled
```

Topic layout:

```text
arserver/{iedName}/{tagName}/value
arserver/{iedName}/{tagName}/quality
arserver/{iedName}/{tagName}/status
arserver/{iedName}/{tagName}/state
arserver/status
```

The `/value` topic is a simple scalar payload for HMI tags. The `/state` topic is JSON for richer dashboards and diagnostics.

---

## Current protocol scope

Implemented native scope:

- TCP connection to port `102`.
- TPKT and COTP transport.
- ACSE/MMS association.
- MMS Confirmed-Read for selected object references.
- MMS GetNameList-based online discovery.
- IEC object candidate mapping from MMS names.
- Companion quality and timestamp reads.
- SCL import and DataSet/RCB-aware planning.

Planned native scope:

- wider data type coverage;
- multi-variable read optimization;
- online DataSet/RCB verification;
- report activation with polling fallback;
- richer quality/timestamp mapping to Modbus registers;
- exportable mapping and validation reports.

See [docs/ROADMAP.md](docs/ROADMAP.md) for the milestone direction.

---

## Safety and validation

ARServer is read-only on the Modbus side by design. Write functions are rejected to reduce accidental relay or process-control risk from HMI clients.

For field use, validate these points on a controlled bench or FAT environment first:

- IEC object references and functional constraints;
- Modbus address ranges and data types;
- Float32 word order and scaling;
- stale/quality behavior during relay disconnects;
- Windows firewall and network segmentation;
- port exposure and client access control;
- HMI address convention and Unit ID settings.

Use the [Validation Matrix](docs/VALIDATION_MATRIX.md) before expanding point count or connecting operational environments.

---

## Build from source

Requirements:

- Windows 10/11;
- Visual Studio 2022;
- .NET 8 SDK.

Build:

```powershell
git clone https://github.com/masarray/arserver.git
cd arserver
dotnet restore
dotnet build -c Release
```

Run from Visual Studio or from the generated output folder.

---

## Repository layout

```text
ARServer.sln
ARServer.csproj
MainWindow.xaml / MainWindow.xaml.cs
Models/
Services/
Protocol/
Assets/
docs/
.github/workflows/
```

Key areas:

- `Services/` contains gateway services, runtime, Modbus, MQTT, SCL import, discovery mapping, and IEC client boundaries.
- `Protocol/` contains native OSI, ASN.1 BER, ACSE, MMS, and IEC 61850 protocol building blocks.
- `Models/` contains project, binding, signal, relay, report, and runtime snapshot models.
- `docs/` contains the public landing page and product Wiki documentation.

---

## License

Apache-2.0. See [LICENSE](LICENSE).
