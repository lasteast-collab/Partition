# User guide (short)

**Start** — The overview shows every disk as a map. Click a partition or free space to see details and actions.
"Scan my computer" (Smart Fix) checks space, layout, alignment and health and offers safe fixes.

**Changing disks** — Actions you choose go to **Pending**. There you see each disk before and after, the time estimate
and any data-loss warning. Type APPLY to run them. Lasteast backs up the partition table first and checks the disk after
every step. Physical disks must be allowed in Settings › Safety first; practice on a virtual disk (Advanced › Virtual Disks).

**Give C: more space** — Smart Fix or the Assistant extends C: into free space next to it, moving the Recovery partition out
of the way if needed. Use Cleanup and Storage Analyzer to free space without touching partitions.

**New SSD** — Migrate › OS Migration copies Windows and its boot partitions. Choose "replaces the old disk" for a straight
swap, or "both disks stay connected" to boot either one. Disk Clone copies any disk or chosen partitions.

**A drive shows as RAW / "you need to format the disk"** — do not format it. Run Lasteast as administrator, open
Recover › Partition Recovery, choose the disk and click "Check boot sector" on the unreadable partition. If the file
system's backup boot sector is intact, "Repair…" restores it through the usual preview and confirmation.

**Something is lost** — Recover › Partition Recovery finds deleted or lost partitions; File Recovery copies deleted files
(NTFS, FAT32, exFAT) to another disk. After a format, or when the file system is unreadable, tick "Search by file content"
to find photos, videos, PDF and Office files by their content (names are generated). For a failing disk, make an image
first and use "Open disk image…" — the image is never modified. Do not write to the affected disk until you are done.

**PC does not start** — Recover › Boot Recovery: back up and rebuild boot files, restore a partition table from Lasteast's
backups, or build the Rescue Boot ISO (needs Microsoft's Windows ADK + WinPE add-on). Account Recovery explains Microsoft's
supported ways back into Windows; Data Backup saves your folders first.

**Health** — Analyze › Disk Health shows SMART/NVMe data; Performance Lab benchmarks a volume and read-tests a disk surface.
Monitoring (Settings) warns about low space, failing drives and heat while the app is open.

**Technicians** — Advanced › Technician exports HTML/CSV/JSON reports; the command line (`lasteast --help`) scripts
inventory, dry-runs and recovery. A `portable` file next to the program keeps all data beside it.
