# Backup Agent: backend and portal integration

Written for: CloudOrc backend and portal developers (`Cloude_Website`).

The Backup Agent speaks the same protocol the backend already serves for external devices. The following already work unchanged:
- Activation code (`POST api/v1/agent/enroll`)
- `wss://<host>/agent`
- Session and certificate renewal
- Admission leases and storage grants
- Job, restore and manifest events
- Typed commands

This page lists only what is new or different.

## 1. Identity: one machine, two agents

The Backup Agent is a separate identity with its own device key and enrollment. On a CloudOrc VPS, the server has two endpoints:
- the Control Agent (server management)
- the Backup Agent (backup)

| Where | New field | Values |
|---|---|---|
| `POST api/v1/agent/enroll` body, `endpoint_binding` | `agent_kind` | `"backup"` |
| same | `os_family` | `"windows"`, `"linux"` or `"macos"` |
| same | `architecture` | `"x64"` or `"arm64"` |
| HELLO | `agentKind`, `osFamily`, `osVersion`, `architecture` | same meanings |
| HELLO `capabilities` | `agent.kind.backup`, `inventory.v1`, `workload.postgresql.v1` (when `pg_dump` is installed) | |

**Backend must:**
- Store the `agent_endpoints` row with `kind='external'` (or a new `agent_kind` column) and link a VPS's Backup Agent to the same `vps_servers` row.
- Never send PowerShell or `powershell-exec` commands to a `backup` agent. It rejects them with an ERROR.

A Control Agent that sees the Backup Agent installed advertises `backup.delegated.v1`. It drops `backup.v1` and stops running backup itself.

## 2. Zero-touch install on a CloudOrc VPS (Control Agent command)

```json
{"type":"COMMAND","commandType":"agent.backup-agent.install","commandVersion":"1.0",
 "parameters":{"installId":"inst-123","packageUrl":"https://<downloads>/backup-agent/1.0.0/CloudOrcBackupSetup.exe",
   "sha256":"<64 hex>","sizeBytes":61234567,"enrollmentToken":"ENR-...","apiBase":"https://<api>/"}}
```

- Send it to the **Control Agent** and sign it like every typed command.
- The `enrollmentToken` must be a one-use token for the NEW Backup Agent endpoint of that server. Use `activationCode` instead if preferred.
- The Control Agent downloads the setup, enforces SHA-256 and Authenticode, installs silently without the status icon, then restarts itself.
- Result codes:

| Code | Meaning |
|---|---|
| `BACKUP_AGENT_INSTALL_INVALID` | Parameters failed validation |
| `BACKUP_AGENT_PACKAGE_REJECTED` | Download, size or SHA-256 check failed |
| `BACKUP_AGENT_SIGNATURE_INVALID` | Setup is not validly signed |
| `BACKUP_AGENT_INSTALL_FAILED` | Setup ran but failed |
| `BACKUP_AGENT_INSTALL_TIMEOUT` | Setup did not finish within 15 minutes |
| `UNSUPPORTED_OS` | Not a Windows server |

## 3. Machine inventory ("analyse this machine")

- **Event:** `POST api/v1/agent/events` with `event_type = "agent.inventory"` and `data.inventory` = JSON string of `MachineInventory` (`src/CloudOrc.Agent.Contracts/Backup/InventoryContracts.cs`). It is sent shortly after connecting, then every 12 h, and only when something changed.
- **Command:** `inventory.refresh` returns the same object in the COMMAND_RESULT `data`. Use it for the portal's "Analyse" button.
- **Contents:** OS, CPU, RAM, VM flag, volumes (size and free space), databases, applications, suggested folders and supported workloads. Database engines are PostgreSQL (backup supported) and MySQL, SQL Server and MongoDB (detected only). Applications are Tally, IIS, nginx and Apache.

## 4. Watchdog events

`agent.watchdog.recovered`, `agent.watchdog.restart_failed`, `agent.watchdog.recovery_failed` and `agent.watchdog.memory_restart` arrive through `api/v1/agent/events` with `data.message`. Show them in the device's activity.

## 5. Folder picker and policy paths on Linux and macOS

- `discovery.browse` returns `/` plus mounted data disks as roots. Paths are POSIX (`/var/www`).
- `/proc`, `/sys`, `/dev` and `/run` are refused.
- Policy source paths on Linux and macOS are absolute POSIX paths. Linux paths are case-sensitive.
- `tally` sources are Windows-only.

## 6. PostgreSQL source (new)

```json
{"path":"postgresql://127.0.0.1:5432","kind":"postgresql",
 "database":{"host":"127.0.0.1","port":5432,"databases":[],"excludeDatabases":["test"],
   "username":"postgres","credentialRef":"pg-main","usePeerAuth":false,"includeGlobals":true,"sslMode":"prefer"}}
```

- `databases: []` means every database except templates.
- `usePeerAuth: true` (Linux) runs the tools as the `postgres` OS user and needs no password.
- **The password is never in the policy.** Put it in the storage grant: `"source_secrets": {"pg-main": "<password>"}`. The agent passes it only as `PGPASSWORD` in the tool's environment.
- **Recovery point:** one snapshot per database. `SnapshotRef.sourcePath` = `C:\CloudOrc-Database\postgresql\<host>_<port>\<db>` on Windows, `/cloudorc-database/postgresql/<host>_<port>/<db>` elsewhere. It contains one file, `<db>.dump`. Roles and tablespaces go in `_globals/globals.sql`. Manifest entries use the same paths.
- **Restore:** `restore.execute` with `targetKind:"alternate"` and `targetPath` (a folder). Optionally add `"database":{"mode":"new-database","targetDatabase":"app_restored","connection":{...}}`.
  - `targetKind:"original"` is refused. An existing database is never overwritten.
  - The grant must again carry `source_secrets` for `connection.credentialRef`.

## 7. Self-update per OS

- `agent.update` stays as it is.
- `packageUrl` must point to the package for the endpoint's `osFamily` and `architecture`:
  - **Windows:** `CloudOrcBackupSetup.exe`
  - **Linux and macOS:** `cloudorc-backup-<ver>-<rid>.tar.gz`, where `<rid>` is `linux-x64`, `linux-arm64`, `osx-x64` or `osx-arm64`
- `sha256` is the package hash. The command signature is what makes the hash trustworthy on Linux and macOS.

## 8. Storage

The agent accepts S3-compatible grants only (`provider`: `s3`, `b2` or `wasabi`, with an `s3` section). Google Drive and rclone grants are refused. Keep the legacy Google Drive flow on the Control Agent.

## 9. Portal download page (Devices > Add device)

Show the steps per OS:
1. Download link and SHA-256
2. Activation code
3. Copy-able command

Use these files from the GitHub release:
- `CloudOrcBackupSetup.exe`
- `cloudorc-backup-<ver>-linux-x64.tar.gz` and `-linux-arm64.tar.gz`
- `.deb` and `.rpm`
- `.pkg`
- `get-cloudorc-backup.sh`, served as `/install/backup.sh`
- `install-backup.ps1`, served as `/install/backup.ps1`
- `VERSION`
