<#
.SYNOPSIS
  Creates (or removes) throwaway virtual disks for trying Lasteast Partition operations safely.

.DESCRIPTION
  Creates two expandable VHDX files in %USERPROFILE%\LasteastTestbed and attaches them:
    lasteast-gpt.vhdx  4 GB GPT : NTFS 1 GB "LE-NTFS", FAT32 512 MB "LE-FAT", rest unallocated
    lasteast-mbr.vhdx  2 GB MBR : NTFS 800 MB "LE-MBR",            rest unallocated
  They appear in Lasteast as "Virtual disk file (VHD/VHDX)", which is the only kind of disk
  Lasteast will change until you enable physical disks in Settings > Safety.
  Uses diskpart (built into Windows; no Hyper-V needed). Must run as administrator.

.EXAMPLE
  powershell -ExecutionPolicy Bypass -File tools\vhdx-testbed.ps1            # create + attach
  powershell -ExecutionPolicy Bypass -File tools\vhdx-testbed.ps1 -Detach    # detach (files kept)
  powershell -ExecutionPolicy Bypass -File tools\vhdx-testbed.ps1 -Remove    # detach and delete files
#>
param([switch]$Detach, [switch]$Remove)
$ErrorActionPreference = 'Stop'

$principal = New-Object Security.Principal.WindowsPrincipal([Security.Principal.WindowsIdentity]::GetCurrent())
if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    throw 'Run this script from an elevated PowerShell (Run as administrator).'
}

$dir = Join-Path $env:USERPROFILE 'LasteastTestbed'
$gpt = Join-Path $dir 'lasteast-gpt.vhdx'
$mbr = Join-Path $dir 'lasteast-mbr.vhdx'

function Invoke-DiskPart([string[]]$lines) {
    $tmp = [IO.Path]::GetTempFileName()
    Set-Content -Path $tmp -Value $lines -Encoding ASCII
    try {
        $out = & diskpart.exe /s $tmp
        if ($LASTEXITCODE -ne 0) { $out | Write-Host; throw "diskpart failed ($LASTEXITCODE)" }
    } finally { Remove-Item $tmp -Force }
}

if ($Detach -or $Remove) {
    foreach ($f in $gpt, $mbr) {
        if (Test-Path $f) {
            try { Invoke-DiskPart @("select vdisk file=`"$f`"", 'detach vdisk') } catch { Write-Host "  (already detached) $f" }
            if ($Remove) { Remove-Item $f -Force; Write-Host "Deleted $f" } else { Write-Host "Detached $f" }
        }
    }
    return
}

New-Item -ItemType Directory -Force -Path $dir | Out-Null
foreach ($f in $gpt, $mbr) { if (Test-Path $f) { throw "$f already exists. Use -Remove first." } }

Invoke-DiskPart @(
    "create vdisk file=`"$gpt`" maximum=4096 type=expandable",
    "attach vdisk", "convert gpt",
    "create partition primary size=1024", "format fs=ntfs label=LE-NTFS quick", "assign",
    "create partition primary size=512", "format fs=fat32 label=LE-FAT quick", "assign"
)
Invoke-DiskPart @(
    "create vdisk file=`"$mbr`" maximum=2048 type=expandable",
    "attach vdisk", "convert mbr",
    "create partition primary size=800", "format fs=ntfs label=LE-MBR quick", "assign"
)
Write-Host "Test disks attached:`n  $gpt`n  $mbr`nOpen Lasteast Partition and press Refresh (F5)."
Write-Host "Note: attached VHDX files are detached at reboot. Re-attach with: diskpart > select vdisk file=... > attach vdisk"
