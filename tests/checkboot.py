# checkboot.py patched.img -> validates and extracts ramdisk into dir 'x'
import sys, struct, os, subprocess, hashlib
d = open(sys.argv[1],'rb').read()
assert d[:8] == b'ANDROID!'
hv = struct.unpack_from('<I', d, 40)[0]
if hv >= 3:
    ks, rs = struct.unpack_from('<II', d, 8); pg = 4096; off = pg + (ks+pg-1)//pg*pg
else:
    ks, _, rs = struct.unpack_from('<III', d, 8); pg = struct.unpack_from('<I', d, 36)[0]; off = pg + (ks+pg-1)//pg*pg
    # verify id
    kern = d[pg:pg+ks]; rdd = d[off:off+rs]
    sha = hashlib.sha1()
    p = off + (rs+pg-1)//pg*pg
    for x in (kern, rdd, b''): sha.update(x); sha.update(struct.pack('<I', len(x)))
    if hv >= 1: sha.update(b''); sha.update(struct.pack('<I', 0))
    if hv == 2:
        ds = struct.unpack_from('<I', d, 1648)[0]; dtb = d[p:p+ds]; sha.update(dtb); sha.update(struct.pack('<I', ds)); assert dtb == b'DTB'*10, 'dtb lost'
    assert d[576:596] == sha.digest(), 'id sha1 mismatch'
rd = d[off:off+rs]
if struct.unpack_from('<I', rd, 0)[0] == 0x58881688:
    n = struct.unpack_from('<I', rd, 4)[0]; assert n == rs - 512, 'mtk size'; rd = rd[512:]
open('p.rd','wb').write(rd)
if rd[:2] == b'\x1f\x8b': os.system('gzip -dc < p.rd > p.cpio')
elif rd[:4] == bytes.fromhex('02214c18'): os.system('lz4 -d -f -q p.rd p.cpio')
else: os.system('cp p.rd p.cpio')
os.system('rm -rf x && mkdir x && cd x && cpio -id --quiet < ../p.cpio')
if d[-64:-60] == b'AVBf':
    osz, vboff, vbs = struct.unpack_from('>QQQ', d, len(d)-64+12)
    assert d[vboff:vboff+4] == b'AVB0', 'vbmeta blob not at offset'
    print('avb footer ok, original_image_size', osz)
print('size', len(d), 'hv', hv)
