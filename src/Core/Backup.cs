using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

namespace Ro2rwPc
{
    public sealed class BackupFile { public string Name; public long Size; public string Sha256; }

    public sealed class BackupManifest
    {
        public DateTime CreatedUtc;
        public int RetentionHours;           // 0 = never delete
        public List<BackupFile> Files = new List<BackupFile>();
    }

    /// <summary>Project backup: SHA-256 manifest of the original images, expiry, and the set of originals to restore.</summary>
    public static class Backup
    {
        public const string FileName = "backup.manifest";

        /// <summary>A backup made by a device dump is kept this long, then deleted.</summary>
        public const int DumpRetentionHours = 2;

        public static string ManifestPath(string root) { return Path.Combine(root, FileName); }

        public static BackupManifest Load(string root)
        {
            string p = ManifestPath(root);
            if (!File.Exists(p)) return null;
            try
            {
                var lines = File.ReadAllLines(p, Encoding.UTF8);
                if (lines.Length == 0 || lines[0] != "RO2RW-BACKUP 1") return null;
                var m = new BackupManifest();
                foreach (var l in lines.Skip(1))
                {
                    int eq = l.IndexOf('=');
                    if (eq < 0) continue;
                    string k = l.Substring(0, eq), v = l.Substring(eq + 1);
                    if (k == "created") m.CreatedUtc = DateTime.Parse(v, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);
                    else if (k == "retention_hours") m.RetentionHours = int.Parse(v, CultureInfo.InvariantCulture);
                    else if (k == "file")
                    {
                        var parts = v.Split(new[] { '|' }, 3);
                        if (parts.Length == 3) m.Files.Add(new BackupFile { Sha256 = parts[0], Size = long.Parse(parts[1], CultureInfo.InvariantCulture), Name = parts[2] });
                    }
                }
                return m;
            }
            catch { return null; }
        }

        public static void Save(string root, BackupManifest m)
        {
            var sb = new StringBuilder();
            sb.AppendLine("RO2RW-BACKUP 1");
            sb.AppendLine("created=" + m.CreatedUtc.ToUniversalTime().ToString("o", CultureInfo.InvariantCulture));
            sb.AppendLine("retention_hours=" + m.RetentionHours.ToString(CultureInfo.InvariantCulture));
            foreach (var f in m.Files) sb.AppendLine("file=" + f.Sha256 + "|" + f.Size.ToString(CultureInfo.InvariantCulture) + "|" + f.Name);
            File.WriteAllText(ManifestPath(root), sb.ToString(), new UTF8Encoding(false));
        }

        /// <summary>Partition images sitting directly in the project folder (not gpt.bin, not scatter files).</summary>
        public static List<string> CandidateImages(string root)
        {
            return Directory.GetFiles(root)
                .Select(Path.GetFileName)
                .Where(n =>
                {
                    string ext = Path.GetExtension(n).ToLowerInvariant();
                    return (ext == ".img" || ext == ".bin") && !n.Equals("gpt.bin", StringComparison.OrdinalIgnoreCase) && n.IndexOf("scatter", StringComparison.OrdinalIgnoreCase) < 0;
                })
                .OrderBy(n => n, StringComparer.OrdinalIgnoreCase).ToList();
        }

        static string Hash(string path, long size, string label, Func<bool> cancelled, Action<string> progress)
        {
            using (var sha = SHA256.Create())
            using (var f = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20))
            {
                var buf = new byte[4 << 20];
                long done = 0; int n; int lastPct = -1;
                while ((n = f.Read(buf, 0, buf.Length)) > 0)
                {
                    if (cancelled != null && cancelled()) throw new OperationCanceledException();
                    sha.TransformBlock(buf, 0, n, null, 0);
                    done += n;
                    int pct = size > 0 ? (int)(done * 100 / size) : 100;
                    if (progress != null && pct != lastPct) { lastPct = pct; progress(label + " " + pct + "%"); }
                }
                sha.TransformFinalBlock(buf, 0, 0);
                return string.Concat(sha.Hash.Select(b => b.ToString("x2")));
            }
        }

        public static BackupManifest Create(string root, IEnumerable<string> names, int retentionHours, DateTime nowUtc, Func<bool> cancelled, Action<string> progress)
        {
            var m = new BackupManifest { CreatedUtc = nowUtc, RetentionHours = retentionHours };
            foreach (var name in names)
            {
                string p = Path.Combine(root, name);
                long size = new FileInfo(p).Length;
                m.Files.Add(new BackupFile { Name = name, Size = size, Sha256 = Hash(p, size, name, cancelled, progress) });
            }
            Save(root, m);
            return m;
        }

        /// <summary>Re-hashes the listed files. Returns "name:missing", "name:size" or "name:hash" per problem; empty = all good.</summary>
        public static List<string> Verify(string root, BackupManifest m, IEnumerable<string> onlyNames, Func<bool> cancelled)
        {
            var only = onlyNames == null ? null : new HashSet<string>(onlyNames, StringComparer.OrdinalIgnoreCase);
            var problems = new List<string>();
            foreach (var f in m.Files)
            {
                if (only != null && !only.Contains(f.Name)) continue;
                string p = Path.Combine(root, f.Name);
                if (!File.Exists(p)) { problems.Add(f.Name + ":missing"); continue; }
                if (new FileInfo(p).Length != f.Size) { problems.Add(f.Name + ":size"); continue; }
                if (!string.Equals(Hash(p, f.Size, f.Name, cancelled, null), f.Sha256, StringComparison.OrdinalIgnoreCase)) problems.Add(f.Name + ":hash");
            }
            return problems;
        }

        public static bool IsExpired(BackupManifest m, DateTime nowUtc)
        {
            return m.RetentionHours > 0 && nowUtc >= m.CreatedUtc.AddHours(m.RetentionHours);
        }

        public static TimeSpan? Remaining(BackupManifest m, DateTime nowUtc)
        {
            if (m.RetentionHours <= 0) return null;
            return m.CreatedUtc.AddHours(m.RetentionHours) - nowUtc;
        }

        /// <summary>Backed-up originals that have an edited counterpart (same file name) - what a restore flashes.</summary>
        public static List<string> RestoreSet(string root, string editedDir, BackupManifest m)
        {
            var edited = Directory.Exists(editedDir)
                ? new HashSet<string>(Directory.GetFiles(editedDir).Select(Path.GetFileName), StringComparer.OrdinalIgnoreCase)
                : new HashSet<string>();
            return m.Files.Where(f => edited.Contains(f.Name) && File.Exists(Path.Combine(root, f.Name))).Select(f => f.Name).ToList();
        }

        /// <summary>Deletes the files listed in the manifest (plain names inside the project folder only) and the manifest. Returns files deleted.</summary>
        public static int Expire(string root, BackupManifest m)
        {
            int n = 0;
            foreach (var f in m.Files)
            {
                if (f.Name != Path.GetFileName(f.Name)) continue;
                string p = Path.Combine(root, f.Name);
                try { if (File.Exists(p)) { File.Delete(p); n++; } } catch { }
            }
            try { File.Delete(ManifestPath(root)); } catch { }
            return n;
        }
    }
}
