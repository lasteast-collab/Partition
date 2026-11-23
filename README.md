# Lasteast Partition

![logo](src/Lasteast.App/Assets/logo-mark.png)

Safe disk and partition management for Windows 10/11 (x64): partitions, conversion, cloning, OS migration, partition and
file recovery, boot repair and rescue media, disk health, and technician tools — in English, עברית, العربية, Français,
Español, Deutsch and Русский. C# / .NET 8 / WPF / MVVM.

**Status: 1.0.0-preview.1** — all roadmap phases implemented. See [`docs/STATUS.md`](docs/STATUS.md) for exactly what is
done, partial (🟡) or not built, and why.

## Safety model

* Nothing changes until you review a before/after preview and type **APPLY**.
* The partition table is backed up before every change; every step is re-read and verified; the engine stops at the first problem.
* **Physical disks are locked** until you allow them in Settings › Safety. Virtual disks (VHDX) are the default place to work.
* Recovered files are never written to the disk they come from.
* Account recovery is guidance only: no password bypass, no BitLocker circumvention, no credential access.
* Health verdicts use only what drives report — unknown is shown as unknown.

Details: [`docs/SAFETY.md`](docs/SAFETY.md).

## Download

Installers and portable zips are built by CI for every commit (Actions → build → artifacts) and attached to tagged
releases. The installer is not code-signed yet.

## Build

Requirements: Windows 10/11 x64 and the [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0).

```powershell
dotnet build LasteastPartition.sln -c Release
dotnet run --project tests/Lasteast.Tests                 # unit tests (also run on Linux)
dotnet run --project tests/Lasteast.IntegrationTests      # as administrator: real VHDX disks, never physical ones
dotnet run --project src/Lasteast.App                     # the app
dotnet run --project src/Lasteast.App -- --demo           # labelled sample data
dotnet run --project src/Lasteast.Cli -- --help           # command line
```

More: [`docs/BUILDING.md`](docs/BUILDING.md), [`docs/ARCHITECTURE.md`](docs/ARCHITECTURE.md), [`docs/USER_GUIDE.md`](docs/USER_GUIDE.md),
[`docs/LICENSING.md`](docs/LICENSING.md), [`CHANGELOG.md`](CHANGELOG.md).
