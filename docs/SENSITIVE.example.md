# Sensitive values (template)

> Parent: [CONVENTIONS](CONVENTIONS.md) · Root: [README](../README.md)

Committed docs use `<PLACEHOLDER>` tokens instead of machine-specific or private values. The real values live in `docs/SENSITIVE.md`, which is listed in `.gitignore` and exists only on machines that need it. To create it, copy this file to `docs/SENSITIVE.md` and fill in the **Value** column.

Put only identifiers here (addresses, account names, IDs, private links). Do **not** store passwords, tokens, API keys or recovery keys in either file. Keep those in the owner's encrypted store, e.g. the per-VM `backups/critical/admin-credentials.enc`.

When a doc needs a new sensitive value, add a placeholder row here, put the real value in the local `SENSITIVE.md`, and write only the placeholder (in backticks) in the doc.

| Placeholder | Value | Meaning | Used in |
|---|---|---|---|
| `<OWNER_USER>` | | Host user that runs the VMs; also the standard Windows account in both VMs | vm/* |
| `<VM_ADMIN>` | | Separate Windows administrator account in both VMs | vm/*, evidence/F00 |
| `<HOST_LAN_IP>` | | Host LAN address of the RDP source-filtering forwarder | vm/* |
| `<LAN_SUBNET>` | | Only source range the LAN RDP forwarder accepts | vm/* |
| `<DENIED_TEST_IP>` | | Out-of-range source used in the forwarder rejection test | vm/WINDOWS-VM-DEPLOYMENT |
| `<RDP_PORT_DEV>` | | RDP port for `win11-dev` | vm/* |
| `<RDP_PORT_CLEAN>` | | RDP port for `win11-clean` | vm/* |
| `<VM_UUID_DEV>` | | QEMU VM UUID of `win11-dev` | vm/WINDOWS-VM-OPERATIONS |
| `<VM_UUID_CLEAN>` | | QEMU VM UUID of `win11-clean` | vm/WINDOWS-VM-OPERATIONS |
| `<VM_MAC_DEV>` | | Virtual NIC MAC of `win11-dev` | vm/WINDOWS-VM-OPERATIONS |
| `<VM_MAC_CLEAN>` | | Virtual NIC MAC of `win11-clean` | vm/WINDOWS-VM-OPERATIONS |
| `<DATA_FS_UUID>` | | Filesystem UUID of the host `/data` volume | vm/WINDOWS-VM-OPERATIONS |
| `<DESIGN_CANVAS_URL>` | | Private online design canvas | design/*, product/PLAN |
