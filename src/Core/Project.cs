using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace Ro2rwPc
{
    public enum EditAction { None, Ro2rw, Vbmeta, Lk, Magisk }

    public sealed class ProjectFile
    {
        public string Path;
        public string Name { get { return System.IO.Path.GetFileName(Path); } }
        public long Size;
        public string Kind = "";
        public EditAction Action;
        public bool Selected;
        public bool Edited;   // exists in "ערוך"
    }

    public sealed class Project
    {
        public const string EditedDir = "ערוך";
        public string Name;
        public string Root;
        public string Edited { get { return System.IO.Path.Combine(Root, EditedDir); } }

        public static string BaseDir
        {
            get { return System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Projects"); }
        }
        public static string ToolsDir
        {
            get { return System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "tools"); }
        }

        public static List<string> List()
        {
            if (!Directory.Exists(BaseDir)) return new List<string>();
            return Directory.GetDirectories(BaseDir).Select(System.IO.Path.GetFileName).OrderBy(x => x).ToList();
        }

        public static string SafeName(string n)
        {
            foreach (var c in System.IO.Path.GetInvalidFileNameChars()) n = n.Replace(c, '_');
            return n.Trim().TrimEnd('.');
        }

        public static Project Open(string name)
        {
            name = SafeName(name);
            if (name.Length == 0) throw new ArgumentException("שם פרויקט ריק");
            var p = new Project { Name = name, Root = System.IO.Path.Combine(BaseDir, name) };
            Directory.CreateDirectory(p.Root);
            Directory.CreateDirectory(p.Edited);
            return p;
        }

        public List<ProjectFile> Scan()
        {
            var list = new List<ProjectFile>();
            foreach (var f in Directory.GetFiles(Root).OrderBy(x => x, StringComparer.OrdinalIgnoreCase))
            {
                var pf = new ProjectFile { Path = f, Size = new FileInfo(f).Length };
                Classify(pf);
                pf.Edited = File.Exists(System.IO.Path.Combine(Edited, pf.Name));
                list.Add(pf);
            }
            // default selection: slot A / unslotted only, main vbmeta only, init_boot instead of boot when present
            bool hasInitBoot = list.Any(f => f.Kind == "init_boot");
            foreach (var pf in list)
            {
                string bn = System.IO.Path.GetFileNameWithoutExtension(pf.Name).ToLowerInvariant();
                pf.Selected = pf.Action != EditAction.None
                    && !bn.EndsWith("_b")
                    && !(pf.Action == EditAction.Vbmeta && bn.StartsWith("vbmeta_"))
                    && !(pf.Action == EditAction.Magisk && pf.Kind == "boot" && hasInitBoot);
            }
            return list;
        }

        static byte[] Head(string path, int n)
        {
            using (var fs = File.OpenRead(path))
            {
                var b = new byte[Math.Min(n, fs.Length)];
                fs.Read(b, 0, b.Length);
                return b;
            }
        }

        public static void Classify(ProjectFile pf)
        {
            string n = pf.Name.ToLowerInvariant();
            byte[] h;
            try { h = Head(pf.Path, 8192); } catch { pf.Kind = "?"; return; }
            bool sparse = h.Length >= 4 && BitConverter.ToUInt32(h, 0) == SparseSource.Magic;
            if (n.StartsWith("super") || (h.Length > 4100 && BitConverter.ToUInt32(h, 4096) == LpMetadata.GeometryMagic))
            {
                pf.Kind = "super" + (sparse ? " (sparse)" : ""); pf.Action = EditAction.Ro2rw; return;
            }
            if (VbmetaPatcher.IsVbmeta(h)) { pf.Kind = "vbmeta"; pf.Action = EditAction.Vbmeta; return; }
            if (n == "gpt.bin" || n.EndsWith("_scatter.txt") || n == "mtkclient_log.txt") { pf.Kind = n == "gpt.bin" ? "GPT" : "מידע"; return; }
            if (BootImage.IsBoot(h))
            {
                pf.Kind = n.StartsWith("init_boot") ? "init_boot" : n.StartsWith("recovery") ? "recovery" : "boot";
                pf.Action = (n.StartsWith("boot") || n.StartsWith("init_boot")) ? EditAction.Magisk : EditAction.None;
                return;
            }
            if (n.StartsWith("lk") || LkPatcher.LooksLikeLk(h)) { pf.Kind = "lk"; pf.Action = EditAction.Lk; return; }
            if (h.Length > 8 && Encoding.ASCII.GetString(h, 0, 8) == "VNDRBOOT") { pf.Kind = "vendor_boot"; return; }
            if (n.EndsWith(".txt") || n.EndsWith(".xml")) { pf.Kind = "טקסט"; return; }
            pf.Kind = "";
        }

        public static string ActionName(EditAction a)
        {
            switch (a)
            {
                case EditAction.Ro2rw: return "RO2RW - קריאה וכתיבה";
                case EditAction.Vbmeta: return "ביטול אימות AVB";
                case EditAction.Lk: return "הסרת Orange State";
                case EditAction.Magisk: return "Root עם Magisk";
            }
            return "-";
        }
    }

    public sealed class EditSettings
    {
        public Settings Ro2rw = new Settings();
        public MagiskOptions Magisk = new MagiskOptions();
        public bool LkDelay = true, LkText = true;
    }

    /// <summary>Runs the selected edits; output goes to the project's "ערוך" folder with the same file names.</summary>
    public sealed class Editor
    {
        public Action<string> Log = s => { };
        public Action<int> Progress = p => { };
        public Func<bool> Cancelled = () => false;

        public int Run(Project proj, List<ProjectFile> files, EditSettings st)
        {
            var todo = files.Where(f => f.Selected && f.Action != EditAction.None).ToList();
            int ok = 0, idx = 0;
            Directory.CreateDirectory(proj.Edited);
            foreach (var f in todo)
            {
                if (Cancelled()) throw new OperationCanceledException();
                int baseP = idx * 1000 / todo.Count, span = 1000 / todo.Count;
                Log("");
                Log("▶ " + f.Name + " - " + Project.ActionName(f.Action));
                string outPath = Path.Combine(proj.Edited, f.Name);
                try
                {
                    switch (f.Action)
                    {
                        case EditAction.Vbmeta:
                            {
                                var b = File.ReadAllBytes(f.Path);
                                VbmetaPatcher.Patch(b, Log);
                                File.WriteAllBytes(outPath, b);
                                break;
                            }
                        case EditAction.Lk:
                            {
                                var b = File.ReadAllBytes(f.Path);
                                var r = LkPatcher.Patch(b, st.LkDelay, st.LkText, Log);
                                if (r.DelayPatched == 0 && r.TextsCleared == 0) throw new InvalidDataException("לא נמצא מה לתקן בקובץ ה-LK");
                                File.WriteAllBytes(outPath, b);
                                break;
                            }
                        case EditAction.Magisk:
                            {
                                var apk = MagiskPatcher.FindOrDownloadApk(Project.ToolsDir, Log);
                                var b = File.ReadAllBytes(f.Path);
                                var outb = MagiskPatcher.Patch(b, apk, st.Magisk, Log);
                                File.WriteAllBytes(outPath, outb);
                                break;
                            }
                        case EditAction.Ro2rw:
                            {
                                using (var c = new Converter())
                                {
                                    c.Log = Log;
                                    c.Cancelled = Cancelled;
                                    c.Progress = p => Progress(baseP + p * span / 1000);
                                    c.Open(f.Path);
                                    c.Analyse(st.Ro2rw);
                                    c.Plan(st.Ro2rw);
                                    c.Write(st.Ro2rw, proj.Edited, f.Name);
                                }
                                break;
                            }
                    }
                    Log("  ✔ נשמר: " + Project.EditedDir + "\\" + f.Name);
                    ok++;
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex)
                {
                    Log("  ✖ שגיאה: " + ex.Message);
                    try { if (File.Exists(outPath) && f.Action != EditAction.Ro2rw) File.Delete(outPath); } catch { }
                }
                idx++;
                Progress(idx * 1000 / todo.Count);
            }
            if (ok > 0)
            {
                Log("");
                try { EditedScatter.Create(proj, Log); } catch (Exception ex) { Log("! יצירת scatter נכשלה: " + ex.Message); }
            }
            return ok;
        }
    }
}
