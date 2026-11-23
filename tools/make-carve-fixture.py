"""Builds tests/fixtures/carve4.img.gz: real files of several formats at sector boundaries between random junk,
plus carve-checksums.txt (offset, extension, sha256). Requires Pillow."""
import io, os, gzip, hashlib, random, struct, zipfile, pathlib
from PIL import Image, ImageDraw
random.seed(11)
out = pathlib.Path(__file__).resolve().parent.parent / "tests" / "fixtures"

def jpeg():
    im = Image.new("RGB", (320, 200), (30, 90, 160)); d = ImageDraw.Draw(im)
    for i in range(40):
        x0, x1 = sorted((random.randint(0,320), random.randint(0,320))); y0, y1 = sorted((random.randint(0,200), random.randint(0,200)))
        d.ellipse([x0, y0, x1, y1], outline=(random.randint(0,255),)*3)
    b = io.BytesIO(); im.save(b, "JPEG", quality=85); main = b.getvalue()
    # an EXIF-style APP1 segment holding a complete thumbnail JPEG (its own FFD8..FFD9) right after SOI
    tb = io.BytesIO(); Image.new("RGB", (32, 20), (200, 50, 50)).save(tb, "JPEG")
    payload = b"Exif\0\0" + tb.getvalue()
    app1 = b"\xff\xe1" + struct.pack(">H", len(payload) + 2) + payload
    return main[:2] + app1 + main[2:]
def png():
    im = Image.new("RGBA", (200, 120), (0, 0, 0, 0)); d = ImageDraw.Draw(im)
    for i in range(30): d.line([random.randint(0,200), random.randint(0,120), random.randint(0,200), random.randint(0,120)], fill=(random.randint(0,255), 120, 40, 255), width=3)
    b = io.BytesIO(); im.save(b, "PNG"); return b.getvalue()
def gif():
    im = Image.new("P", (120, 80)); d = ImageDraw.Draw(im)
    for i in range(20):
        x0, x1 = sorted((random.randint(0,120), random.randint(0,120))); y0, y1 = sorted((random.randint(0,80), random.randint(0,80)))
        d.rectangle([x0, y0, x1, y1], fill=random.randint(0,255))
    b = io.BytesIO(); im.save(b, "GIF"); return b.getvalue()
def pdf():
    im = Image.new("RGB", (300, 300), (240, 240, 240)); ImageDraw.Draw(im).text((20, 140), "Lasteast carving test", fill=(0,0,0))
    b = io.BytesIO(); im.save(b, "PDF"); return b.getvalue()
def docx():
    b = io.BytesIO()
    with zipfile.ZipFile(b, "w", zipfile.ZIP_DEFLATED) as z:
        z.writestr("[Content_Types].xml", '<?xml version="1.0"?><Types xmlns="http://schemas.openxmlformats.org/package/2006/content-types"/>')
        z.writestr("word/document.xml", "<w:document>" + "Lasteast " * 2000 + "</w:document>")
    return b.getvalue()
def zipf():
    b = io.BytesIO()
    with zipfile.ZipFile(b, "w", zipfile.ZIP_DEFLATED) as z:
        z.writestr("notes.txt", "hello " * 5000); z.writestr("data.bin", bytes(random.getrandbits(8) for _ in range(30000)))
    return b.getvalue()
def mp4():
    def box(t, payload): return struct.pack(">I", 8 + len(payload)) + t + payload
    ftyp = box(b"ftyp", b"isom" + struct.pack(">I", 512) + b"isomiso2mp41")
    moov = box(b"moov", box(b"mvhd", bytes(100)))
    mdat = box(b"mdat", bytes(random.getrandbits(8) for _ in range(150000)))
    return ftyp + moov + mdat

files = [("jpg", jpeg()), ("png", png()), ("gif", gif()), ("pdf", pdf()), ("docx", docx()), ("zip", zipf()), ("mp4", mp4()), ("jpg", jpeg())]
size = 4 * 1024 * 1024
img = bytearray(size)
for sec in range(0, size, 512):                     # 1 in 5 sectors random junk, the rest zeros (keeps the fixture small)
    if random.random() < 0.2: img[sec:sec + 512] = bytes(random.getrandbits(8) for _ in range(512))
lines = []; pos = 64 * 1024
for ext, data in files:
    pos = (pos + 511) // 512 * 512
    img[pos:pos + len(data)] = data
    # make sure junk right after the file cannot extend it (zero the tail of its last sector)
    end = (pos + len(data) + 511) // 512 * 512
    img[pos + len(data):end] = bytes(end - pos - len(data))
    lines.append(f"{pos} {ext} {hashlib.sha256(data).hexdigest()}")
    pos = end + random.randint(1, 120) * 512
assert pos < size
with gzip.open(out / "carve4.img.gz", "wb", 9) as f: f.write(img)
(out / "carve-checksums.txt").write_text("\n".join(lines) + "\n")
print("\n".join(lines))
