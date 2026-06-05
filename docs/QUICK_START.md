# ARServer Quick Start

This guide helps you try ARServer as a local Windows gateway for IEC 61850 MMS, Modbus TCP, and MQTT integration work.

## 1. Download

Open the latest GitHub Release and download:

```text
ARServer-vX.Y.Z-win-x64-portable.zip
```

Extract the ZIP to a writable folder, for example:

```text
C:\Tools\ARServer
```

## 2. Start the application

Run either:

```text
Start-ARServer.bat
```

or:

```text
ArServer.exe
```

The launcher is included only to make the portable package easier to start from Windows Explorer. The application itself is a Windows desktop app, not a CLI tool.

## 3. Try mock mode first

Mock mode lets you inspect the interface, select sample signals, build a Modbus map, and test MQTT output without connecting to a real IED.

Recommended first test:

1. Open ARServer.
2. Add an IED workspace.
3. Use the available sample/mock signal set.
4. Open **Edit IED Wizard**.
5. Select several position, Boolean, and analog signals.
6. Continue to **Modbus Binding**.
7. Save the selected points to runtime.
8. Enable **Modbus TCP** and start runtime.

## 4. Connect a real IED

For real IEC 61850 MMS operation, place your IEC 61850 MMS runtime components beside `ArServer.exe` before starting the app.

Then:

1. Add an IED by IP address and MMS port, normally TCP `102`.
2. Discover objects or import an SCL/CID/SCD file.
3. Select SCADA-ready signals.
4. Confirm the IEC Reference column before binding.
5. Review the Modbus map and address ranges.
6. Enable Modbus TCP and/or MQTT.
7. Start runtime.

## 5. Connect an HMI or SCADA client

Default Modbus TCP settings can be changed in the Modbus Server workspace.

Typical test flow:

```text
FUXA / HMI / SCADA Modbus master
        ↓
ARServer Modbus TCP server
        ↓
ARServer runtime cache
        ↓
IEC 61850 IED acquisition
```

The HMI reads from ARServer's local cache. It does not directly trigger relay reads.

## 6. MQTT output

MQTT requires an external broker. Common test brokers include Mosquitto, EMQX, and HiveMQ.

Typical topic layout:

```text
arserver/{iedName}/{tagName}/value
arserver/{iedName}/{tagName}/quality
arserver/{iedName}/{tagName}/status
arserver/{iedName}/{tagName}/state
arserver/status
```

Use scalar `/value` topics for simple HMI tags and `/state` JSON topics for richer dashboards.

## 7. Fast CB acquisition

Use **Fast CB** when evaluating breaker/status response time on a small selected signal set.

Recommended starting points:

| Scenario | MMS poll target |
|---|---:|
| Normal gateway testing | 500–1000 ms |
| HMI refresh evaluation | 100–250 ms |
| Few critical status tags | 20–50 ms |
| Expert single/few tag bench test | 10 ms |

The polling value is a target interval. Actual response depends on the IED, network, point count, and runtime load.
