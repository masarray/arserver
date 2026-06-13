# ARServer Quick Start

This guide walks from a relay IP or SCL file to a running Modbus TCP/MQTT gateway.

## 1. Start the app

Open `ArServer.exe` or run the project from Visual Studio 2022.

The app opens on **IEC 61850 Explorer**. Runtime is stopped until you press **Start**.

## 2. Add an IED by IP

Use this when you have a live relay endpoint.

1. Click **+ Add IED**.
2. Choose **Add by IP**.
3. Enter the IED IP address.
4. Keep MMS port `102` unless your relay uses another port.
5. Click **Connect & Discover**.
6. Review the discovered IEC 61850 candidates.
7. Select the SCADA points you need.
8. Click **Probe Selected** to verify the selected point is readable.
9. Assign Modbus register addresses and MQTT routing.
10. Click **Add to Runtime**.

## 3. Add an IED from SCL/CID/SCD/ICD

Use this when you have the engineering model.

1. Click **+ Add IED**.
2. Choose **Open SCL**.
3. Select the file.
4. Review the detected IED name, IP, DataSet, and ReportControl candidates.
5. Override the runtime IP if the actual relay IP differs from the file.
6. Select SCADA tags.
7. Probe selected tags when the relay is reachable.
8. Assign Modbus/MQTT routing.
9. Click **Add to Runtime**.

## 4. Check the runtime grid

The runtime grid is arranged as:

```text
IEC Object | Value | Timestamp | Quality | Type
```

A healthy status point usually shows:

- IEC object similar to `LD0/XCBR1.Pos.stVal`
- value such as `Open` or `Closed`
- device timestamp when `t` is readable
- quality when `q` is readable
- type such as `Dbpos`

## 5. Start the runtime

1. Confirm at least one binding exists.
2. Set MMS polling interval.
3. Enable or disable Fast CB lane as needed.
4. Press **Start**.

The diagnostics tab will show IEC 61850, Modbus, MQTT, and runtime messages.

## 6. Connect Modbus TCP client

Default server:

```text
Address: PC IP running ARServer
Port: 502
Unit ID: 1
```

For FUXA or another HMI:

1. Create a Modbus TCP connection.
2. Enter the ARServer PC IP.
3. Use the configured port and Unit ID.
4. Add tags using the mapped register addresses.

## 7. Enable MQTT

1. Open the MQTT tab.
2. Enable MQTT.
3. Set broker host, port, and topic root.
4. Enable MQTT for selected bindings.
5. Start runtime.
6. Subscribe from your dashboard, test broker, or data collector.

## 8. Save the project

Use **Save Project** after a working mapping is created. The saved project stores IED endpoints, selected IEC objects, Modbus mapping, MQTT settings, and runtime options.

## Fast checklist

- IED reachable by ping or same routed network.
- MMS TCP port is reachable.
- Discovery returns candidates.
- Probe selected signal succeeds.
- Runtime grid shows value.
- Modbus binding is enabled.
- Runtime is running.
- HMI connects to ARServer, not directly to the relay.
