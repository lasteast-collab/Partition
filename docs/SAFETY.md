# Safety design

Data safety is the highest priority. These are the rules every phase must follow.

Phase 2 status: rules 1–4, 6–9 are implemented and unit-tested with a fake disk (54 tests). Rule 5 (lock/dismount)
is delegated to the Windows Storage Management API, which refuses to format a volume in use (`Force=false`).
Additional Phase 2 safeguards: physical disks are off-limits until enabled in Settings › Safety; the app window
refuses to close while operations run; every attempt — including refused ones — is written to `history.jsonl`.

## Already enforced (Phase 1)

- Query-only device handles (`dwDesiredAccess = 0`). The detection layer is incapable of modifying a disk.
- System-critical partitions (ESP, MSR, Recovery, the Windows volume, the firmware system partition, MBR active) are identified and marked with a shield in the UI. This flag is the input to future protection checks.
- Corrupt partition tables (overlaps, partitions past the end) are surfaced as Critical findings with "do not modify this disk until investigated".
- Health is never guessed. Read errors are shown, not hidden.
- Sample data is impossible to confuse with real disks (`--demo` only, orange banner, `IsSampleData` flag).
- Unhandled UI exceptions are logged and reported, and the message states that no disk was changed.

## Operation engine contract (enforced since Phase 2 by `GuardedExecutionEngine`)

1. **Plan, don't act.** UI and AI produce `OperationPlan`s (pure data). Only the engine executes, and only after explicit confirmation.
2. **Validate against a fresh snapshot.** Before Apply: re-read the layout and abort if it differs from the plan's base snapshot (disk identity = number + GPT disk GUID/MBR signature + serial + size).
3. **Simulate first.** Every plan is applied to an in-memory copy of the layout (`DiskMapLayout`) to produce the before/after preview and to detect overlaps, out-of-range, and alignment issues.
4. **Back up metadata.** Before any write: save MBR/GPT (primary and backup headers + entries) to a different physical disk when available, else to the app data folder with a warning. Metadata backups are never described as data backups.
5. **Lock and dismount** volumes through the documented FSCTLs; refuse if a lock cannot be obtained.
6. **Refuse unsupported operations.** BitLocker-locked, dynamic, Storage Spaces and unknown file systems are read-only until explicitly supported.
7. **Never silently destroy.** Destructive steps require typed confirmation for system disks and show disk number, model, size and serial.
8. **Honest undo.** Pending operations can be undone; executed destructive operations are never presented as undoable.
9. **Test on virtual disks first.** VHDX fixtures (GPT/MBR, 512/4Kn, NTFS/FAT32/exFAT) created by the test harness; destructive tests never target a physical disk.

## Module 39 (Rescue Boot & Windows account recovery)

Implemented exactly as the specification constrains it: guidance and supported Microsoft paths only
(Microsoft account recovery, password reset disk, another administrator, domain admin, Reset this PC/reinstall), plus a backup wizard for accessible, unencrypted data.
Lasteast will **not** include offline password removal, SAM/registry editing, credential extraction, or any bypass of Microsoft-account, domain or BitLocker protection.
The rescue environment will be built on Windows PE via the Windows ADK under its licence terms.
