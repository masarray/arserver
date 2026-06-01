# ARServer

ARServer is a Windows desktop gateway for publishing IEC 61850 MMS data as a clean Modbus TCP server for HMI/SCADA tools such as FUXA.

The project is built for substation and relay-bench workflows where operators need a readable IEC 61850 explorer, a deterministic Modbus map, and diagnostics that separate real communication failures from normal polling noise.

## What It Does

- Connects to IEC 61850 MMS relays using a real libiec61850-based adapter when the runtime DLLs are available.
- Provides a mock IEC 61850 mode for UI, mapping, and Modbus gateway testing without a relay.
- Imports SCL/CID/SCD files and helps select SCADA-ready signals.
- Builds Modbus TCP bindings for coils, discrete inputs, input registers, and holding registers.
- Runs a read-only Modbus TCP server for HMI polling.
- Shows runtime diagnostics, IEC activity, Modbus polling status, stale values, and per-signal quality.

## Current Scope

ARServer is currently a WPF/.NET 8 Windows application focused on IEC 61850 MMS polling and Modbus TCP publishing.

The UI already supports a multi-IED workspace model, but the runtime architecture should still be treated as a careful field tool in active development. Validate mappings and communication behavior on a test bench before connecting it to operational environments.

## Requirements

- Windows 10 or later
- .NET 8 SDK for building from source
- Optional real IEC 61850 runtime DLLs copied beside the built executable:
  - `iec61850dotnet.dll`
  - `iec61850.dll`

Without those DLLs, ARServer can still run in mock mode for mapping and Modbus TCP testing.

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

1. Copy the libiec61850 .NET/native DLLs beside `ArServer.exe`.
2. Start ARServer.
3. Add or connect an IED by IP address and MMS port, usually TCP `102`.
4. Select SCADA/HMI signals.
5. Build or validate the Modbus map.
6. Start the Modbus TCP server and point the HMI client to the PC IP, configured port, and Unit ID.

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
- `Services/ModbusTcpServer.cs` implements the read-only Modbus TCP server.
- `Services/RealLibIec61850Client.cs` adapts libiec61850.NET through reflection.
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

ARServer is open source under the GNU General Public License v3.0 or later. See [LICENSE](LICENSE).
