# Troubleshooting

## IED does not connect

Check these items first:

- IP address is correct.
- MMS port is correct, usually `102`.
- PC and IED are in the same routed network.
- Windows firewall allows ARServer outbound access and Modbus inbound access when used.
- IED MMS service is enabled.
- Another engineering tool is not exhausting available client sessions.
- VLAN, gateway, subnet mask, and switch port are correct.

## Discovery connects but returns few or no signals

Possible causes:

- The IED restricts online model browsing.
- The selected access point is not the MMS access point.
- The IED model exposes object names in a vendor-specific shape.
- The relay allows reading known objects but limits directory browsing.

Recommended action:

- Try Open SCL/CID/SCD if available.
- Check diagnostics for domain and variable browse messages.
- Probe a known CB position or measurement from SCL.

## Probe selected fails

Probe validates the final object path and functional constraint. Failure may mean:

- object path differs from the discovered candidate,
- functional constraint is wrong,
- the relay does not allow the attribute read,
- the point is not present in this IED variant,
- the connection was closed by the IED after a malformed or unsupported request.

Try another candidate from the same logical node, or use SCL import for more deterministic object references.

## Value is live but timestamp is blank

The value attribute may be readable while the companion `t` timestamp attribute is not present or not allowed. ARServer leaves timestamp blank rather than generating a device timestamp.

## Value is live but quality is blank or Bad

Quality comes from the companion `q` attribute when available. If the relay returns an invalid or unavailable quality, ARServer shows that state.

## Modbus client connects but values do not change

Check:

- Runtime is running.
- The IEC value is live in the runtime grid.
- The binding is enabled.
- The Modbus register address is correct.
- The client uses the correct Unit ID.
- The client uses the same zero-based or one-based address convention as your mapping.
- The client reads the correct data type size.

## MQTT publishes nothing

Check:

- MQTT is enabled globally.
- MQTT is enabled for the selected binding.
- Broker host and port are correct.
- Broker accepts anonymous or configured credentials.
- Network/firewall allows broker access.
- Diagnostics tab shows MQTT connected.

## Runtime is blocked

ARServer blocks runtime when it cannot create a safe live IEC session. This avoids publishing stale or invented values.

Fix the IEC connection first, then start runtime again.

## When to use SCL import

Use SCL/CID/SCD/ICD when:

- IP discovery returns too many candidates,
- online browse is restricted,
- you need DataSet or RCB planning,
- you want a repeatable engineered mapping,
- the relay object structure is known from the project file.

## What information to include in a bug report

Include:

- ARServer version,
- Windows version,
- relay vendor/model/firmware when shareable,
- whether the workflow used Add by IP or Open SCL,
- screenshot of diagnostics,
- selected IEC object,
- expected value and observed value,
- Modbus/MQTT settings if the issue is output-related.
