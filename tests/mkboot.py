# independent boot image builder for tests: mkboot.py out ver ramdisk_fmt [mtk] [avb]
import sys, struct, gzip, subprocess, os, hashlib
out, ver, fmt = sys.argv[1], int(sys.argv[2]), sys.argv[3]
mtk = 'mtk' in sys.argv; avb = 'avb' in sys.argv
os.makedirs('rd', exist_ok=True)
os.system('rm -rf rd/*; mkdir -p rd/system/bin')
arch32 = 'arm32' in sys.argv
elf = bytearray(64); elf[0:4] = b'\x7fELF'; elf[4] = 1 if arch32 else 2; elf[5] = 1
elf[18:20] = struct.pack('<H', 0x28 if arch32 else 0xB7)
open('rd/init','wb').write(bytes(elf) + b'ORIGINAL-INIT'*1000)
open('rd/fstab.mt6765','w').write('/dev/block/by-name/system /system ext4 ro wait,verify,avb=vbmeta_system,logical,first_stage_mount\n/dev/block/by-name/userdata /data f2fs noatime forceencrypt=footer,fileencryption=aes-256-xts,quota\n')
open('rd/verity_key','wb').write(b'K'*100)
os.symlink('/system/bin/init', 'rd/system/bin/initlink') if not os.path.exists('rd/system/bin/initlink') else None
cpio = subprocess.run('cd rd && find . | sort | cpio -o -H newc --quiet', shell=True, capture_output=True).stdout
if fmt == 'gzip': rd = gzip.compress(cpio)
elif fmt == 'lz4': open('t.cpio','wb').write(cpio); os.system('lz4 -l -f -q t.cpio t.lz4'); rd = open('t.lz4','rb').read()
else: rd = cpio
kernel = gzip.compress(b'\x00'*0x38 + b'ARM\x64' + os.urandom(100000))
if mtk:
    def mh(d, name): return struct.pack('<II', 0x58881688, len(d)) + name.ljust(32, b'\0') + b'\xff'*472 + d
    rd = mh(rd, b'ROOTFS'); kernel = mh(kernel, b'KERNEL')
def pad(d, p): return d + b'\0'*((-len(d)) % p)
if ver >= 3:
    hdr = b'ANDROID!' + struct.pack('<IIII', len(kernel), len(rd), 0, 1580 if ver == 3 else 1584) + b'\0'*16 + struct.pack('<I', ver) + b'console=ttyS0'.ljust(1536, b'\0')
    if ver == 4: hdr += struct.pack('<I', 0)
    img = pad(hdr, 4096) + pad(kernel, 4096) + pad(rd, 4096)
else:
    pg = 2048
    sha = hashlib.sha1()
    for d in (kernel, rd, b''): sha.update(d); sha.update(struct.pack('<I', len(d)))
    if ver >= 1: sha.update(b''); sha.update(struct.pack('<I', 0))
    if ver >= 2: sha.update(b'DTB'*10); sha.update(struct.pack('<I', 30))
    hdr = b'ANDROID!' + struct.pack('<10I', len(kernel), 0x40008000, len(rd), 0x41000000, 0, 0, 0x40000100, pg, ver, 0) + b'boardname'.ljust(16, b'\0') + b'cmd'.ljust(512, b'\0') + sha.digest().ljust(32, b'\0') + b'\0'*1024
    if ver >= 1: hdr += struct.pack('<IQI', 0, 0, 1660 if ver == 2 else 1648)
    if ver == 2: hdr += struct.pack('<IQ', 30, 0x48000000)
    img = pad(hdr, pg) + pad(kernel, pg) + pad(rd, pg)
    if ver == 2: img += pad(b'DTB'*10, pg)
total = 16*1024*1024
if avb:
    vb = b'AVB0' + os.urandom(1000)
    vboff = (len(img) + 4095)//4096*4096
    data = img.ljust(vboff, b'\0') + vb
    data = data.ljust(total - 64, b'\0')
    footer = b'AVBf' + struct.pack('>II', 1, 0) + struct.pack('>QQQ', len(img), vboff, len(vb)) + b'\0'*28
    data += footer
else:
    data = img.ljust(total, b'\0')
open(out, 'wb').write(data)
