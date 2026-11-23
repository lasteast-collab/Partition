# Changelog

## 1.0.0-preview.1

First complete preview of Lasteast Partition: all 11 roadmap phases are implemented. "Preview" because the app has been
tested extensively on virtual disks (every build runs the engine against real VHDX disks as administrator on Windows),
but not yet in the field on many physical machines. Physical disks stay locked until you allow them in Settings › Safety.

**Disk management** — disk maps, create / delete / format / resize / label / drive letter, initialize, MBR↔GPT without
data loss, move partitions (crash-resumable), clone disks or partitions (used-space or sector-by-sector, verified),
OS migration to a new disk (with boot preparation when both disks stay connected). Every change: before/after preview,
typed confirmation, partition-table backup, verification after each step.

**Recovery** — lost-partition scan (quick/deep; NTFS, FAT32, exFAT, NTFS backup boot sector) and restore; deleted-file
recovery from NTFS and FAT32 to another disk; partition-table restore from automatic backups; BCD viewer/export, ESP backup,
boot repair; Rescue Boot ISO builder (uses your own Windows ADK + WinPE); account-recovery guidance using Microsoft's
supported methods only; data backup with verification.

**Health & performance** — NVMe health log, ATA SMART, Windows reliability counters (reported values only), benchmark,
read-only surface test, background monitoring.

**Tools** — Smart Fix ("Scan my computer"), storage analyzer, cleanup, virtual disks, shrink-blocker analysis, BitLocker and
Storage Spaces overview, technician reports (HTML/CSV/JSON), assistant (optional AI planner, never executes), command line
with dry-run, portable mode, signed licenses (safety features never gated), 7 languages including Hebrew and Arabic (RTL).

Known limitations are listed honestly in docs/STATUS.md. The installer is not code-signed yet, so Windows SmartScreen may warn.
