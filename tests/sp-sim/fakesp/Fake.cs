using System; using System.IO; using System.Linq; using System.Text; using System.Xml.Linq; using System.Globalization;
// Stand-in for SP Flash Tool's console mode: reads the same config XML and writes what the real tool would (synthetic 512-byte-sector disk).
static class Fake {
  const int SS = 512;
  static readonly object[][] P = {
    new object[]{"boot_a", 64, 127, (byte)'B'}, new object[]{"lk_a", 128, 191, (byte)'L'}, new object[]{"vbmeta_a", 192, 255, (byte)'V'},
    new object[]{"super", 256, 767, (byte)'S'}, new object[]{"userdata", 768, 2047, (byte)'U'} };
  static byte[] gpt;
  static void Init() {
    gpt = new byte[0x8000];
    Array.Copy(Encoding.ASCII.GetBytes("EFI PART"), 0, gpt, SS, 8);
    BitConverter.GetBytes(4095L).CopyTo(gpt, SS + 32); BitConverter.GetBytes(4062L).CopyTo(gpt, SS + 48);
    BitConverter.GetBytes(2L).CopyTo(gpt, SS + 72); BitConverter.GetBytes(128).CopyTo(gpt, SS + 80); BitConverter.GetBytes(128).CopyTo(gpt, SS + 84);
    for (int i = 0; i < P.Length; i++) { int o = 2 * SS + i * 128; gpt[o] = 0xAF;
      BitConverter.GetBytes((long)(int)P[i][1]).CopyTo(gpt, o + 32); BitConverter.GetBytes((long)(int)P[i][2]).CopyTo(gpt, o + 40);
      Encoding.Unicode.GetBytes((string)P[i][0]).CopyTo(gpt, o + 56); }
  }
  static byte At(long off) {
    if (off < gpt.Length && off >= 0) { bool inPart = P.Any(p => off >= (int)p[1] * SS && off < ((int)p[2] + 1) * SS); if (!inPart) return gpt[off]; }
    foreach (var p in P) if (off >= (int)p[1] * SS && off < ((int)p[2] + 1) * SS) return (byte)p[3];
    return 0;
  }
  static long Hex(string s) { return s.StartsWith("0x") ? Convert.ToInt64(s.Substring(2), 16) : long.Parse(s); }
  static int Main(string[] a) {
    Init();
    int failFirst = int.Parse(Environment.GetEnvironmentVariable("FAKE_FAIL_FIRST") ?? "0");
    string ctr = Path.Combine(Path.GetTempPath(), "fakesp_counter.txt");
    int runs = File.Exists(ctr) ? int.Parse(File.ReadAllText(ctr)) : 0;
    if (failFirst > 0 && runs < failFirst) { File.WriteAllText(ctr, (runs + 1).ToString()); Console.Write("Begin\r\nConnecting to BROM...\r\nConnect BROM failed: STATUS_BROM_CMD_STARTCMD_FAIL(-1073348607)\r\n"); return 1; }
    string cfg = null; for (int i = 0; i < a.Length; i++) if (a[i] == "-i") cfg = a[i + 1];
    if (cfg == null) { Console.Write("usage\r\n"); return 2; }
    var cmds = XDocument.Load(cfg).Root.Element("commands");
    Console.Write("Begin\r\nMediaTek SP Flash Tool v5.fake\r\nConnecting to BROM...\r\nBROM connected\r\n");
    var rb = cmds.Element("readback");
    if (rb != null) foreach (var it in rb.Element("readback-list").Elements("readback-rom-item")) {
      int part = int.Parse(it.Attribute("part-id").Value); long start = Hex(it.Attribute("start-address").Value), len = Hex(it.Attribute("readback-length").Value);
      int userId = int.Parse(Environment.GetEnvironmentVariable("FAKE_USER") ?? "34"), bootId = int.Parse(Environment.GetEnvironmentVariable("FAKE_BOOT") ?? "32");
      if (part != userId && part != bootId) { Console.Write("ERROR: bad part-id " + part + "\r\n"); continue; }
      using (var f = File.Create(it.Value)) { var buf = new byte[1 << 16]; for (long o = 0; o < len; o += buf.Length) { int n = (int)Math.Min(buf.Length, len - o); for (int k = 0; k < n; k++) buf[k] = part == bootId ? (byte)'P' : At(start + o + k); if (part == bootId && o == 0) { buf[0] = 0x4D; buf[1] = 0x4D; buf[2] = 0x4D; buf[3] = 0x01; } f.Write(buf, 0, n); } }
      Console.Write("50% of image data has been sent\r\n100% of image data has been sent\r\n");
    }
    var rn = cmds.Element("readback-withoutscatter");
    if (rn != null) foreach (var it in rn.Element("readback-list").Elements("readback-rom-item")) {
      var p = P.FirstOrDefault(x => (string)x[0] == it.Attribute("readback-partion").Value);
      if (p == null) { Console.Write("ERROR: partition not found " + it.Attribute("readback-partion").Value + "\r\n"); continue; }
      using (var f = File.Create(it.Value)) { var buf = Enumerable.Repeat((byte)p[3], ((int)p[2] - (int)p[1] + 1) * SS).ToArray(); f.Write(buf, 0, buf.Length); }
      Console.Write("100% of image data has been sent\r\n");
    }
    var dl = cmds.Element("download-withoutScatter");
    if (dl != null) foreach (var it in dl.Element("download-list").Elements("download-rom-item")) {
      File.AppendAllText(Path.Combine(Path.GetDirectoryName(cfg), "flashed.txt"), it.Attribute("partition").Value + "=" + new FileInfo(it.Value).Length + "\n");
      Console.Write("Download Succeeded.\r\n");
    }
    Console.Write("All command exec done!\r\n");
    return 0;
  }
}
