<div align="center">

# Support Bridge

### Consent-based remote Windows support for local operators

![Source release](https://img.shields.io/badge/release-source-2563eb?style=flat-square)
![Local first](https://img.shields.io/badge/design-local--first-0f766e?style=flat-square)

**Coded by [zaid.dev](https://github.com/gamerock587)** · **invnty**

C# · .NET · PowerShell

</div>

---

## What it does

- Outbound client connection with explicit consent
- One-time pairing and authenticated operations
- Command execution, bounded file transfers and revocation

## Quick start

**Requirements:** Windows x64 and .NET 10 SDK.

```powershell
powershell -File scripts/build.ps1
python tests/integration.py
```

## Before you run

Use your own HTTPS hostname. The client asks for UAC and explicit ALLOW consent. Every operator token holder has administrator-level remote powers; keep the token local. No customer histories, devices, audit logs or tokens are supplied. No unattended enrollment or multi-operator role isolation is implemented.

## Documentation

See [USAGE.md](USAGE.md) for the original detailed operating guide.

## Privacy and configuration

This source export excludes local credentials, databases, sessions, logs, recordings,
personal spreadsheets and compiled distributions. Supply your own configuration.
The ignore rules help prevent accidental commits; they do not replace reviewing files.

## Contributing

Describe the problem, include reproducible steps with synthetic data, and keep changes focused.
Never include tokens, private recordings, customer data or personal contact lists in an issue.
Validation and remaining limitations are recorded in [RELEASE_REVIEW.md](RELEASE_REVIEW.md).

## License and attribution

Original application code is licensed under [MIT](LICENSE). Third-party components retain
their own licenses and authorship; see THIRD-PARTY-NOTICES.md and any bundled license files.
