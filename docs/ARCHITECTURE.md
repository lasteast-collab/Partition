# Architecture

## Layers

```
┌──────────────────────────────────────────────────────────────────────────┐
│ Lasteast.App (WPF)            Views · Themes · DiskMapView · ThemeService │
│                               DeviceChangeWatcher · TrExtension (i18n)    │
├──────────────────────────────────────────────────────────────────────────┤
│ Lasteast.Presentation         MainViewModel · page VMs · Disk/Segment VMs │
│ (no WPF reference)            ObservableObject / Relay / AsyncRelay       │
├───────────────────────────────┬──────────────────────────────────────────┤
│ Lasteast.Core                 │ Lasteast.Storage.Windows                  │
│  Models (Disk/Partition/Vol)  │  NativeMethods (P/Invoke, query-only)     │
│  DiskMapLayout (pure)         │  StructParsers (byte-exact, tested)       │
│  StorageInsights (findings)   │  DiskAssembler (pure, tested)             │
│  Loc (7 languages, RTL)       │  WindowsStorageInventoryProvider          │
│  Settings (JSON) · FileLog    │  WindowsPrivilegeService                  │
│  Abstractions (interfaces)    │                                           │
└───────────────────────────────┴──────────────────────────────────────────┘
        ▲ future engines plug in behind interfaces in Core.Abstractions:
        IOperationEngine (P2) · ICloneEngine (P5) · IRecoveryEngine (P7) · IHealthProvider (P9) · IAssistant (P10, optional)
```

Rules:

- **UI is separated from the disk engine.** The WPF project contains no storage logic; view models contain no Win32.
- **Detection is strictly read-only.** Every device handle is opened with `dwDesiredAccess = 0` (IOCTL query only), so the Phase 1 code *cannot* read or write sectors.
- **Pure where possible.** Layout math, IOCTL buffer parsing, disk assembly, insights and view models are pure and unit-tested on any OS. Only `WindowsStorageInventoryProvider` touches the OS.
- **Never invent data.** Health is `Unknown` until SMART/NVMe reading exists (Phase 9). Read failures become visible notes, not silent omissions.

## Detection pipeline (Phase 1)

1. `FindFirstVolume/FindNextVolume` → for each volume: mount points, `GetVolumeInformation` (label, FS, read-only flag), free/total space, cluster size, `IOCTL_VOLUME_GET_VOLUME_DISK_EXTENTS`.
2. Windows volume = volume containing `%SystemRoot%`. Firmware system partition (ESP/active) = `HKLM\SYSTEM\Setup\SystemPartition` matched through `QueryDosDevice`.
3. `\\.\PhysicalDrive0..63`: `IOCTL_DISK_GET_DRIVE_GEOMETRY_EX`, `IOCTL_STORAGE_QUERY_PROPERTY` (device descriptor → model/serial/bus/removable; seek penalty → SSD/HDD; TRIM; access alignment → logical/physical sector size), `IOCTL_DISK_GET_DRIVE_LAYOUT_EX`.
4. `DiskAssembler` merges results: classifies partitions (GPT GUID / MBR type, extended vs logical), maps volumes by `(disk, offset)`, computes alignment, flags Windows/boot disks and dynamic (LDM) disks.
5. `DiskMapLayout.BuildSegments` inserts unallocated gaps (> 1 MiB, ignoring MBR lead-in/EBR slack) and reports overlaps or out-of-range partitions as problems.
6. `StorageInsights.Analyze` emits fact-based findings (low space, misalignment, MBR > 2 TB, unallocated space, layout problems, TRIM off, unreadable volumes). This is the read-only seed of Smart Fix.

`WM_DEVICECHANGE` triggers a debounced re-scan when drives are plugged/unplugged.

Why IOCTLs instead of WMI/`MSFT_Disk`: they are the native storage APIs the spec asks for, need no NuGet packages, are fast, and expose exact byte offsets. WMI/Storage Management API will be added where it is the better tool (e.g. Storage Spaces, BitLocker status) behind the same interfaces.

## UI

- **Navigation**: top bar (Home, Disk Management, Partition Recovery, Migration, Tools, Settings) + grouped sidebar with all 17 modules from the Ultimate spec. Modules not yet built open an honest "Planned — Phase N" page.
- **Disk maps**: `DiskMapView` + `ProportionalPanel` — widths proportional to size with a minimum so a 16 MB MSR stays clickable; colour strip per file system/role, used-space bar, hatched unallocated space, shield icon on system-critical partitions. Maps stay in physical (left-to-right) order in RTL languages; text inside follows the UI language.
- **Themes**: `Dark.xaml` / `Light.xaml` palettes swapped at runtime, accent colour injected, Windows title bar follows the theme (DWM). System colours are overridden so stock popups follow the palette.
- **Localization**: JSON string tables embedded in Core; `{ui:Tr Key}` markup extension binds live, so switching language (incl. Hebrew/Arabic RTL) re-renders instantly without restart. Numbers/dates/units use the selected culture.
- **High-DPI**: PerMonitorV2 manifest; vector glyphs (Segoe Fluent Icons) and a 512 px logo.

## Storage of app data

`%LOCALAPPDATA%\Lasteast\Partition\settings.json` (atomic write, corrupt files set aside) and `...\logs\lasteast-YYYY-MM-DD.log`.
SQLite arrives with the operation queue (Phase 2) for operation history and audit logs.
