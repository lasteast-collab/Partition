# Building & testing

## Windows (full build)

```powershell
dotnet --version          # 8.0.x
dotnet build LasteastPartition.sln -c Release
dotnet run --project tests/Lasteast.Tests -c Release
dotnet run --project src/Lasteast.App -c Release            # real disks, read-only
dotnet run --project src/Lasteast.App -c Release -- --demo  # sample data
```

Publish a single folder:

```powershell
dotnet publish src/Lasteast.App -c Release -r win-x64 --self-contained false -o publish
```

## Linux / macOS (non-UI parts)

Core, Storage.Windows (compiles; APIs are Windows-only at run time), Presentation, CLI and the tests build and run anywhere:

```bash
dotnet run --project tests/Lasteast.Tests
```

The WPF project needs the Windows Desktop SDK and only builds on Windows.

## Tests

`tests/Lasteast.Tests` is a small self-contained runner (`[Test]` static methods, `dotnet run`, optional name filter as argument). Coverage:

- Disk-map segmentation (GPT gaps, MBR extended/logical, overlap detection) and proportional width math
- Byte-exact parsing of `DRIVE_LAYOUT_INFORMATION_EX` (GPT and MBR), `STORAGE_DEVICE_DESCRIPTOR`, `DISK_GEOMETRY_EX`, alignment and volume-extent buffers
- Disk assembly: volume mapping, logical partitions, Windows/boot flags, unreadable tables
- Insights, formatting, partition-type classification, settings persistence/corruption recovery
- View models end to end with the sample provider (refresh, selection persistence, Hebrew RTL switch, serial masking)
- **Static XAML checks**: well-formed XAML, every `StaticResource`/`DynamicResource` key exists, Dark/Light define identical keys, every data-driven brush key exists, every binding path names a real property, every localization key exists in all 7 languages with matching `{n}` placeholders

## Trying operations safely (Phase 2)

1. Open an **elevated** PowerShell in the repo and run `powershell -ExecutionPolicy Bypass -File tools\vhdx-testbed.ps1`.
   It creates two small VHDX disks (GPT and MBR) with test partitions.
2. Start Lasteast **as administrator**, press F5. The test disks show as "Virtual disk file".
3. On Overview select a partition or unallocated space on a test disk → choose an action → *Add to pending*.
4. Open *Pending Operations*, compare before/after, tick the backup box, type `APPLY`, press *Apply changes*.
5. Compare the result with `diskmgmt.msc`. Backups are in `%LOCALAPPDATA%\Lasteast\Partition\backups`, history in `history.jsonl`.
6. Clean up with `tools\vhdx-testbed.ps1 -Remove`.

Physical disks stay locked until Settings › Safety › "Allow changes to physical disks" is ticked.

## Manual verification checklist (Windows)

1. Run the app unelevated: all internal disks listed; C: marked Windows; ESP marked Boot (UEFI).
2. Plug in a USB stick: list refreshes within ~2 s; unplug: disk disappears.
3. Compare with `diskmgmt.msc` and `Get-Partition | ft DiskNumber,PartitionNumber,Offset,Size,Type`. Offsets/sizes must match byte-for-byte.
4. Switch to עברית and العربية: layout mirrors, disk maps stay in sector order; switch back.
5. Toggle Dark/Light and each accent; restart and confirm settings persisted.
6. `lasteast inventory --json` output matches the UI.
7. Attach a VHDX (Disk Management → Attach VHD) and check it shows as "Virtual disk file".

## Editing translations

String tables live in `tools/strings_part*.py` as aligned rows (EN, HE, AR, FR, ES, DE, RU) so a key can't be added to one language only.
After editing, regenerate the embedded JSON:

```bash
python3 tools/build_strings.py
```

The `StaticChecks` tests fail if any language is missing a key or a `{n}` placeholder.
