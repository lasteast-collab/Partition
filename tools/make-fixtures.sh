#!/bin/bash
# Builds small real file-system images used by the tests (committed gzip-compressed).
# Requires: mkntfs, ntfscp (ntfs-3g), mkfs.fat, mcopy (mtools). Run on Linux.
set -euo pipefail
out="$(cd "$(dirname "$0")/.." && pwd)/tests/fixtures"
tmp=$(mktemp -d)
cd "$tmp"
printf 'Hello from Lasteast\n' > hello.txt
head -c 1048576 /dev/urandom > big.bin            # 1 MiB, random (incompressible)
python3 -c "import sys; sys.stdout.buffer.write(bytes(range(256))*4096)" > pattern.bin  # 1 MiB pattern

# NTFS volume, 24 MiB, 512-byte sectors, hidden sectors = 2048 (as if at 1 MiB on a disk)
truncate -s 24M ntfs.img
mkntfs -F -Q -q -s 512 -c 4096 -p 2048 -H 255 -S 63 -L LE-NTFS ntfs.img
ntfscp -q ntfs.img hello.txt hello.txt
ntfscp -q ntfs.img big.bin big.bin
ntfscp -q ntfs.img pattern.bin pattern.bin
ntfsls ntfs.img

# FAT32 volume, 40 MiB
truncate -s 40M fat32.img
mkfs.fat -F 32 -S 512 -h 2048 -n LE-FAT32 fat32.img >/dev/null
mcopy -i fat32.img hello.txt ::/HELLO.TXT
mcopy -i fat32.img pattern.bin ::/PATTERN.BIN
mdir -i fat32.img ::/

sha256sum big.bin pattern.bin hello.txt > checksums.txt
gzip -9 -c ntfs.img > "$out/ntfs24.img.gz"
gzip -9 -c fat32.img > "$out/fat32-40.img.gz"

cp checksums.txt "$out/checksums.txt"
ls -la "$out"
rm -rf "$tmp"

# ---- Phase 7: deleted files (requires FUSE: ntfs-3g mount) ----
tmp2=$(mktemp -d); cd "$tmp2"
python3 - <<'PY'
import os, random
random.seed(7)
open("keep.txt","w").write("still here\n")
open("small-deleted.txt","w").write("tiny resident file that was deleted\n")
open("deleted.bin","wb").write(bytes(random.getrandbits(8) for _ in range(300*1024)))
open("nested-deleted.dat","wb").write(bytes(random.getrandbits(8) for _ in range(150*1024)))
open("Ünïcode name – deleted.txt","w",encoding="utf-8").write("unicode " * 1000)
PY
truncate -s 32M ntfsdel.img
mkntfs -F -Q -q -s 512 -c 4096 -p 2048 -H 255 -S 63 -L LE-DEL ntfsdel.img
mkdir mnt
ntfs-3g ntfsdel.img mnt
cp keep.txt small-deleted.txt deleted.bin "Ünïcode name – deleted.txt" mnt/
mkdir -p "mnt/Photos/2024"
cp nested-deleted.dat "mnt/Photos/2024/"
sync
rm "mnt/small-deleted.txt" "mnt/deleted.bin" "mnt/Photos/2024/nested-deleted.dat" "mnt/Ünïcode name – deleted.txt"
umount mnt

truncate -s 40M fatdel.img
mkfs.fat -F 32 -S 512 -h 2048 -n LE-FATDEL fatdel.img >/dev/null
cp deleted.bin DELETED.BIN
mmd -i fatdel.img ::/Docs
mcopy -i fatdel.img keep.txt ::/KEEP.TXT
mcopy -i fatdel.img deleted.bin "::/Long file name deleted.bin"
mcopy -i fatdel.img nested-deleted.dat ::/Docs/NESTED.DAT
mdel -i fatdel.img "::/Long file name deleted.bin" ::/Docs/NESTED.DAT

sha256sum keep.txt small-deleted.txt deleted.bin nested-deleted.dat "Ünïcode name – deleted.txt" > "$out/checksums-deleted.txt"
gzip -9 -c ntfsdel.img > "$out/ntfs-deleted32.img.gz"
gzip -9 -c fatdel.img > "$out/fat32-deleted40.img.gz"

# exFAT volume for the lost-partition scanner
truncate -s 16M exfat.img
mkfs.exfat -n LE-EXFAT exfat.img >/dev/null
gzip -9 -c exfat.img > "$out/exfat16.img.gz"
cd /; rm -rf "$tmp2"
ls -la "$out"
