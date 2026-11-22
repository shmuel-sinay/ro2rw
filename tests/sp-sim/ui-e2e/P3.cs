using System; using System.Collections.Generic; using System.Drawing; using System.IO; using System.Linq; using System.Runtime.InteropServices; using System.Text; using System.Threading; using System.Windows.Forms; using System.Reflection;
static class P3 {
  delegate bool EnumProc(IntPtr h, IntPtr l);
  [DllImport("user32.dll")] static extern bool EnumWindows(EnumProc p, IntPtr l);
  [DllImport("user32.dll")] static extern bool EnumChildWindows(IntPtr h, EnumProc p, IntPtr l);
  [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetClassName(IntPtr h, StringBuilder s, int n);
  [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetWindowText(IntPtr h, StringBuilder s, int n);
  [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
  [DllImport("user32.dll")] static extern IntPtr SendMessage(IntPtr h, uint m, IntPtr w, IntPtr l);
  static volatile bool run = true;
  static StringBuilder res = new StringBuilder();
  static object F(object o, string n) { return o.GetType().GetField(n, BindingFlags.NonPublic | BindingFlags.Instance).GetValue(o); }
  static void SetF(object o, string n, object v) { o.GetType().GetField(n, BindingFlags.NonPublic | BindingFlags.Instance).SetValue(o, v); }
  static void Ok(string name, bool cond, string extra = "") { res.AppendLine((cond ? "ok   " : "FAIL ") + name + (extra.Length > 0 ? "  [" + extra + "]" : "")); }
  static IEnumerable<Control> All(Control c) { foreach (Control x in c.Controls) { yield return x; foreach (var y in All(x)) yield return y; } }
  static Button Btn(Control root, string text) { return All(root).OfType<Button>().FirstOrDefault(b => b.Text == text); }
  static void ClickBoxes() {
    uint me = (uint)System.Diagnostics.Process.GetCurrentProcess().Id;
    while (run) {
      EnumWindows((h, l) => {
        uint pid; GetWindowThreadProcessId(h, out pid); if (pid != me) return true;
        var cls = new StringBuilder(64); GetClassName(h, cls, 64); if (cls.ToString() != "#32770") return true;
        EnumChildWindows(h, (c, l2) => {
          var cc = new StringBuilder(64); GetClassName(c, cc, 64); if (cc.ToString() != "Button") return true;
          var t = new StringBuilder(64); GetWindowText(c, t, 64); string s = t.ToString().Replace("&", "");
          if (s == "Yes" || s == "כן" || s == "OK" || s == "אישור") { SendMessage(c, 0xF5, IntPtr.Zero, IntPtr.Zero); return false; }
          return true; }, IntPtr.Zero);
        return true; }, IntPtr.Zero);
      Thread.Sleep(150);
    }
  }
  static bool single, preloader;
  static HashSet<Form> handled = new HashSet<Form>();
  static void Tick() {
    foreach (Form f in Application.OpenForms.Cast<Form>().ToList()) {
      if (f.GetType().Name == "SpDumpForm" && handled.Add(f)) {
        var grid = (DataGridView)F(f, "grid");
        if (grid.Rows.Count == 0) { ((TextBox)F(f, "tbChip")).Text = "MT6765"; Btn(f, "קרא טבלת מחיצות מהמכשיר").PerformClick(); }
        else {
          if (single) { ((RadioButton)F(f, "rbSingle")).Checked = true; if (preloader) ((CheckBox)F(f, "cbPre")).Checked = true; }
          Btn(f, "לעריכה").PerformClick();
          ((Button)F(f, "btnDump")).PerformClick();
        }
      } else if (f.AcceptButton is Button && f.Text == "SP Flash Tool" && handled.Add(f)) ((Button)f.AcceptButton).PerformClick();
    }
  }
  static bool Wait(Func<bool> c, int sec) { var end = DateTime.Now.AddSeconds(sec); while (DateTime.Now < end) { Application.DoEvents(); if (c()) return true; Thread.Sleep(40); } return false; }
  static bool Idle(Form form) { var w = (Thread)F(form, "worker"); var cancel = (Button)F(form, "btnCancel"); return (w == null || !w.IsAlive) && !cancel.Enabled; }
  static bool Bytes(string path, long len, byte fill) { if (!File.Exists(path)) return false; var b = File.ReadAllBytes(path); return b.Length == len && b.All(x => x == fill); }

  [STAThread] static int Main(string[] a) {
    Application.EnableVisualStyles();
    string sp = a[0];
    var t = new Thread(ClickBoxes) { IsBackground = true }; t.Start();
    var form = new Ro2rwPc.MainForm(); form.Show(); Application.DoEvents();
    SetF(form, "sptoolPath", Path.Combine(sp, "flash_tool.exe"));
    var timer = new System.Windows.Forms.Timer { Interval = 150 }; timer.Tick += (s, e) => Tick(); timer.Start();
    var mLoad = form.GetType().GetMethod("LoadProjects", BindingFlags.NonPublic | BindingFlags.Instance);
    var mDump = form.GetType().GetMethod("DumpRomSp", BindingFlags.NonPublic | BindingFlags.Instance);
    var mAuto = form.GetType().GetMethod("RunSpAuto", BindingFlags.NonPublic | BindingFlags.Instance);
    var log = (RichTextBox)F(form, "log");
    Func<string, Ro2rwPc.Project> fresh = n => { var p = Ro2rwPc.Project.Open(n); foreach (var f in Directory.GetFiles(p.Root)) File.Delete(f); foreach (var f in Directory.GetFiles(p.Edited)) File.Delete(f); return p; };

    Environment.SetEnvironmentVariable("FAKE_USER", "8"); Environment.SetEnvironmentVariable("FAKE_BOOT", "1");
    // S1: one file per partition (reads the partition table from the "device" first)
    var p1 = fresh("SpSim1"); mLoad.Invoke(form, new object[] { "SpSim1" });
    single = false; preloader = false; handled.Clear();
    mDump.Invoke(form, null);
    Wait(() => File.Exists(Path.Combine(p1.Root, "super.img")) && Idle(form), 90);
    Ok("S1 gpt.bin read from device", File.Exists(Path.Combine(p1.Root, "gpt.bin")));
    Ok("S1 probing found part-id 8 after 34 failed", (int)F(form, "spPart") == 8, "spPart=" + F(form, "spPart"));
    Ok("S1 boot_a content", Bytes(Path.Combine(p1.Root, "boot_a.img"), 64 * 512, (byte)'B'));
    Ok("S1 lk_a content", Bytes(Path.Combine(p1.Root, "lk_a.img"), 64 * 512, (byte)'L'));
    Ok("S1 vbmeta_a content", Bytes(Path.Combine(p1.Root, "vbmeta_a.img"), 64 * 512, (byte)'V'));
    Ok("S1 super content", Bytes(Path.Combine(p1.Root, "super.img"), 512 * 512, (byte)'S'));
    Ok("S1 userdata not dumped", !File.Exists(Path.Combine(p1.Root, "userdata.img")));
    var sc1 = Directory.GetFiles(p1.Root, "*scatter*.txt").ToList();
    Ok("S1 one scatter only", sc1.Count == 1, string.Join(",", sc1.Select(Path.GetFileName)));
    Ok("S1 scatter lists boot_a", sc1.Count > 0 && File.ReadAllText(sc1.Last()).Contains("partition_name: boot_a") && File.ReadAllText(sc1.Last()).Contains("Generated by RO2RW"));
    Ok("S1 backup manifest", File.Exists(Path.Combine(p1.Root, "backup.manifest")) && File.ReadAllText(Path.Combine(p1.Root, "backup.manifest")).Contains("boot_a.img"));
    Ok("S1 temp folder removed", !Directory.Exists(Path.Combine(p1.Root, "_sp_dump")));
    Ok("S1 success logged", log.Text.Contains("נשאבו 4 מחיצות"));

    Environment.SetEnvironmentVariable("FAKE_USER", "34"); Environment.SetEnvironmentVariable("FAKE_BOOT", "32"); SetF(form, "spPart", 0);
    // S2: one continuous dump, cut by the table, plus preloader (gpt already known: copy it)
    var p2 = fresh("SpSim2"); File.Copy(Path.Combine(p1.Root, "gpt.bin"), Path.Combine(p2.Root, "gpt.bin"));
    mLoad.Invoke(form, new object[] { "SpSim2" });
    single = true; preloader = true; handled.Clear();
    mDump.Invoke(form, null);
    Wait(() => File.Exists(Path.Combine(p2.Root, "super.img")) && Idle(form), 90);
    Ok("S2 boot_a content", Bytes(Path.Combine(p2.Root, "boot_a.img"), 64 * 512, (byte)'B'));
    Ok("S2 vbmeta_a content", Bytes(Path.Combine(p2.Root, "vbmeta_a.img"), 64 * 512, (byte)'V'));
    Ok("S2 super content", Bytes(Path.Combine(p2.Root, "super.img"), 512 * 512, (byte)'S'));
    Ok("S2 userdata not kept", !File.Exists(Path.Combine(p2.Root, "userdata.img")));
    { var pb = File.Exists(Path.Combine(p2.Root, "preloader.bin")) ? File.ReadAllBytes(Path.Combine(p2.Root, "preloader.bin")) : new byte[0]; Ok("S2 preloader saved (found via part-id 32, MMM magic)", pb.Length == 0x100000 && pb[0] == 0x4D && pb[3] == 0x01 && pb[4] == (byte)'P'); }
    Ok("S2 full dump removed", !Directory.Exists(Path.Combine(p2.Root, "_sp_dump")) && !File.Exists(Path.Combine(p2.Root, "full_dump.bin")));

    // S3: flash by partition name when there is no scatter at all
    var p3 = fresh("SpSim3"); File.Copy(Path.Combine(p1.Root, "gpt.bin"), Path.Combine(p3.Root, "gpt.bin"));
    File.WriteAllBytes(Path.Combine(p3.Edited, "boot.img"), new byte[1000]); File.WriteAllBytes(Path.Combine(p3.Edited, "lk.img"), new byte[2000]);
    mLoad.Invoke(form, new object[] { "SpSim3" });
    handled.Clear();
    mAuto.Invoke(form, new object[] { false });
    string flashed = Path.Combine(p3.Edited, "flashed.txt");
    Wait(() => File.Exists(flashed) && Idle(form), 60);
    string ft = File.Exists(flashed) ? File.ReadAllText(flashed).Replace("\r", "") : "";
    Ok("S3 flashed by name without scatter", ft.Contains("boot_a=1000") && ft.Contains("lk_a=2000"), ft.Replace("\n", "|"));
    string cfg3 = Path.Combine(p3.Edited, "sp_flash_byname_config.xml");
    Ok("S3 config is download-withoutScatter", File.Exists(cfg3) && File.ReadAllText(cfg3).Contains("<download-withoutScatter>"));

    // S4: the first two handshakes fail (STARTCMD_FAIL) - the app must retry by itself and still finish
    Environment.SetEnvironmentVariable("FAKE_USER", "34"); Environment.SetEnvironmentVariable("FAKE_BOOT", "32"); Environment.SetEnvironmentVariable("FAKE_FAIL_FIRST", "2");
    File.Delete(Path.Combine(Path.GetTempPath(), "fakesp_counter.txt")); SetF(form, "spPart", 0);
    var p4 = fresh("SpSim4"); mLoad.Invoke(form, new object[] { "SpSim4" });
    single = false; preloader = false; handled.Clear();
    mDump.Invoke(form, null);
    Wait(() => File.Exists(Path.Combine(p4.Root, "super.img")) && Idle(form), 120);
    Ok("S4 finished after two failed handshakes", Bytes(Path.Combine(p4.Root, "super.img"), 512 * 512, (byte)'S') && File.Exists(Path.Combine(p4.Root, "gpt.bin")));
    Ok("S4 retry was logged", log.Text.Contains("ניסיון 2 מתוך"));
    Environment.SetEnvironmentVariable("FAKE_FAIL_FIRST", "0");

    run = false; timer.Stop();
    res.AppendLine("--- LOG TAIL ---"); res.AppendLine(string.Join(" | ", log.Lines.Reverse().Take(14).Reverse())); File.WriteAllText("e2e.txt", res.ToString());
    form.Close(); return 0;
  }
}
