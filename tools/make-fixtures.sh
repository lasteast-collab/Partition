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
