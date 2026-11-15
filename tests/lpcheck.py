# independent liblp parser: lpcheck.py super.raw [extract_dir]
import sys, struct, hashlib, os
f = open(sys.argv[1], 'rb')
def rd(o, n): f.seek(o); return f.read(n)
g = rd(4096, 4096)
magic, ss = struct.unpack_from('<II', g, 0)
assert magic == 0x616c4467, 'geo magic'
chk = g[8:40]; gg = bytearray(g[:ss]); gg[8:40] = b'\0'*32
assert hashlib.sha256(gg).digest() == chk, 'geo checksum'
assert rd(8192, 4096) == g, 'backup geometry differs'
maxsz, slots, lbs = struct.unpack_from('<III', g, 40)
def parse(off):
    h = rd(off, maxsz)
    m, maj, mn, hs = struct.unpack_from('<IHHI', h, 0)
    assert m == 0x414C5030
    hh = bytearray(h[:hs]); hc = bytes(hh[12:44]); hh[12:44] = b'\0'*32
    assert hashlib.sha256(hh).digest() == hc, 'header checksum'
    ts, = struct.unpack_from('<I', h, 44)
    assert hashlib.sha256(h[hs:hs+ts]).digest() == h[48:80], 'tables checksum'
    d = [struct.unpack_from('<III', h, 80+12*i) for i in range(4)]
    t = h[hs:hs+ts]
    ext = [struct.unpack_from('<QIQI', t, d[1][0]+i*d[1][2]) for i in range(d[1][1])]
    parts = []
    for i in range(d[0][1]):
        o = d[0][0]+i*d[0][2]
        name = t[o:o+36].rstrip(b'\0').decode(); attr, fe, ne, gi = struct.unpack_from('<IIII', t, o+36)
        parts.append((name, attr, ext[fe:fe+ne], gi))
    return (maj, mn), parts, h[:hs+ts]
base = 4096 + 8192
copies = [parse(base + maxsz*s) for s in range(slots)] + [parse(base + maxsz*slots + maxsz*s) for s in range(slots)]
assert all(c[2] == copies[0][2] for c in copies), 'slot copies differ'
ver, parts, _ = copies[0]
print('metadata OK v%d.%d, %d slots' % (ver[0], ver[1], slots))
for name, attr, exts, gi in parts:
    size = sum(e[0] for e in exts)*512
    print('  %-14s attr=%d group=%d size=%d extents=%s' % (name, attr, gi, size, [(e[0], e[2]) for e in exts]))
    if len(sys.argv) > 2 and exts:
        with open(os.path.join(sys.argv[2], name + '.img'), 'wb') as o:
            for n, tt, data, src in exts:
                o.write(rd(data*512, n*512))
