using System; using System.Drawing; using System.IO; using System.Windows.Forms; using System.Threading;
static class P4 {
  static void Shot(Form f, string path) {
    f.StartPosition = FormStartPosition.Manual; f.Location = new Point(0, 0); f.Show(); Application.DoEvents(); Thread.Sleep(400); Application.DoEvents();
    var b = new Bitmap(f.Width, f.Height); f.DrawToBitmap(b, new Rectangle(0, 0, f.Width, f.Height)); b.Save(path); f.Close();
  }
  [STAThread] static int Main(string[] a) {
    Application.EnableVisualStyles();
    string dir = a[0];
    var proj = Ro2rwPc.Project.Open("SpSim1");
    var gpt = Ro2rwPc.Gpt.Parse(File.ReadAllBytes(Path.Combine(proj.Root, "gpt.bin")));
    Shot(new Ro2rwPc.SpDumpForm(gpt, "MT6765", false, "", 0), Path.Combine(dir, "spdump.png"));
    Shot(new Ro2rwPc.SpDumpForm(null, "", false, "", 0), Path.Combine(dir, "spdump-empty.png"));
    Shot(new Ro2rwPc.ScatterToolForm(proj.Name, proj.Root, gpt, "MT6765", false), Path.Combine(dir, "scattertool.png"));
    return 0;
  }
}
