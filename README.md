# Lasteast Partition

Professional disk & partition management for Windows 10/11 (x64) — C# / .NET 8 / WPF / MVVM.

> **Current build: Phase 4 — Storage Analyzer, Cleanup, Smart Fix + basic partition operations.**
> **SCAN MY COMPUTER** finds storage problems and turns supported fixes (e.g. expanding C:) into previewed, confirm-first operations.
> Detect and visualize disks; plan create / delete / format / resize / label / drive-letter operations,
> preview them before/after, and apply them through a guarded engine (backup first, verify every step).
> **By default only virtual disks (VHDX) can be changed** — see `tools/vhdx-testbed.ps1`.
> See [`docs/STATUS.md`](docs/STATUS.md) for exactly what is done, partial, and pending.

![logo](src/Lasteast.App/Assets/logo-mark.png)

## Quick start (Windows)

Requirements: Windows 10/11 x64, [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0), Visual Studio 2022 (17.8+) with the ".NET desktop development" workload — or just the SDK.

```powershell
git clone <your repo> LasteastPartition
cd LasteastPartition
dotnet build LasteastPartition.sln -c Release
dotnet run --project tests/Lasteast.Tests            # 71 tests, no Windows APIs needed
dotnet run --project src/Lasteast.App                # the desktop app (your real disks, read-only)
dotnet run --project src/Lasteast.App -- --demo      # labelled sample data, for UI work
dotnet run --project src/Lasteast.Cli -- inventory   # console listing; add --json
```

Or open `LasteastPartition.sln` in Visual Studio and set **Lasteast.App** as the startup project.

The app runs unelevated. Use **Run as administrator** in the top bar if a device needs elevation to be read;
the later editing phases will require it.

## Solution layout

| Project | Target | Purpose |
|---|---|---|
| `Lasteast.Core` | net8.0 | Domain model, disk-map layout math, insights, localization (7 languages), settings, logging |
| `Lasteast.Storage.Windows` | net8.0 (Windows-only APIs) | Read-only detection via native storage IOCTLs; byte-exact parsers |
| `Lasteast.Presentation` | net8.0 | UI-agnostic MVVM view models + labelled sample provider |
| `Lasteast.App` | net8.0-windows (WPF) | Desktop shell, themes, disk map control, views |
| `Lasteast.Cli` | net8.0 | `lasteast inventory [--json]` |
| `Lasteast.Tests` | net8.0 | Dependency-free test runner incl. static XAML/localization checks |

**No third-party packages.** Everything builds from the .NET SDK alone.

Docs: [Architecture](docs/ARCHITECTURE.md) · [Status matrix](docs/STATUS.md) · [Building & testing](docs/BUILDING.md) · [Safety design](docs/SAFETY.md)
