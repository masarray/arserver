# ARServer Troubleshooting

This page lists common setup and runtime issues when using ARServer as an IEC 61850 MMS to Modbus TCP and MQTT gateway.

## The app starts, but real IED communication is not available

ARServer can run in mock mode without external runtime files. For real IEC 61850 MMS communication, the required runtime components must be placed beside `ArServer.exe`.

Check:

- the runtime files are in the same folder as `ArServer.exe`;
- Windows did not block downloaded DLL files;
- the x64/x86 architecture matches the application package;
- the runtime version supports the methods expected by ARServer;
- the application was restarted after copying the runtime files.

## Cannot connect to IED on port 102

Check:

- IP address is reachable using `ping` where allowed by the network;
- TCP `102` is not blocked by firewall rules;
- the relay MMS server is enabled;
- another engineering tool is not holding the MMS association;
- VLAN, routing, and subnet settings are correct;
- Windows Defender Firewall allows ARServer on the selected network profile.

## Modbus client cannot connect

Check:

- Modbus TCP output is enabled before runtime starts;
- the configured port is not already used by another program;
- the Modbus client uses the correct IP address and port;
- the Unit ID matches the ARServer setting;
- the client is reading the correct register area and address base.

## Values are stale or slower than the configured polling time

The MMS polling value is a scheduler target, not a guaranteed device response time.

Actual refresh depends on:

- IED response time;
- number of selected points;
- number of active IED workspaces;
- network latency and retransmission;
- timeout/retry behavior;
- Windows scheduling load.

Use **Fast CB** for breaker/status evaluation and keep the selected point count small when testing 10–50 ms acquisition.

## MQTT topics do not update

Check:

- the MQTT broker is running;
- host, port, username, password, TLS, and client ID are correct;
- Windows firewall allows outbound broker connection;
- selected rows are enabled for MQTT publishing;
- the topic root is correct;
- the subscriber uses the same topic path and wildcard strategy.

## Float or register value looks wrong in HMI

Check:

- Modbus area: input register vs holding register;
- address base: zero-based vs human display addressing;
- word order for Float32;
- scale and engineering unit;
- whether the HMI is reading one register or two registers for 32-bit values.

## The release ZIP is blocked by Windows

After downloading a ZIP from the internet, Windows may mark files as blocked.

Try:

1. Right-click the ZIP file.
2. Open **Properties**.
3. Tick **Unblock** if available.
4. Extract again.

## Good issue report checklist

When opening a GitHub issue, include:

- ARServer version;
- Windows version;
- whether the issue occurs in mock mode;
- steps to reproduce;
- screenshot or log excerpt;
- sanitized signal/reference examples.

Do not include private substation files, credentials, public IP addresses, customer names, or confidential relay settings.
