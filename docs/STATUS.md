# Implementation status

Legend: ✅ implemented · 🟡 partial · ⏳ planned (phase) · 🚫 will not be built (see note)

> **Build verification.** Every push is built and tested on GitHub Actions (`windows-latest`): the full solution
> including the WPF app, 89 unit tests, and integration tests that run the real engine as administrator against
> freshly created VHDX virtual disks (create/format/resize/label/letter, MBR↔GPT conversion, partition move,
> disk clone) and read the data back through Windows afterwards. Nothing in CI touches a physical disk.

## Phase 5 — Conversion, move, clone  (this build)

| Feature | Status | Notes |
|---|---|---|
| MBR → GPT without data loss | ✅ | Partitions keep their sectors; backup GPT written first, then header + protective MBR (crash order). Refuses extended/logical, partitions overlapping GPT areas, unknown types |
| GPT → MBR without data loss | ✅ | ≤ 4 partitions, below 2 TB; MSR dropped (warned); GPT copies zeroed after the MBR is written |
| Conversion of the running Windows disk | 🚫 | Needs a firmware switch as well; the app points to Microsoft's MBR2GPT instead of half-doing it |
| Move a partition (left/right, overlapping or not) | ✅ | Chunk ≤ shift distance so every chunk copy is idempotent; write-through JSON journal → resumes after power loss; NTFS/FAT hidden-sectors patched (incl. backup boot sectors); volume locked + dismounted |
| Move the Recovery partition | ✅ | `reagentc /disable` before, `/enable` after |
| Move the running Windows volume / active boot partition | 🚫 | Impossible from inside Windows; needs the rescue environment (Phase 8) |
| Move exFAT / ReFS partitions | ⏳ | Refused for now (their own position records are not patched) |
| Disk clone: sector-by-sector or used-space (NTFS bitmap), whole disk or chosen partitions | ✅ | Same offsets; GPT rebuilt for the destination size; optional new disk/partition IDs |
| Clone source consistency | 🟡 | Unused volumes are locked; volumes in use (C:) are read from a VSS snapshot — the VSS path compiles but CI only exercises the locked path |
| Clone verification (SHA-256 re-read) | ✅ | |
| Clone wizard with compatibility check (module 11) | ✅ | Uses the same simulator as the engine |
| Disk Conversion page; Move / Convert actions in the editor | ✅ | |
| Clone to a smaller disk by shrinking partitions; clone extended/logical MBR | ⏳ | Shrink the source first; refused with a clear message |

## Phase 4 — Storage Analyzer & Cleanup

| Feature | Status | Notes |
|---|---|---|
| Scan a drive or any folder (read-only, cancellable, progress) | ✅ | Never follows links/junctions/cloud placeholders; honours exclusions; bounded memory |
| Treemap with drill-down (disk → folder → subfolder → file), breadcrumb, Up | ✅ | Squarified layout, unit-tested for exact area and no overlap |
| Largest files / largest folders / old files / recently changed / empty folders | ✅ | Filters: minimum size, extensions |
| File types by category (videos, images, audio, documents, archives, apps, games, temporary, system) and top extensions | ✅ | |
| Duplicate finder (size → 64 KiB prefix hash → full SHA-256) | ✅ | Files ≥ 1 MB; nothing deleted |
| Show in File Explorer | ✅ | |
| Move a single file to the Recycle Bin | ✅ | Yes/No confirmation (default No); refuses Windows, Program Files, ProgramData, system/linked files, and files changed since the scan |
| Cleanup: user temp, Windows temp, crash dumps, error reports, legacy IE cache, Delivery Optimization cache | ✅ | Scan → preview files → confirm → remove; admin-only targets disabled without elevation |
| Cleanup safety: Recycle Bin by default, re-check every file (inside root, not linked, unchanged, not excluded), in-use files skipped | ✅ | Tested with real files incl. symlink escape attempts |
| Exclusion list shared by analyzer and cleanup | ✅ | |
| Smart Fix "Open Cleanup / Storage Analyzer" fixes | ✅ | |
| Windows Recycle Bin removal (`SHFileOperation`, `FOF_ALLOWUNDO`) | 🟡 | Compiles; not yet run on Windows |
| Browser caches (Chrome/Edge/Firefox), Windows Update cleanup, component store | ⏳ | Need browser-closed detection / DISM integration |
| Shrink-blocker detection (page file, hibernation, shadow copies, unmovable files) | ⏳ P10 | File Operations module |

## Phase 3 — Smart Fix

| Feature | Status | Notes |
|---|---|---|
| "SCAN MY COMPUTER" button on the dashboard + Smart Fix page | ✅ | |
| Report grouped Critical / High / Recommended / Optional / Healthy | ✅ | Each item: problem, explanation, recommendation, benefit, risk, backup advice |
| Checks: C: space, other volumes' space, partition-table consistency, alignment, unreadable volumes, unallocated space, MBR > 2 TB, TRIM, dynamic disks, EFI partition, recovery partition, temp files, Recycle Bin, hibernation file, elevation | ✅ | Read-only measurements (`WindowsSystemProbe`, time-boxed) |
| "Fix now" → pending queue → preview → confirm → engine → verify | ✅ | Smart Fix never executes anything itself |
| One-click C: expansion (module 9) into adjacent unallocated space | ✅ | Max size computed; validated by the simulator |
| C: expansion when the Recovery partition is in the way | ✅ P5 | One click queues "move Recovery to the end" + "extend C:"; other blocking partitions are explained only |
| Disk health / SMART in the scan | ⏳ P9 | Listed as "not measured yet", never guessed |
| BitLocker status, file-system dirty bit, WinRE status | ⏳ P8–P10 | Need elevation / APIs not yet integrated |
| Cleanup actions for temp files and duplicates | ✅ P4 | Via Cleanup / Storage Analyzer pages |

## Phase 2 — Basic partition operations

| Feature | Status | Notes |
|---|---|---|
| Operation model (create, delete, format, resize/extend/shrink, label, drive letter) | ✅ | Partitions addressed by offset, not number |
| Layout simulator + validation rules | ✅ | Protected partitions, alignment, overlap, MBR 4-primary & 2 TB, GPT 128, FAT32 32 GB, label limits, letter conflicts, shrink floor, adjacent-space extend |
| Pending operation queue: add (validated), remove, undo last, clear, re-validate on refresh | ✅ | Invalid operations never enter the queue; stale ones block Apply |
| Before / after disk maps per disk, data-loss and time summary | ✅ | Pending page |
| Inline operation editor with live validation | ✅ | Details panel on Overview |
| Typed confirmation (`APPLY`) + backup acknowledgement | ✅ | |
| Guarded execution engine | ✅ | Elevation, fingerprint match, re-simulation, backup-before-write, verify after every step, stop on first problem, cancel between steps only |
| Partition-table backup (head/tail 1 MiB, EBRs, layout, SHA-256 manifest, read-back verified) | ✅ | Warns when stored on the changed disk |
| Windows executor: Storage Management API (MSFT_Disk/Partition/Volume) + Win32 mount points/labels | ✅ | Verified in CI against real VHDX disks |
| Virtual-disks-only policy by default; opt-in for physical disks | ✅ | Settings › Safety |
| Operation history (JSON lines, includes refused attempts) | ✅ | `%LOCALAPPDATA%\Lasteast\Partition\history.jsonl`; SQLite deferred (no NuGet in authoring env) |
| VHDX test bed script | ✅ | `tools/vhdx-testbed.ps1` |
| Window cannot be closed while operations run | ✅ | |
| Move partition | ✅ P5 | |
| Merge, split, copy, hide, set active, type ID, cluster-size change | ⏳ | |
| Drag-and-drop resizing on the map | ⏳ P3 | Editor uses typed sizes for now |
| Restore partition table from backup | ⏳ P3 | Backups are written and verified; restore UI not built yet |
| Initialize disk (RAW → GPT/MBR) | ⏳ P3 | |

## Phase 1 — Foundation

| Feature | Status | Notes |
|---|---|---|
| Solution architecture (UI / presentation / engine / detection separated) | ✅ | See ARCHITECTURE.md |
| Branding: penguin logo, app icon, logo in top bar / empty states / about | ✅ | Emblem extracted from the provided logo; .ico 16–256 px |
| Splash screen, welcome screen, installer artwork | ⏳ P11 | |
| Disk detection (internal, NVMe, SATA, USB, VHD/VHDX, removable) | ✅ | Native IOCTLs, read-only |
| Disk info: number, model, serial (masked), firmware rev, size, sector sizes, GPT/MBR, disk ID, bus, SSD/HDD, TRIM, removable | ✅ | |
| Partition info: number, offset, size, GPT type/ID/name/attributes, MBR type/active, role, hidden, alignment | ✅ | |
| Volume info: letter, mount points, label, FS, capacity, used/free, cluster size, read-only | ✅ | |
| Windows disk / firmware boot partition identification | ✅ | `%SystemRoot%` + `HKLM\SYSTEM\Setup\SystemPartition` |
| UEFI vs BIOS detection | ✅ | `GetFirmwareType` |
| Dynamic disk detection | 🟡 | Detected and shown read-only; LDM volume layout not decoded |
| Interactive graphical disk maps | ✅ | Proportional, min-width, selection, used bar, unallocated hatching, protected markers |
| Dashboard: totals, SSD/HDD/external counts, Windows disk, firmware, findings | ✅ | |
| All Disks page (properties) / All Partitions page (sortable grid) | ✅ | |
| Fact-based findings (low space, misalignment, MBR > 2 TB, unallocated, layout problems, TRIM off, unreadable volume) | ✅ | Read-only seed of Smart Fix |
| Auto refresh on device arrival/removal | ✅ | Debounced `WM_DEVICECHANGE` |
| Administrator detection + on-demand elevation | ✅ | Manifest `asInvoker` |
| Dark / Light / follow-Windows themes, 6 accent colours, dark title bar | ✅ | |
| 7 languages (EN, HE, AR, FR, ES, DE, RU), live switching, full RTL | ✅ | 242 keys each, placeholder-checked |
| Localized numbers and units | ✅ | |
| High-DPI / 4K (PerMonitorV2) | ✅ | |
| Keyboard access (F5, tab order, focus borders) & automation names | 🟡 | Basic; full screen-reader pass planned for Phase 11 |
| Settings persistence, logging | ✅ | JSON + daily text log; operation history in JSONL |
| CLI `inventory [--json]` | 🟡 | Inventory only; the rest is Phase 10 |
| Drag-and-drop resizing | ⏳ P3 | |
| SMART / temperature / health status | ⏳ P9 | Shown as "not measured" — never guessed |
| BitLocker status | ⏳ P10 | Locked volumes currently appear as unreadable with a finding |
| Smooth animations | 🟡 | Popup fades only |

## Later phases

| Module (spec) | Phase | Status |
|---|---|---|
| Partition operations: create, delete, format, resize, extend, shrink, label, letter (M4) | 2 | 🟡 built; hardware validation pending |
| Operation queue, before/after simulator, validation, Apply, history (M6, M28) | 2 | ✅ (SQLite → JSONL for now) |
| Merge, split, copy, hide/unhide, set active, type ID, cluster size, wipe, align (M4) | 2–3 | ⏳ |
| Disk conversion MBR↔GPT, FAT32↔NTFS, basic↔dynamic (M26) | 2 | ⏳ |
| Smart Fix "Scan my computer" with fixes (M3) | 3 | 🟡 detection-only findings exist |
| One-click C: expansion wizard (M9) | 3 | ⏳ |
| Storage analyzer treemap, large/duplicate/old files, cleanup (M7, M21) | 4 | ⏳ |
| File operations, shrink-blocker detection (M23) | 4 | ⏳ |
| Disk/partition cloning, compatibility check, verification (M10, M11) | 5 | ⏳ |
| Windows OS migration (M12) | 6 | ⏳ |
| Partition & file recovery (M13) | 7 | ⏳ |
| Boot repair, EFI manager, boot entry manager (M15, M16) | 8 | ⏳ |
| Rescue Boot media creator on Windows PE (M14, M39) | 8 | ⏳ |
| SMART/NVMe health monitor, benchmarks, surface scan (M19, M20, M22) | 9 | ⏳ |
| Virtual disk manager, BitLocker assistant, Storage Spaces info (M17, M18, M25) | 10 | ⏳ |
| Technician mode, reports, full CLI with dry-run (M24, M30) | 10 | ⏳ |
| Metadata backup & restore (M29) | 2–3 | 🟡 backup ✅, restore ⏳ |
| Optional AI assistant (M8, M31) | 10 | ⏳ (plans only; never executes; works on real data only) |
| Licensing Free / Pro / Technician (M34) | 11 | ⏳ (safety features never gated) |
| Installer, signing, compatibility testing (M37 P11) | 11 | ⏳ |
| Windows account recovery — guidance and supported Microsoft methods, backup wizard (M39 §5–7) | 8 | ⏳ |
| Offline password removal / SAM editing / credential extraction | — | 🚫 Excluded by the spec's own safety rules (M39 §5) |

## Spec reconciliation

- The first spec asks for a wolf logo; the second for a penguin mascot. The provided artwork is a penguin, so the penguin is used throughout. Its blue/green palette is the default accent; orange is the brand highlight (shield markers, admin button) and charcoal/silver form the dark theme.
- Phase numbering follows the Ultimate roadmap (11 phases).
