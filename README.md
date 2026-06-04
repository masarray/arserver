# ARServer

ARServer is a Windows desktop gateway for publishing IEC 61850 MMS data to Modbus TCP and MQTT for HMI/SCADA tools such as FUXA.

The project is built for substation and relay-bench workflows where operators need a readable IEC 61850 explorer, a deterministic Modbus map, configurable MQTT topic routing, and diagnostics that separate real communication failures from normal polling noise.

**License architecture:** ARServer core is licensed under Apache-2.0. The optional real IEC 61850 MMS adapter loads libiec61850-compatible DLLs only when the user supplies them beside the executable. ARServer source and release packages do not bundle libiec61850 binaries or source.

[Download the latest Windows installer](https://github.com/masarray/arserver/releases)

## Screenshots

### Start Workspace

![ARServer start workspace](docs/assets/screenshots/arserver-start.webp)

### Live IEC 61850 Values

![ARServer live IEC 61850 values](docs/assets/screenshots/arserver-iec-values.webp)

### Modbus TCP Server Map

![ARServer Modbus TCP server map](docs/assets/screenshots/arserver-modbus-server.webp)

### MQTT Topic Routing

![ARServer MQTT topic routing](docs/assets/screenshots/arserver-mqtt-topics.webp)

## What It Does

- Connects to IEC 61850 MMS relays using an optional libiec61850-compatible adapter when user-provided runtime DLLs are available.
- Provides a mock IEC 61850 mode for UI, mapping, and Modbus gateway testing without a relay.
- Imports SCL/CID/SCD files and helps select SCADA-ready signals.
- Builds Modbus TCP bindings for coils, discrete inputs, input registers, and holding registers.
- Runs a read-only Modbus TCP server for HMI polling.
- Publishes the same IEC 61850 runtime values to MQTT so HMI clients can subscribe instead of polling.
- Shows runtime diagnostics, IEC activity, Modbus polling status, stale values, and per-signal quality.
- Provides an adjustable IEC 61850 MMS polling target from 10 ms upward for bench monitoring, with cache-based Modbus/MQTT publishing.
- Adds a Fast CB acquisition lane so breaker/status/Boolean/protection points are scheduled before slower analog/quality points.

## Current Scope

ARServer is currently a WPF/.NET 8 Windows application focused on IEC 61850 MMS polling, Modbus TCP publishing, and MQTT publishing through an external broker.

The UI already supports a multi-IED workspace model, but the runtime architecture should still be treated as a careful field tool in active development. Validate mappings and communication behavior on a test bench before connecting it to operational environments.

## Requirements

- Windows 10 or later
- .NET 8 SDK for building from source
- Inno Setup 6 if you want to rebuild the Windows installer locally
- Optional real IEC 61850 runtime DLLs supplied by the user and copied beside the built executable under their own license terms:
  - `iec61850dotnet.dll`
  - `iec61850.dll`
- Optional MQTT broker for MQTT output, for example Eclipse Mosquitto.

Without those DLLs, ARServer can still run in mock mode for mapping, MQTT, and Modbus TCP testing.

## Download Release

Ready-to-install Windows builds are published on GitHub Releases:

https://github.com/masarray/arserver/releases

The release package is a ZIP containing an Inno Setup installer for Windows x64. The installer includes the Apache-2.0 license, NOTICE, README, and third-party notices. It must not include libiec61850 DLLs unless a separate redistribution right/commercial license is in place.

## Build

```powershell
dotnet build ARServer.sln
```

Build output is written under:

```text
bin\Debug\net8.0-windows\
```

## Run

Open the solution in Visual Studio, or run the built WPF executable from the build output folder.

For real relay testing:

1. If you need real IED communication, provide your own licensed libiec61850 .NET/native DLLs beside `ArServer.exe`.
2. Start ARServer.
3. Add or connect an IED by IP address and MMS port, usually TCP `102`.
4. Select SCADA/HMI signals.
5. Build or validate the Modbus map.
6. Enable Modbus TCP, MQTT, or both.
7. Start runtime and point the HMI client to the selected output.

## Modbus Mapping Guidance

Recommended area policy:

- Protection and status booleans: Discrete Input / FC02 / `1xxxx`
- Position enums: Input Register / FC04 / `3xxxx`
- Analog Float32 values: Holding Register / FC03 / `4xxxx`
- Quality, age, and sequence metadata: Holding Register / FC03 / `4xxxx`

For multi-IED planning, keep address separation inside each Modbus area. Example:

- IED-01: DI `10001+`, IR `30001+`, HR `40001+`
- IED-02: DI `11001+`, IR `31001+`, HR `41001+`
- IED-03: DI `12001+`, IR `32001+`, HR `42001+`

## MQTT Output

MQTT output is implemented as a publisher to an external MQTT broker. This keeps ARServer small and interoperable while allowing production deployments to use hardened brokers such as Mosquitto, EMQX, or HiveMQ.

Default MQTT settings:

- Broker: `127.0.0.1`
- Port: `1883`
- Topic root: `arserver`
- QoS: `0`
- Retain last value: enabled
- JSON state payload: enabled

Topic layout:

```text
arserver/{iedName}/{tagName}/value
arserver/{iedName}/{tagName}/quality
arserver/{iedName}/{tagName}/status
arserver/{iedName}/{tagName}/state
arserver/status
```

The `/value` topic is a simple scalar payload for HMI tags. The `/state` topic is JSON for richer dashboards and diagnostics.

Modbus TCP and MQTT can be enabled independently. The IEC 61850 relay is still read once by ARServer; enabled outputs receive values from the same runtime cache.

## IEC 61850 MMS Polling Time

The selected IED workspace now shows the **MMS poll** setting directly on the right-side command bar, immediately before **Edit IED Wizard**. Enter the target interval in milliseconds, click **Apply**, then open/edit the wizard or start runtime. The fastest accepted target is `10 ms`, aligned with the smallest scan-rate class commonly exposed by Kepware-style IEC 61850 MMS drivers. Treat this as expert bench mode for one/few tags, not as a default multi-IED operating mode.

Beside the polling control there is a **Fast CB** switch. When enabled, ARServer uses a fast acquisition lane: CB position, switch status, Boolean, trip/start/general protection flags and similar discrete points are selected first in every scheduler cycle. Measurement, quality and timestamp points still run, but they do not block breaker-status refresh.

The Modbus Server workspace still shows the active MMS polling status, but the editable timing control intentionally lives beside the IEC wizard because this is an IEC 61850 acquisition setting, not a Modbus output setting.

Important field notes:

- The configured value is a scheduler target, not a guaranteed relay response time. Actual update speed depends on the relay MMS server, network latency, number of active points, and active IED count.
- With **Fast CB** enabled, ARServer is no longer a simple first-N round-robin. It runs a priority lane first, then a smaller normal lane so analog points do not starve.
- FUXA/SCADA Modbus reads never trigger direct relay reads; ARServer polls IEC 61850 into its cache, then Modbus TCP and MQTT publish from that cache.
- For protection-grade event capture, prefer IEC 61850 Reports/RCB, GOOSE, or Sampled Values where available. Fast MMS polling is useful for bench monitoring and HMI refresh, not as a substitute for event/report architecture.

## Repository Layout

```text
ARServer.sln
ARServer.csproj
MainWindow.xaml / MainWindow.xaml.cs
Models/
Services/
Assets/
```

Key service classes:

- `Services/BridgeRuntime.cs` coordinates IEC polling and Modbus publishing.
- `Services/MqttGatewayPublisher.cs` publishes selected runtime values to MQTT topics.
- `Services/ModbusTcpServer.cs` implements the read-only Modbus TCP server.
- `Services/RealLibIec61850Client.cs` adapts a user-provided libiec61850.NET runtime through reflection.
- `Services/SclImportService.cs` imports SCL/CID/SCD signal definitions.

## Safety Notes

ARServer is read-only on the Modbus side by design. Write functions are rejected to avoid accidental relay or process control from an HMI client.

For field use, verify:

- IEC object references and functional constraints.
- Modbus address ranges and data types.
- Word order for Float32 values.
- Stale/quality behavior during relay disconnects.
- Network segmentation, firewall rules, and port exposure.

## License

ARServer core is open source under the Apache License 2.0. See [LICENSE](LICENSE).

The repository does **not** distribute libiec61850 source or binaries. Real IEC 61850 MMS communication requires a separately supplied runtime such as libiec61850 under GPLv3 or a commercial license from its copyright holder. If you distribute a package that bundles GPLv3 libiec61850 binaries with ARServer, treat that combined distribution separately and review the GPL/commercial licensing obligations.

MQTTnet is MIT-licensed and compatible with Apache-2.0 distribution. Lucide icons are ISC-licensed and may be used in this project when their copyright/license notice is preserved. Third-party dependency notices are listed in [THIRD_PARTY_NOTICES.md](THIRD_PARTY_NOTICES.md).


## Using the Software

ARServer is a Windows gateway application, not a cloud service. The landing page in `docs/` is only product documentation; the actual gateway runs locally on Windows.

For real IEC 61850 and MQTT operation:

1. Install ARServer from [GitHub Releases](https://github.com/masarray/arserver/releases), or build it from source.
2. For real IED communication, copy your own licensed `iec61850dotnet.dll` and `iec61850.dll` beside `ArServer.exe`. These files are not distributed by ARServer.
3. Start an MQTT broker such as Mosquitto, EMQX, or HiveMQ if MQTT output is enabled.
4. Add an IED by IP address and MMS port.
5. Discover IEC 61850 objects, select SCADA/HMI signals, and validate the Modbus map.
6. In the MQTT tab, select which signals publish to broker topics.
7. Start runtime and connect FUXA/HMI either through Modbus TCP or MQTT.
