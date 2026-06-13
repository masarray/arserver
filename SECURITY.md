# Security Policy

ARServer is an engineering gateway tool for Windows lab, FAT/SAT, and controlled evaluation environments.

## Supported versions

Security fixes are handled on the latest public release and the default branch.

## Reporting a security issue

Please do not open a public issue for sensitive security reports.

Use a private contact channel when available, or open a minimal public issue asking for a secure contact path without disclosing exploit details.

## Safe disclosure guidelines

Do not include:

- relay credentials;
- private IP addressing plans;
- VPN information;
- customer names;
- confidential SCL/CID/SCD files;
- full network diagrams;
- screenshots that expose operational assets.

## Deployment responsibility

Before connecting ARServer to operational or customer networks, validate:

- network segmentation;
- firewall rules;
- Windows host hardening;
- account and file permissions;
- broker security for MQTT;
- Modbus TCP exposure boundaries;
- change-management approval.

ARServer rejects Modbus write functions by design, but network exposure and host security still need proper engineering control.
