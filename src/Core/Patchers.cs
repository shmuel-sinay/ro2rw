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
        public static bool Patch(byte[] b, Action<string> log) { return Patch(b, 3, log); }

        /// <param name="flags">1 = disable verity (hashtree), 2 = disable verification, 3 = both.</param>
        public static bool Patch(byte[] b, int flags, Action<string> log)
        {
            if (!IsVbmeta(b)) throw new InvalidDataException("קובץ vbmeta לא חוקי (חסר AVB0)");
            flags &= 3;
            if (flags == 0) flags = 3;
            int before = b[123] & 3;
            b[123] |= (byte)flags;   // flags (big endian u32 @120): 1 HASHTREE_DISABLED | 2 VERIFICATION_DISABLED
            log("  דגלי AVB: " + Describe(before) + " → " + Describe(b[123] & 3));
            return before != (b[123] & 3);
        }

        public static string Describe(int f)
        {
            if ((f & 3) == 3) return "verity כבוי + verification כבוי";
            if ((f & 3) == 1) return "verity כבוי, verification פעיל";
            if ((f & 3) == 2) return "verification כבוי, verity פעיל";
            return "פעיל (verity + verification)";
        }
    }

    // ======================================================================= LK
    /// <summary>MTK LK: removes the 5 second Orange State delay and/or the warning text.</summary>
    public static class LkPatcher
    {
        public sealed class Result { public int DelayPatched; public int TextsCleared; public int WarningsPatched; public string AndroidHint = ""; public List<string> Categories = new List<string>(); }

        // MTK lk warning/verify patterns (source: R0rt1z2/lkpatcher patches.json, GPLv3).
        // Each needle's first bytes are overwritten with 00207047 = "movs r0,#0 ; bx lr" (return 0, neuter the routine).
        static readonly string[][] WarnPatterns =
        {
            new[] { "fastboot_unlock", "2de9f04fadf5ac5d", "00207047" },
            new[] { "fastboot_unlock", "f0b5adf5925d",     "00207047" },
            new[] { "dm_verity",       "30b583b002ab0022", "00207047" },
            new[] { "dm_verity",       "022800d0704738b53224e0f7", "00207047" },
            new[] { "orange_state",    "08b50a4b7b441b681b68022b", "00207047" },
            new[] { "orange_state",    "08b50e4b7b441b681b68022b", "00207047" },
            new[] { "red_state",       "f0b5002489b0",     "00207047" },
        };

        static byte[] Hex(string h)
        {
            var r = new byte[h.Length / 2];
            for (int i = 0; i < r.Length; i++) r[i] = System.Convert.ToByte(h.Substring(i * 2, 2), 16);
            return r;
        }

        // 08 B5 ?? ?? 7B 44 1B 68 1B 68 0[1|2] 2B  ->  08 B5 00 20 08 BD 1B 68 1B 68 0[1|2] 2B
        static readonly byte[] Suffix = { 0x7B, 0x44, 0x1B, 0x68, 0x1B, 0x68 };
        static readonly string[] TextKeys = { "orange state", "has been unlocked", "be trusted", "will boot in", "boot in 5", "power off in", "verify fail" };

        public static Result Patch(byte[] b, bool removeDelay, bool removeText, Action<string> log) { return Patch(b, removeDelay, removeText, 0, log); }

        /// <param name="android">0 = auto (both patterns), 9 = Android 9 and below (01), 10 = Android 10+ (02).</param>
        public static Result Patch(byte[] b, bool removeDelay, bool removeText, int android, Action<string> log)
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
                    if ((android == 9 && b[i + 10] != 0x01) || (android == 10 && b[i + 10] != 0x02)) continue;
                    res.AndroidHint = b[i + 10] == 0x02 ? "אנדרואיד 10 ומעלה" : "אנדרואיד 9 ומטה";
                    log(string.Format("  נמצא קוד ההשהיה ב-0x{0:X} ({1})", i, res.AndroidHint));
                    b[i + 2] = 0x00; b[i + 3] = 0x20; b[i + 4] = 0x08; b[i + 5] = 0xBD;
                    res.DelayPatched++;
                    i += 11;
                }
                if (res.DelayPatched == 0) log("  ! קוד ההשהיה (5 שניות) לא נמצא. ייתכן שהקובץ כבר תוקן או שהמכשיר לא נתמך.");

                // dm-verity / fastboot-unlock / red/orange state routines (אחראים גם על "לחץ כפתור כדי להמשיך")
                foreach (var pat in WarnPatterns)
                {
                    var needle = Hex(pat[1]); var patch = Hex(pat[2]);
                    for (int i = 0; i + needle.Length <= b.Length; i++)
                    {
                        bool ok = true;
                        for (int k = 0; k < needle.Length; k++) if (b[i + k] != needle[k]) { ok = false; break; }
                        if (!ok) continue;
                        for (int k = 0; k < patch.Length; k++) b[i + k] = patch[k];
                        res.WarningsPatched++;
                        if (!res.Categories.Contains(pat[0])) res.Categories.Add(pat[0]);
                        log(string.Format("  נוטרלה אזהרת {0} ב-0x{1:X}", pat[0], i));
                        i += needle.Length - 1;
                    }
                }
                if (res.WarningsPatched > 0) log("  סוגי אזהרות שטופלו: " + string.Join(", ", res.Categories.ToArray()));
                else log("  (לא נמצאו דפוסי dm_verity/fastboot/red_state - ייתכן שכבר מתוקנים או לא קיימים בדגם)");
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

        /// <summary>True only for a real LK image: MTK section header whose name is "lk" (other MTK images share the magic).</summary>
        public static bool LooksLikeLk(byte[] b)
        {
            if (b.Length < 40 || BitConverter.ToUInt32(b, 0) != 0x58881688) return false;
            int n = 0; while (n < 32 && b[8 + n] != 0) n++;
            string name = Encoding.ASCII.GetString(b, 8, n).ToLowerInvariant();
            return name == "lk" || name == "lk_a" || name == "lk_b";
        }
    }

    // ======================================================================= Magisk
    public sealed class MagiskOptions
    {
        public bool KeepVerity, KeepForceEncrypt;
        public bool RecoveryMode;      // RECOVERYMODE: devices whose boot has no ramdisk for Android (Magisk in recovery)
        public bool PatchVbmetaFlag;   // PATCHVBMETAFLAG: disable verification in the boot image's own AVB footer vbmeta
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
            // built-in Magisk (embedded at build time)
            var asm = System.Reflection.Assembly.GetExecutingAssembly();
            using (var rs = asm.GetManifestResourceStream("Magisk.apk"))
            {
                if (rs != null)
                {
                    string ver = "";
                    using (var vs = asm.GetManifestResourceStream("Magisk.version"))
                        if (vs != null) ver = new StreamReader(vs).ReadToEnd().Trim();
                    string dest0 = Path.Combine(toolsDir, "builtin", "Magisk-" + (ver.Length > 0 ? ver : "builtin") + ".apk");
                    Directory.CreateDirectory(Path.GetDirectoryName(dest0));
                    if (!File.Exists(dest0) || new FileInfo(dest0).Length != rs.Length)
                        using (var f = File.Create(dest0)) rs.CopyTo(f);
                    log("  Magisk מובנה: " + Path.GetFileName(dest0));
                    return dest0;
                }
            }
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

        /// <summary>Downloads a specific Magisk version (e.g. "26.4" or "v26.4") from GitHub into toolsDir and returns its path.</summary>
        public static string DownloadApkVersion(string toolsDir, string version, Action<string> log)
        {
            Directory.CreateDirectory(toolsDir);
            string tag = version.Trim();
            if (tag.Length == 0) throw new ArgumentException("לא הוזנה גרסה");
            if (!tag.StartsWith("v", StringComparison.OrdinalIgnoreCase)) tag = "v" + tag;
            try { ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12; } catch { }
            log("  מוריד Magisk " + tag + " מ-GitHub...");
            string json;
            using (var wc = new WebClient())
            {
                wc.Headers.Add("User-Agent", "RO2RW-PC");
                try { json = wc.DownloadString("https://api.github.com/repos/topjohnwu/Magisk/releases/tags/" + tag); }
                catch (WebException) { throw new IOException("לא נמצאה גרסת Magisk בשם " + tag + ". בדוק את מספר הגרסה (למשל 26.4) בדף המהדורות של Magisk."); }
            }
            var m = Regex.Match(json, "\"browser_download_url\"\\s*:\\s*\"([^\"]+/Magisk-v[^\"]+\\.apk)\"");
            if (!m.Success) throw new IOException("לא נמצא קובץ apk במהדורת " + tag);
            string url = m.Groups[1].Value;
            string dest = Path.Combine(toolsDir, url.Substring(url.LastIndexOf('/') + 1));
            using (var wc = new WebClient()) { wc.Headers.Add("User-Agent", "RO2RW-PC"); wc.DownloadFile(url, dest + ".part"); }
            if (File.Exists(dest)) File.Delete(dest);
            File.Move(dest + ".part", dest);
            // make it the newest apk so FindOrDownloadApk picks it
            File.SetLastWriteTimeUtc(dest, DateTime.UtcNow);
            log("  נבחר: " + Path.GetFileName(dest));
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

            using (var z = ZipFile.OpenRead(apkPath))
            {
                string script = Encoding.UTF8.GetString(ZipGet(z, "assets/boot_patch.sh") ?? new byte[0]);

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
                if (abi == "auto")
                {
                    string why;
                    abi = DetectAbi(cpio, bi, out why);
                    log("  ABI: " + abi + " (" + why + ")");
                }
                foreach (var f in new[] { "sbin/launch_daemonsu.sh", "sbin/su", "init.xposed.rc", "boot/sbin/launch_daemonsu.sh" })
                    if (cpio.Exists(f)) throw new InvalidDataException("ה-boot נערך כבר על ידי כלי אחר. השתמש ב-boot מקורי.");
                foreach (var f in new[] { ".backup/.magisk", "init.magisk.rc", "overlay/init.magisk.rc" })
                    if (cpio.Exists(f)) throw new InvalidDataException("ה-boot כבר עבר Magisk. השתמש ב-boot מקורי (לא ערוך).");
                log("  זוהה boot מקורי");

                var magiskinit = ZipGet(z, "lib/" + abi + "/libmagiskinit.so");
                if (magiskinit == null) throw new InvalidDataException("ב-Magisk.apk אין קבצים עבור " + abi);
                var magisk = ZipGet(z, "lib/" + abi + "/libmagisk.so");
                var magisk64 = ZipGet(z, "lib/" + abi + "/libmagisk64.so");
                var magisk32 = ZipGet(z, "lib/" + abi + "/libmagisk32.so") ?? (abi == "arm64-v8a" ? ZipGet(z, "lib/armeabi-v7a/libmagisk32.so") : abi == "x86_64" ? ZipGet(z, "lib/x86/libmagisk32.so") : null);
                var initld = ZipGet(z, "lib/" + abi + "/libinit-ld.so");
                var stub = ZipGet(z, "assets/stub.apk");
                bool newScheme = script.Contains("overlay.d/sbin/magisk.xz") || (magisk != null && magisk64 == null);

                string sha1;
                using (var h = SHA1.Create()) sha1 = BitConverter.ToString(h.ComputeHash(boot)).Replace("-", "").ToLowerInvariant();
                var orig = cpio.Clone();

                var cfg = new StringBuilder();
                cfg.Append("KEEPVERITY=" + (opt.KeepVerity ? "true" : "false") + "\n");
                cfg.Append("KEEPFORCEENCRYPT=" + (opt.KeepForceEncrypt ? "true" : "false") + "\n");
                cfg.Append("RECOVERYMODE=" + (opt.RecoveryMode ? "true" : "false") + "\n");
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
                if (opt.PatchVbmetaFlag) PatchFooterVbmeta(outImg, log);
                return outImg;
            }
        }

        static string ElfAbi(byte[] d)
        {
            if (d == null || d.Length < 20 || d[0] != 0x7F || d[1] != 'E' || d[2] != 'L' || d[3] != 'F') return null;
            int mach = BitConverter.ToUInt16(d, 18);
            switch (mach)
            {
                case 0xB7: return "arm64-v8a";
                case 0x28: return "armeabi-v7a";
                case 0x3E: return "x86_64";
                case 0x03: return "x86";
            }
            return null;
        }

        /// <summary>Userspace ABI = architecture of the ramdisk's init (what Magisk binaries must match).</summary>
        public static string DetectAbi(Cpio cpio, BootImage bi, out string why)
        {
            foreach (var path in new[] { "init", "system/bin/init", "first_stage_ramdisk/system/bin/init" })
            {
                Cpio.Entry e;
                if (cpio.Entries.TryGetValue(path, out e) && (e.Mode & Cpio.S_IFMT) == Cpio.S_IFREG)
                {
                    var abi = ElfAbi(e.Data);
                    if (abi != null) { why = "לפי /" + path + " שב-ramdisk"; return abi; }
                }
            }
            // any ELF in the ramdisk
            foreach (var kv in cpio.Entries)
            {
                if ((kv.Value.Mode & Cpio.S_IFMT) != Cpio.S_IFREG) continue;
                var abi = ElfAbi(kv.Value.Data);
                if (abi != null) { why = "לפי /" + kv.Key + " שב-ramdisk"; return abi; }
            }
            // kernel (raw or gzip/lz4)
            byte[] k = bi.Kernel;
            try
            {
                var f = Compress.Detect(k);
                if (f == RamdiskFormat.Gzip || f == RamdiskFormat.Lz4Legacy || f == RamdiskFormat.Lz4Frame) k = Compress.Decompress(k, f);
            }
            catch { }
            if (k.Length > 0x40 && Encoding.ASCII.GetString(k, 0x38, 4) == "ARM\x64") { why = "לפי ה-kernel (ARM64)"; return "arm64-v8a"; }
            why = "לא זוהה - ברירת מחדל. אם המערכת 32 ביט בחר armeabi-v7a ידנית";
            return "arm64-v8a";
        }

        /// <summary>PATCHVBMETAFLAG: sets the flags of the vbmeta blob referenced by the AVB footer.</summary>
        public static void PatchFooterVbmeta(byte[] img, Action<string> log)
        {
            if (img.Length < 64 || Encoding.ASCII.GetString(img, img.Length - 64, 4) != "AVBf") { log("  ! אין AVB footer ב-boot - PATCHVBMETAFLAG דולג"); return; }
            long off = 0; for (int i = 0; i < 8; i++) off = (off << 8) | img[img.Length - 64 + 20 + i];
            if (off < 0 || off + 128 > img.Length || !VbmetaPatcher.IsVbmeta(Slice(img, off, 256))) { log("  ! vbmeta ב-footer לא נמצא"); return; }
            img[off + 123] |= 3;
            log("  PATCHVBMETAFLAG: האימות ב-vbmeta הפנימי של ה-boot כובה");
        }

        static byte[] Slice(byte[] b, long o, int n) { var r = new byte[Math.Min(n, b.Length - o)]; Buffer.BlockCopy(b, (int)o, r, 0, r.Length); return r; }

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

    // ======================================================================= generic HEX find/replace
    /// <summary>Hex find/replace with wildcards ("??" in the pattern matches any byte; "??" in the replacement keeps the byte).</summary>
    public static class HexPatcher
    {
        public static int[] Parse(string hex)
        {
            hex = Regex.Replace(hex ?? "", @"[\s,-]", "").ToUpperInvariant();
            if (hex.Length == 0 || hex.Length % 2 != 0) throw new FormatException("מחרוזת HEX חייבת להכיל מספר זוגי של תווים (למשל 08B5??207B44)");
            var r = new int[hex.Length / 2];
            for (int i = 0; i < r.Length; i++)
            {
                string h = hex.Substring(i * 2, 2);
                if (h == "??" || h == "**") r[i] = -1;
                else r[i] = Convert.ToInt32(h, 16);
            }
            return r;
        }

        public static List<long> Find(byte[] b, int[] pat, int max = int.MaxValue)
        {
            var res = new List<long>();
            int first = Array.FindIndex(pat, x => x >= 0);
            for (int i = 0; i + pat.Length <= b.Length && res.Count < max; i++)
            {
                if (first >= 0 && b[i + first] != pat[first]) continue;
                bool ok = true;
                for (int k = 0; k < pat.Length; k++) if (pat[k] >= 0 && b[i + k] != pat[k]) { ok = false; break; }
                if (ok) res.Add(i);
            }
            return res;
        }

        /// <returns>number of replacements.</returns>
        public static int Replace(byte[] b, string find, string replace, bool all, Action<string> log)
        {
            var f = Parse(find); var r = Parse(replace);
            if (r.Length != f.Length) throw new FormatException("החיפוש וההחלפה חייבים להיות באותו אורך (" + f.Length + " בתים מול " + r.Length + ")");
            var hits = Find(b, f, all ? int.MaxValue : 1);
            foreach (var at in hits)
            {
                for (int k = 0; k < r.Length; k++) if (r[k] >= 0) b[at + k] = (byte)r[k];
                log(string.Format("  הוחלף ב-0x{0:X}", at));
            }
            if (hits.Count == 0) log("  לא נמצאה התאמה");
            return hits.Count;
        }
    }
}
