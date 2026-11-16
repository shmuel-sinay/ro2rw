using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace Ro2rwPc
{
    // ======================================================================= vbmeta
    /// <summary>Disables AVB verification/verity in vbmeta (logic of AVB-Disabler).</summary>
    public static class VbmetaPatcher
    {
        public static bool IsVbmeta(byte[] b) { return b.Length >= 256 && b[0] == 'A' && b[1] == 'V' && b[2] == 'B' && b[3] == '0'; }

        /// <returns>true if it was active and is now disabled, false if it was already disabled.</returns>
        public static bool Patch(byte[] b, Action<string> log)
        {
            if (!IsVbmeta(b)) throw new InvalidDataException("קובץ vbmeta לא חוקי (חסר AVB0)");
            bool active = (b[123] & 3) != 3;
            b[123] |= 3;   // flags (big endian u32 @120): HASHTREE_DISABLED | VERIFICATION_DISABLED
            log(active ? "  AVB היה פעיל - כובה (verity + verification)" : "  AVB כבר היה כבוי");
            return active;
        }
    }

    // ======================================================================= LK
    /// <summary>MTK LK: removes the 5 second Orange State delay and/or the warning text.</summary>
    public static class LkPatcher
    {
        public sealed class Result { public int DelayPatched; public int TextsCleared; public string AndroidHint = ""; }

        // 08 B5 ?? ?? 7B 44 1B 68 1B 68 0[1|2] 2B  ->  08 B5 00 20 08 BD 1B 68 1B 68 0[1|2] 2B
        static readonly byte[] Suffix = { 0x7B, 0x44, 0x1B, 0x68, 0x1B, 0x68 };
        static readonly string[] TextKeys = { "orange state", "has been unlocked", "be trusted", "will boot in", "boot in 5", "power off in", "verify fail" };

        public static Result Patch(byte[] b, bool removeDelay, bool removeText, Action<string> log)
        {
            var res = new Result();
            if (removeDelay)
            {
                for (int i = 0; i + 12 <= b.Length; i++)
                {
                    if (b[i] != 0x08 || b[i + 1] != 0xB5) continue;
                    bool ok = true;
                    for (int k = 0; k < Suffix.Length; k++) if (b[i + 4 + k] != Suffix[k]) { ok = false; break; }
                    if (!ok || (b[i + 10] != 0x01 && b[i + 10] != 0x02) || b[i + 11] != 0x2B) continue;
                    res.AndroidHint = b[i + 10] == 0x02 ? "אנדרואיד 10 ומעלה" : "אנדרואיד 9 ומטה";
                    log(string.Format("  נמצא קוד ההשהיה ב-0x{0:X} ({1})", i, res.AndroidHint));
                    b[i + 2] = 0x00; b[i + 3] = 0x20; b[i + 4] = 0x08; b[i + 5] = 0xBD;
                    res.DelayPatched++;
                    i += 11;
                }
                if (res.DelayPatched == 0) log("  ! קוד ההשהיה (5 שניות) לא נמצא. ייתכן שהקובץ כבר תוקן או שהמכשיר לא נתמך.");
            }
            if (removeText)
            {
                int i = 0;
                while (i < b.Length)
                {
                    if (b[i] < 0x20 || b[i] > 0x7E) { i++; continue; }
                    int s = i;
                    while (i < b.Length && b[i] >= 0x20 && b[i] <= 0x7E) i++;
                    int len = i - s;
                    if (len < 6) continue;
                    if (i < b.Length && b[i] != 0 && b[i] != 0x0A) continue;
                    string str = Encoding.ASCII.GetString(b, s, len).ToLowerInvariant();
                    if (TextKeys.Any(k => str.Contains(k)))
                    {
                        log("  נמחק טקסט: \"" + Encoding.ASCII.GetString(b, s, Math.Min(len, 70)) + "\"");
                        for (int k = s; k < s + len; k++) b[k] = 0;
                        res.TextsCleared++;
                    }
                }
                if (res.TextsCleared == 0) log("  ! לא נמצא טקסט Orange State בקובץ");
            }
            return res;
        }

        public static bool LooksLikeLk(byte[] b)
        {
            if (b.Length > 8 && BitConverter.ToUInt32(b, 0) == 0x58881688) return true;
            return false;
        }
    }

    // ======================================================================= Magisk
    public sealed class MagiskOptions
    {
        public bool KeepVerity, KeepForceEncrypt;
        public string Abi = "auto";   // auto / arm64-v8a / armeabi-v7a / x86_64 / x86
    }

    /// <summary>Patches boot/init_boot on the PC exactly like Magisk's boot_patch.sh (+ magiskboot cpio).</summary>
    public static class MagiskPatcher
    {
        public static string FindOrDownloadApk(string toolsDir, Action<string> log)
        {
            Directory.CreateDirectory(toolsDir);
            var local = Directory.GetFiles(toolsDir, "*.apk").Where(f => Path.GetFileName(f).ToLowerInvariant().Contains("magisk")).OrderByDescending(f => File.GetLastWriteTimeUtc(f)).FirstOrDefault();
            if (local != null) { log("  משתמש ב-" + Path.GetFileName(local)); return local; }
            log("  מוריד את Magisk הרשמי מ-GitHub (topjohnwu/Magisk)...");
            try { ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12; } catch { }
            string json;
            using (var wc = new WebClient())
            {
                wc.Headers.Add("User-Agent", "RO2RW-PC");
                json = wc.DownloadString("https://api.github.com/repos/topjohnwu/Magisk/releases/latest");
            }
            var m = Regex.Match(json, "\"browser_download_url\"\\s*:\\s*\"([^\"]+/Magisk-v[^\"]+\\.apk)\"");
            if (!m.Success) throw new IOException("לא נמצא קובץ Magisk.apk במהדורה האחרונה");
            string url = m.Groups[1].Value;
            string dest = Path.Combine(toolsDir, url.Substring(url.LastIndexOf('/') + 1));
            using (var wc = new WebClient())
            {
                wc.Headers.Add("User-Agent", "RO2RW-PC");
                wc.DownloadFile(url, dest + ".part");
            }
            File.Move(dest + ".part", dest);
            log("  הורד: " + Path.GetFileName(dest));
            return dest;
        }

        static byte[] ZipGet(ZipArchive z, string name)
        {
            var e = z.GetEntry(name);
            if (e == null) return null;
            using (var s = e.Open()) { var ms = new MemoryStream(); s.CopyTo(ms); return ms.ToArray(); }
        }

        public static byte[] Patch(byte[] boot, string apkPath, MagiskOptions opt, Action<string> log)
        {
            var bi = BootImage.Parse(boot);
            log("  " + bi.Info);

            string abi = opt.Abi;
            if (abi == "auto")
            {
                abi = "arm64-v8a";
                if (bi.Kernel.Length > 0 && !bi.KernelIsArm64) abi = "armeabi-v7a";
                log("  ABI: " + abi + (bi.Kernel.Length == 0 ? " (ברירת מחדל - אין kernel ב-init_boot)" : " (לפי ה-kernel)"));
            }

            using (var z = ZipFile.OpenRead(apkPath))
            {
                string script = Encoding.UTF8.GetString(ZipGet(z, "assets/boot_patch.sh") ?? new byte[0]);
                var magiskinit = ZipGet(z, "lib/" + abi + "/libmagiskinit.so");
                if (magiskinit == null) throw new InvalidDataException("ב-Magisk.apk אין קבצים עבור " + abi);
                var magisk = ZipGet(z, "lib/" + abi + "/libmagisk.so");
                var magisk64 = ZipGet(z, "lib/" + abi + "/libmagisk64.so");
                var magisk32 = ZipGet(z, "lib/" + abi + "/libmagisk32.so") ?? (abi == "arm64-v8a" ? ZipGet(z, "lib/armeabi-v7a/libmagisk32.so") : abi == "x86_64" ? ZipGet(z, "lib/x86/libmagisk32.so") : null);
                var initld = ZipGet(z, "lib/" + abi + "/libinit-ld.so");
                var stub = ZipGet(z, "assets/stub.apk");
                bool newScheme = script.Contains("overlay.d/sbin/magisk.xz") || (magisk != null && magisk64 == null);

                // ---- ramdisk
                RamdiskFormat fmt;
                Cpio cpio;
                bool skipBackup = false;
                if (bi.Ramdisk.Length == 0)
                {
                    log("  אין ramdisk - נוצר חדש");
                    fmt = RamdiskFormat.Lz4Legacy; cpio = new Cpio(); skipBackup = true;
                }
                else
                {
                    fmt = Compress.Detect(bi.Ramdisk);
                    if (fmt == RamdiskFormat.Unknown) throw new NotSupportedException("פורמט דחיסת ramdisk לא נתמך (נתמכים gzip/lz4)");
                    log("  ramdisk: " + fmt);
                    cpio = Cpio.Load(Compress.Decompress(bi.Ramdisk, fmt));
                }
                foreach (var f in new[] { "sbin/launch_daemonsu.sh", "sbin/su", "init.xposed.rc", "boot/sbin/launch_daemonsu.sh" })
                    if (cpio.Exists(f)) throw new InvalidDataException("ה-boot נערך כבר על ידי כלי אחר. השתמש ב-boot מקורי.");
                foreach (var f in new[] { ".backup/.magisk", "init.magisk.rc", "overlay/init.magisk.rc" })
                    if (cpio.Exists(f)) throw new InvalidDataException("ה-boot כבר עבר Magisk. השתמש ב-boot מקורי (לא ערוך).");
                log("  זוהה boot מקורי");

                string sha1;
                using (var h = SHA1.Create()) sha1 = BitConverter.ToString(h.ComputeHash(boot)).Replace("-", "").ToLowerInvariant();
                var orig = cpio.Clone();

                var cfg = new StringBuilder();
                cfg.Append("KEEPVERITY=" + (opt.KeepVerity ? "true" : "false") + "\n");
                cfg.Append("KEEPFORCEENCRYPT=" + (opt.KeepForceEncrypt ? "true" : "false") + "\n");
                cfg.Append("RECOVERYMODE=false\n");
                if (script.Contains("VENDORBOOT")) cfg.Append("VENDORBOOT=false\n");
                cfg.Append("SHA1=" + sha1 + "\n");

                cpio.Add(0x1E8, "init", magiskinit);                 // 0750
                cpio.Mkdir(0x1E8, "overlay.d");
                cpio.Mkdir(0x1E8, "overlay.d/sbin");
                if (newScheme)
                {
                    if (magisk == null) throw new InvalidDataException("libmagisk.so חסר ב-APK");
                    cpio.Add(0x1A4, "overlay.d/sbin/magisk.xz", XzWriter.Encode(magisk));
                }
                else
                {
                    if (magisk32 != null) cpio.Add(0x1A4, "overlay.d/sbin/magisk32.xz", XzWriter.Encode(magisk32));
                    if (magisk64 != null) cpio.Add(0x1A4, "overlay.d/sbin/magisk64.xz", XzWriter.Encode(magisk64));
                    if (stub != null && script.Contains("stub.xz")) cpio.Add(0x1A4, "overlay.d/sbin/stub.xz", XzWriter.Encode(stub));
                }
                if (initld != null && (script.Contains("init-ld") || newScheme)) cpio.Add(0x1A4, "overlay.d/sbin/init-ld.xz", XzWriter.Encode(initld));

                PatchFstabs(cpio, opt, log);
                if (!skipBackup) Backup(cpio, orig);
                cpio.Mkdir(0, ".backup");
                cpio.Add(0, ".backup/.magisk", Encoding.ASCII.GetBytes(cfg.ToString()));

                var newRd = Compress.CompressAs(cpio.Dump(), fmt);
                log("  ramdisk חדש: " + Converter.Human(newRd.Length));
                var outImg = bi.Repack(newRd);
                return outImg;
            }
        }

        static readonly string[] VerityPat = { "verifyatboot", "verify", "avb_keys", "avb", "support_scfs", "fsverity" };
        static readonly string[] EncPat = { "forceencrypt", "forcefdeorfbe", "fileencryption" };

        static void PatchFstabs(Cpio c, MagiskOptions opt, Action<string> log)
        {
            foreach (var name in c.Entries.Keys.ToList())
            {
                var e = c.Entries[name];
                bool fstab = (!opt.KeepVerity || !opt.KeepForceEncrypt) && (e.Mode & Cpio.S_IFMT) == Cpio.S_IFREG
                    && !name.StartsWith(".backup") && !name.StartsWith("twrp") && !name.StartsWith("recovery") && name.StartsWith("fstab");
                if (!opt.KeepVerity)
                {
                    if (fstab) { log("  מתקן " + name + " (verity)"); e.Data = RemovePattern(e.Data, VerityPat); }
                    else if (name == "verity_key") { c.Entries.Remove(name); continue; }
                }
                if (!opt.KeepForceEncrypt && fstab) e.Data = RemovePattern(e.Data, EncPat);
            }
        }

        static byte[] RemovePattern(byte[] buf, string[] pats)
        {
            var o = new List<byte>(buf.Length);
            int read = 0;
            while (read < buf.Length)
            {
                int len = buf[read] == ',' ? 1 : 0;
                int found = -1;
                foreach (var p in pats)
                {
                    if (read + len + p.Length <= buf.Length && Encoding.ASCII.GetString(buf, read + len, p.Length) == p) { found = p.Length; break; }
                }
                if (read + len >= buf.Length) found = -1;
                if (found >= 0)
                {
                    len += found;
                    if (read + len < buf.Length && buf[read + len] == '=')
                        while (read + len < buf.Length && " \n,\0".IndexOf((char)buf[read + len]) < 0) len++;
                    read += len;
                }
                else { o.Add(buf[read]); read++; }
            }
            return o.ToArray();
        }

        static bool Same(byte[] a, byte[] b)
        {
            if (a.Length != b.Length) return false;
            for (int i = 0; i < a.Length; i++) if (a[i] != b[i]) return false;
            return true;
        }

        /// <summary>magiskboot "backup": stores changed/removed originals in .backup, new names in .backup/.rmlist.</summary>
        static void Backup(Cpio cur, Cpio orig)
        {
            orig.Rm(".backup", true);
            cur.Rm(".backup", true);
            var backups = new Dictionary<string, Cpio.Entry>();
            backups[".backup"] = new Cpio.Entry { Mode = Cpio.S_IFDIR };
            var rm = new StringBuilder();
            var names = new SortedSet<string>(orig.Entries.Keys.Concat(cur.Entries.Keys), StringComparer.Ordinal);
            foreach (var n in names)
            {
                Cpio.Entry le, re;
                bool l = orig.Entries.TryGetValue(n, out le), r = cur.Entries.TryGetValue(n, out re);
                if (l && (!r || !Same(le.Data, re.Data)))
                {
                    bool reg = (le.Mode & Cpio.S_IFMT) == Cpio.S_IFREG;
                    var copy = new Cpio.Entry { Mode = le.Mode, Uid = le.Uid, Gid = le.Gid, RdevMajor = le.RdevMajor, RdevMinor = le.RdevMinor, Data = reg ? XzWriter.Encode(le.Data) : le.Data };
                    backups[reg ? ".backup/" + n + ".xz" : ".backup/" + n] = copy;
                }
                else if (!l && r) rm.Append(n).Append('\0');
            }
            if (rm.Length > 0) backups[".backup/.rmlist"] = new Cpio.Entry { Mode = Cpio.S_IFREG, Data = Encoding.UTF8.GetBytes(rm.ToString()) };
            foreach (var kv in backups) cur.Entries[kv.Key] = kv.Value;
        }
    }
}
