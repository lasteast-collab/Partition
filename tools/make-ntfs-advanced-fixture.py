#!/usr/bin/env python3
"""Builds tests/fixtures/ntfs-adv64.img.gz with ntfs-3g (Linux, FUSE): a 64 MiB NTFS volume holding
  frag.bin        — written into ~1500 separate 4 KiB holes, so its run list needs an $ATTRIBUTE_LIST
  Packed/log.txt  — NTFS-compressed (LZNT1), compressible text (kept)
  Packed/gone.txt — NTFS-compressed, deleted
  Packed/noise.bin— in the compressed folder but incompressible (units stored raw)
Writes tests/fixtures/ntfs-adv-checksums.txt: "<path>\t<deleted 0|1>\t<sha256>"."""
import gzip, hashlib, os, random, subprocess, tempfile
root = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
out = os.path.join(root, "tests", "fixtures")
t = tempfile.mkdtemp(); img = os.path.join(t, "n.img"); mnt = os.path.join(t, "m"); os.mkdir(mnt)
with open(img, "wb") as f: f.truncate(64 * 1024 * 1024)
subprocess.run(["mkntfs", "-F", "-Q", "-c", "4096", "-L", "LE-ADV", img], check=True, stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)
subprocess.run(["ntfs-3g", "-o", "compression", img, mnt], check=True)

def text(seed, n):
    r = random.Random(seed); words = ["disk", "sector", "cluster", "volume", "lasteast", "record", "partition", "backup"]
    return "".join(f"{i:07d} " + " ".join(r.choice(words) for _ in range(8)) + "\n" for i in range(n)).encode()

try:
    # compressed files (folder with FILE_ATTRIBUTE_COMPRESSED: new files inherit it)
    os.mkdir(os.path.join(mnt, "Packed"))
    os.setxattr(os.path.join(mnt, "Packed"), "system.ntfs_attrib_be", (0x00000810).to_bytes(4, "big"))
    log, gone = text(2, 9000), text(3, 5000)
    noise = random.Random(4).randbytes(200_000)
    for name, data in [("log.txt", log), ("gone.txt", gone), ("noise.bin", noise)]:
        with open(os.path.join(mnt, "Packed", name), "wb") as f: f.write(data)
    attrs = int.from_bytes(os.getxattr(os.path.join(mnt, "Packed", "log.txt"), "system.ntfs_attrib_be"), "big")
    assert attrs & 0x800, hex(attrs)

    # fragmented file: small files, the rest of the volume filled, every other small file deleted → only 4 KiB holes
    holes = os.path.join(mnt, "holes"); os.mkdir(holes)
    for i in range(3200):
        with open(os.path.join(holes, f"h{i:05d}"), "wb") as f: f.write(b"H" * 4096)
    f = open(os.path.join(mnt, "rest.bin"), "wb", buffering=0)
    try:
        while True: f.write(b"R" * 4096)
    except OSError: pass
    try: f.close()
    except OSError: pass
    for i in range(0, 3200, 2): os.remove(os.path.join(holes, f"h{i:05d}"))
    frag = text(1, 100000)[: 1450 * 4096]
    with open(os.path.join(mnt, "frag.bin"), "wb") as a:
        for i in range(0, len(frag), 4096):
            a.write(frag[i:i + 4096]); a.flush(); os.fsync(a.fileno())
    os.remove(os.path.join(mnt, "Packed", "gone.txt"))
    exp = [("frag.bin", 0, frag), ("Packed\\log.txt", 0, log), ("Packed\\gone.txt", 1, gone), ("Packed\\noise.bin", 0, noise)]
finally:
    subprocess.run(["umount", mnt], check=True)
with gzip.open(os.path.join(out, "ntfs-adv64.img.gz"), "wb", 9) as g: g.write(open(img, "rb").read())
with open(os.path.join(out, "ntfs-adv-checksums.txt"), "w", newline="\n") as f:
    for p, d, data in exp: f.write(f"{p}\t{d}\t{hashlib.sha256(data).hexdigest()}\n")
print("ok", os.path.getsize(os.path.join(out, "ntfs-adv64.img.gz")))
