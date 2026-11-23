# Changelog

## Unreleased

**Recovery**
- File Recovery reads volumes whose start is damaged through the file system's backup boot sector — in memory only,
  nothing is written — so files can be copied off with their names before any repair. "Copy files…" on unreadable
  partitions in Partition Recovery. A damaged MFT record 0 is read from $MFTMirr.
- NTFS: very fragmented files (attribute lists / extension records) and NTFS-compressed files (LZNT1) are now
  recovered; previously they were listed as unrecoverable.

## 1.0.0-preview.2

**Fixes**
- The main window now fits the screen: on 1366×768 laptops (or with high display scaling) it opened larger than the
  screen with its title bar and close button out of reach. It now opens maximized on small screens and centred otherwise.
- Smart Fix gave the wrong advice for RAW drives (chkdsk cannot run on them). Advice now depends on Windows' actual
  error: RAW, damaged, BitLocker-locked, read errors; empty card readers are no longer reported.
- Raw disk access without administrator rights now says so instead of "Access is denied".
- Removed leftover "planned for Phase N" texts; those findings open the pages that exist now.

**Repair a damaged boot sector** — Partition Recovery lists partitions that are in the table but unreadable (RAW) and
checks their boot sector in seconds. When the file system's own backup is valid (NTFS: last sector, verified against
the MFT; FAT32: sectors 6–8; exFAT: checksummed backup boot region), the repair is queued like any other operation:
preview, typed confirmation, the replaced sectors saved, write, read-back, and Windows must mount the volume afterwards.
CLI: `bootsector <disk> <offset>`, `op <disk> repair-boot-sector offset=… fs=…`.


**Recovery**
- exFAT deleted-file recovery (USB sticks, SD cards, camera cards), including files in deleted folders.
- "Search by file content" (file carving) finds photos, videos, PDF and Office files after a format or on unreadable file
  systems; it is also used automatically when no file system is recognised. Names and folders cannot be recovered this way.
- Disk-image files as a recovery source: raw images and fixed VHDs are read directly, VHDX and dynamic VHDs are attached
  read-only. Whole-disk sources for searching when the partition table is gone.
- Command line: `undelete --by-content`, `undelete-image`.

**Disk management**
- Partition attributes: set active (MBR), hide / unhide, change partition type; initialize a blank disk as GPT or MBR.

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
