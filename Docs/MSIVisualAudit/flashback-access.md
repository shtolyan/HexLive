# Flashback: explicit CA bundle on MSI

Use the project client's dedicated transport wrapper when Python's default
certificate chain fails on this machine:

```powershell
python -X utf8 Tools/bugs_with_ca.py --ca-file C:/Users/shtolyan/HexLiveSourceReference/20261011/flashback-ca-bundle.pem whoami
python -X utf8 Tools/bugs_with_ca.py --ca-file C:/Users/shtolyan/HexLiveSourceReference/20261011/flashback-ca-bundle.pem queue
```

The public CA bundle was delivered by Sveta; it is outside Git. The wrapper
passes an explicit `ssl.create_default_context(cafile=...)` to its own requests.
Hostname, certificate-chain and validity checks stay enabled. Missing or invalid
CA files fail closed. It does not modify global SSL state or the shared client.

CLI commands, revision checks, commit uploads and credential selection remain
those of `.agents/skills/hexlive-bug-tracker/scripts/bugs.py`. The credential
reader uses this device's existing local file; no token is copied or printed.
Run `whoami` before writes and read each card fresh before updating its revision.

Verified 2026-10-11 using both installed Python and Blender 5.2's Python:
authenticated identity `MSI ноутбук`; new/rework queue empty. Fresh card reads:

| Assignment | Flashback | Owner/status |
|---|---|---|
| HV-001 hair | #431 | codex-msi-visual-bugs-20261011 / in_progress |
| HV-003 normals | #432 | codex-msi-visual-bugs-20261011 / in_progress |
| HV-004 bandages | #433 | codex-msi-visual-bugs-20261011 / in_progress |
| HV-005 portrait | #434 | codex-msi-visual-bugs-20261011 / in_progress |

Emoji remains assigned to Mac. These are observation-time statuses, not a queue
snapshot to use instead of fresh Flashback reads.
