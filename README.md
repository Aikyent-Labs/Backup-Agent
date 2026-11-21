# CloudOrc Backup Agent

A standalone backup agent for **any machine**: Windows (server and desktop), Linux and macOS. It is a separate product from the CloudOrc Server Agent (Control Agent). It installs, updates and uninstalls on its own, has its own identity, and ships with its own watchdog.

Customers and partners download it from the portal and activate it with a code. On a CloudOrc VPS, the Server Agent can install it zero-touch with the `agent.backup-agent.install` command.

Everything is managed from the CloudOrc portal: what to back up, schedule, retention and restore. The machine itself only shows activation and status.

## What it backs up

| Workload | Windows | Linux | macOS | How |
|---|---|---|---|---|
| Files / folders | yes (VSS snapshot) | yes (live read) | yes (live read, needs Full Disk Access) | Kopia, incremental, deduplicated, AES-256 encrypted on the machine |
| Tally | yes | n/a | n/a | VSS + Tally process detection |
| PostgreSQL | yes | yes | yes | `pg_dump --format=custom` streamed straight into Kopia (no temp file); globals via `pg_dumpall --globals-only`; restore into a NEW database only |

## It never blocks the machine

| Control | Windows | Linux | macOS |
|---|---|---|---|
| Hard CPU / memory cap per job | Job Object | cgroup v2 (`Delegate=yes`, a child cgroup per job) | (no hard cap) |
| Low CPU + disk priority | BELOW_NORMAL / IDLE priority class | `nice 10/19` + `ionice best-effort 7 / idle`, from the first instruction | `taskpolicy -b` (background QoS) |
| Backs off when the machine is busy | Performance counters | `/proc/stat`, `/proc/diskstats`, `/proc/meminfo` | load average |
| Protected applications (e.g. Tally) | yes | yes | yes |
| Agent footprint | ~70 MB RSS idle, delayed-auto start | ~70 MB RSS idle, `Nice=5`, `CPUWeight=50` | Background process type, low-priority IO |
| Upload limit | from the policy / admission lease | same | same |

Antivirus friendliness:
- Plain (not single-file, not packed) .NET binaries.
- Authenticode-signed setup (needs a PFX in CI).
- Engine binaries are SHA-256 pinned.
- No script or shell execution path at all.
- No Defender exclusions are added.

## Layout

```
{install}/bin/       cloudorc-backup (service + CLI), cloudorc-backup-worker, -watchdog, -updater
                     + ONE shared self-contained .NET 10 runtime (no .NET install needed on the machine)
{install}/engines/   kopia + engines.manifest.json (SHA-256 pinned)
```

| OS | Install | Data (identity, state, logs) | Services |
|---|---|---|---|
| Windows | `C:\Program Files\CloudOrc\Backup` | `C:\ProgramData\CloudOrc\Backup` (SYSTEM + Administrators) | `CloudOrcBackupAgent`, `CloudOrcBackupWatchdog` |
| Linux | `/opt/cloudorc-backup` | `/var/lib/cloudorc-backup` (root, 0700) | `cloudorc-backup-agent.service`, `cloudorc-backup-watchdog.service` |
| macOS | `/Library/CloudOrc/Backup` | `/Library/Application Support/CloudOrcBackup` | `com.cloudorc.backup.agent`, `com.cloudorc.backup.watchdog` |

## Install

**Windows (GUI):** run `CloudOrcBackupSetup.exe` and paste the enrollment token from the portal (Backup > Computers > Add device).

**Windows (command line, elevated):**

```
CloudOrcBackupSetup.exe /VERYSILENT /SUPPRESSMSGBOXES /NORESTART /TOKEN=CLB1.xxxx [/COMPONENTS=agent]
```

Or the PowerShell one-liner:

```
& ([scriptblock]::Create((irm https://<portal>/install/backup.ps1))) -Token CLB1.xxxx -DownloadBase https://<downloads>/backup-agent/<ver>
```

Setup exit codes:

| Code | Meaning |
|---|---|
| 0 | Success |
| 10 / 11 | A service did not start |
| 21 | Installed and running, but not activated (wrong or expired code, or bad address). Activate later with `cloudorc-backup activate <CODE> --api-base <ADDRESS>`. |

**Linux:**

```
curl -fsSL https://<portal>/install/backup.sh | sudo bash -s -- --token CLB1.xxxx --download-base https://<downloads>/backup-agent/<ver>
# or: sudo apt install ./cloudorc-backup_<ver>_amd64.deb   /   sudo dnf install ./cloudorc-backup-<ver>.x86_64.rpm
sudo cloudorc-backup activate CLB1.xxxx
```

**macOS:** open `CloudOrcBackup-<ver>-osx-arm64.pkg`, then run `sudo cloudorc-backup activate CLB1.xxxx`. Grant **Full Disk Access** to `bin/cloudorc-backup-worker` and `engines/kopia` (or push a PPPC profile through MDM), otherwise protected folders such as Documents and Desktop cannot be read.

## Command line

```
cloudorc-backup activate <CODE> [--api-base https://...]   activate (root / Administrator)
cloudorc-backup status [--json]                            asks the running service
cloudorc-backup diagnostics                                support info, no secrets
cloudorc-backup restart-service | set-activation | enroll --token ENR-... | --version
```

## Build and test

```
dotnet test CloudOrc.BackupAgent.sln                        # Windows (includes the status icon)
dotnet test CloudOrc.BackupAgent.CrossPlatform.slnf         # Linux / macOS
bash scripts/package.sh linux-x64 1.0.0                     # -> dist/cloudorc-backup-1.0.0-linux-x64.tar.gz (+ .sha256)
pwsh scripts/package-windows.ps1 -Version 1.0.0             # -> dist/CloudOrcBackupSetup.exe (needs Inno Setup 6)
bash installer/macos/build-pkg.sh osx-arm64 1.0.0           # on macOS -> .pkg
```

`.github/workflows/release.yml` runs the tests on every push to `main`, and builds, tests and packages every OS (GitHub pre-release) on a `backup-v*` tag.

Verified on 19 Nov 2026:

| Check | Result |
|---|---|
| Windows tests | 549 pass |
| Linux tests (Ubuntu 24.04, WSL) | 434 pass, 32 Windows-only tests skipped |
| Linux package | built and run as root; status over the local socket works; non-root access refused; ~69 MB idle |

| End to end on Windows 11 (local backend) | activation code -> connected -> inventory -> folder picker -> policy -> Backup now (verified recovery point) -> restore to a new folder; all restored SHA-256 hashes match |

The agent also restarts a stopped Backup Watchdog (checked every 5 minutes), and `activate` starts it too.

The macOS build and `.pkg` are untested: they need a Mac. The Windows `setup.exe` was not compiled locally because Inno Setup is not installed on this machine; CI installs it.

See `docs/BACKEND_INTEGRATION.md` for what the CloudOrc backend and portal must send and accept.
