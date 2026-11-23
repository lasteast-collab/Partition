#!/usr/bin/env python3
"""Builds tests/fixtures/exfat16.img.gz: a 16 MiB exFAT volume (mkfs.exfat) populated without mounting, then with
some files deleted the way Windows deletes them (InUse bit cleared on the entry set, clusters freed in the bitmap).

Contents (paths relative to the root):
  keep.txt                    existing
  gone.bin        (deleted)   contiguous, NoFatChain, 40000 bytes
  chained.bin     (deleted)   FAT chain kept (Windows leaves FAT entries), 3 clusters out of order
  Photos/         (existing)
    Photos/beach-with-a-long-name-über.jpg   (deleted)
  OldDir/         (deleted directory)
    OldDir/inside.txt  (existing entry inside a deleted directory)
Writes tests/fixtures/exfat-checksums.txt: "<path> <deleted 0|1> <sha256>".
"""
import gzip, hashlib, os, random, struct, subprocess, sys, tempfile

root = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
out = os.path.join(root, "tests", "fixtures")
tmp = tempfile.mkdtemp()
img = os.path.join(tmp, "exfat.img")
with open(img, "wb") as f:
    f.truncate(16 * 1024 * 1024)
subprocess.run(["mkfs.exfat", "-L", "LE-EXFAT", "-c", "4096", img], check=True, stdout=subprocess.DEVNULL)
d = bytearray(open(img, "rb").read())
assert d[3:11] == b"EXFAT   "
fat_off, fat_len, heap_off, ccount, root_cl = struct.unpack_from("<IIIII", d, 0x50)
bps = 1 << d[0x6C]
cs = bps << d[0x6D]
def coff(c): return heap_off * bps + (c - 2) * cs
def fat_get(c): return struct.unpack_from("<I", d, fat_off * bps + c * 4)[0]
def fat_set(c, v): struct.pack_into("<I", d, fat_off * bps + c * 4, v)

# root directory: find the bitmap and the up-case table, and the first free entry slot
def dir_clusters(first):
    c, out_ = first, []
    while 2 <= c < 0xFFFFFFF7:
        out_.append(c); c = fat_get(c)
    return out_
root_cls = dir_clusters(root_cl)
bitmap_cl = bitmap_len = None
for c in root_cls:
    for e in range(0, cs, 32):
        t = d[coff(c) + e]
        if t == 0x81:
            bitmap_cl, bitmap_len = struct.unpack_from("<IQ", d, coff(c) + e + 20)
assert bitmap_cl
def bit(c): return d[coff(bitmap_cl) + (c - 2) // 8] >> ((c - 2) % 8) & 1
def setbit(c, v):
    o = coff(bitmap_cl) + (c - 2) // 8
    d[o] = d[o] | (1 << ((c - 2) % 8)) if v else d[o] & ~(1 << ((c - 2) % 8))
def alloc(n):
    """n contiguous free clusters."""
    c = 2
    while True:
        if all(not bit(c + i) for i in range(n)):
            for i in range(n): setbit(c + i, 1)
            return c
        c += 1

def rot(chk, b, bits=16):
    return (((chk << (bits - 1)) | (chk >> 1)) + b) & ((1 << bits) - 1)

def entry_set(name, attr, first, length, nofat, ts):
    name16 = name.encode("utf-16-le")
    nlen = len(name)
    nh = 0
    for b in name.upper().encode("utf-16-le"): nh = rot(nh, b)
    names = [name16[i:i + 30].ljust(30, b"\0") for i in range(0, len(name16), 30)]
    f = bytearray(32); f[0] = 0x85; f[1] = 1 + len(names); struct.pack_into("<H", f, 4, attr)
    struct.pack_into("<III", f, 8, ts, ts, ts)
    s = bytearray(32); s[0] = 0xC0; s[1] = 0x01 | (0x02 if nofat else 0); s[3] = nlen; struct.pack_into("<H", s, 4, nh)
    struct.pack_into("<Q", s, 8, length); struct.pack_into("<I", s, 20, first); struct.pack_into("<Q", s, 24, length)
    ents = [f, s] + [bytes([0xC1, 0]) + n for n in names]
    chk = 0
    for i, e in enumerate(ents):
        for j, b in enumerate(e):
            if i == 0 and j in (2, 3): continue
            chk = rot(chk, b)
    struct.pack_into("<H", f, 2, chk)
    return [bytes(e) for e in ents]

slots = {}  # dir first cluster -> next free byte offset
def add_entries(dir_cl, ents):
    o = slots.get(dir_cl)
    if o is None:
        o = coff(dir_cl)
        while d[o] != 0: o += 32
    loc = o
    for e in ents:
        d[o:o + 32] = e; o += 32
    slots[dir_cl] = o
    return loc, len(ents)

ts = (2026 - 1980) << 25 | 11 << 21 | 23 << 16 | 14 << 11 | 30 << 5 | 10
rng = random.Random(7)
expected = []
def put_file(dir_cl, path, data, nofat=True, order=None):
    n = max(1, (len(data) + cs - 1) // cs) if data else 0
    if n == 0:
        first = 0
    elif nofat:
        first = alloc(n)
        d[coff(first):coff(first) + len(data)] = data
    else:
        cl = [alloc(1) for _ in range(n)]
        if order: cl = [cl[i] for i in order]
        for i, c in enumerate(cl):
            fat_set(c, cl[i + 1] if i + 1 < n else 0xFFFFFFFF)
            d[coff(c):coff(c) + cs] = data[i * cs:(i + 1) * cs].ljust(cs, b"\0")
        first = cl[0]
    loc = add_entries(dir_cl, entry_set(path.split("/")[-1], 0x20, first, len(data), nofat, ts))
    return loc, first, n

def put_dir(dir_cl, name):
    c = alloc(1)
    d[coff(c):coff(c) + cs] = bytes(cs)
    loc = add_entries(dir_cl, entry_set(name, 0x10, c, cs, True, ts))
    return loc, c

pending = []
def delete(loc, first=None, n=0):
    pending.append((loc, first, n))  # applied after all files are written, so freed clusters are not reused
def apply_delete(loc, first, n):
    o, count = loc
    for i in range(count): d[o + 32 * i] &= 0x7F  # clear InUse
    for i in range(n): setbit(first + i, 0)

keep = b"kept file\r\n" * 10
put_file(root_cl, "keep.txt", keep); expected.append(("keep.txt", 0, keep))
gone = bytes(rng.getrandbits(8) for _ in range(40000))
loc, first, n = put_file(root_cl, "gone.bin", gone); delete(loc, first, n); expected.append(("gone.bin", 1, gone))
chained = bytes(rng.getrandbits(8) for _ in range(cs * 2 + 100))
loc, first, n = put_file(root_cl, "chained.bin", chained, nofat=False, order=[2, 0, 1])
o, cnt = loc
c = first
cl = []
while c < 0xFFFFFFF7: cl.append(c); c = fat_get(c)
delete(loc)
for_free = list(cl)  # Windows frees the bitmap but leaves the FAT chain
expected.append(("chained.bin", 1, chained))
_, photos = put_dir(root_cl, "Photos")
pic = bytes(rng.getrandbits(8) for _ in range(9000))
name = "beach-with-a-long-name-über.jpg"
loc, first, n = put_file(photos, "Photos/" + name, pic); delete(loc, first, n); expected.append(("Photos\\" + name, 1, pic))
dloc, old = put_dir(root_cl, "OldDir")
inside = b"inside a deleted folder\n" * 50
loc, first, n = put_file(old, "OldDir/inside.txt", inside)
delete(dloc, old, 1); for_free.append(first)  # the folder is deleted; its files' clusters are freed but entries keep InUse
expected.append(("OldDir\\inside.txt", 1, inside))

for args in pending: apply_delete(*args)
for c in for_free: setbit(c, 0)

with gzip.open(os.path.join(out, "exfat16.img.gz"), "wb", 9) as g:
    g.write(d)
with open(os.path.join(out, "exfat-checksums.txt"), "w", encoding="utf-8", newline="\n") as f:
    for p, dl, data in expected:
        f.write(f"{p}\t{dl}\t{hashlib.sha256(data).hexdigest()}\n")
print("cluster", cs, "root", root_cl, "bitmap", bitmap_cl, "files", len(expected))
