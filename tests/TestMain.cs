using System;
using System.IO;
using System.Linq;
using Ro2rwPc;

static class TestMain
{
    static int Main(string[] a)
    {
        try
        {
            switch (a[0])
            {
                case "fs2ext4":
                    {
                        using (var src = new RawFileSource(a[1]))
                        {
                            IFsReader r = ErofsReader.Detect(src) ? (IFsReader)new ErofsReader(src) : new Ext4Reader(src);
                            var tree = r.ReadTree();
                            var w = new Ext4Writer(tree, "test");
                            long extra = a.Length > 3 ? long.Parse(a[3]) << 20 : 16 << 20;
                            Console.WriteLine("files=" + w.FileCount + " min=" + w.MinimalBytes);
                            using (var o = new FileStream(a[2], FileMode.Create))
                                Console.WriteLine("written=" + w.Write(o, 0, w.MinimalBytes + extra, r, p => { }, null));
                        }
                        return 0;
                    }
                case "super":
                    {
                        using (var c = new Converter())
                        {
                            c.Log = Console.WriteLine;
                            c.Open(a[1]);
                            var st = new Settings { SparseOutput = a.Length > 3 && a[3] == "sparse" };
                            c.Analyse(st);
                            c.Plan(st);
                            Console.WriteLine("out=" + c.Write(st, a[2]));
                        }
                        return 0;
                    }
                case "mksuper":
                    {
                        // mksuper out sizeMB name=img ...
                        long size = long.Parse(a[2]) << 20;
                        var md = new LpMetadata { MetadataMaxSize = 65536, MetadataSlotCount = 3, LogicalBlockSize = 4096, Major = 10, Minor = 2 };
                        md.BlockDevices.Add(new LpMetadata.BlockDevice { FirstLogicalSector = 2048, Alignment = 1 << 20, Size = (ulong)size, PartitionName = "super" });
                        md.Groups.Add(new LpMetadata.Group { Name = "default" });
                        md.Groups.Add(new LpMetadata.Group { Name = "main_a", MaximumSize = (ulong)(size - (4 << 20)) });
                        md.Groups.Add(new LpMetadata.Group { Name = "main_b", MaximumSize = (ulong)(size - (4 << 20)) });
                        long pos = 1 << 20;
                        var imgs = new System.Collections.Generic.List<System.Tuple<long, string>>();
                        for (int i = 3; i < a.Length; i++)
                        {
                            var kv = a[i].Split('=');
                            long len = (new FileInfo(kv[1]).Length + 4095) / 4096 * 4096;
                            var pa = new LpMetadata.Partition { Name = kv[0] + "_a", Attributes = 1, GroupIndex = 1 };
                            pa.Extents.Add(new LpMetadata.Extent { NumSectors = (ulong)(len / 512), TargetData = (ulong)(pos / 512) });
                            md.Partitions.Add(pa);
                            md.Partitions.Add(new LpMetadata.Partition { Name = kv[0] + "_b", Attributes = 1, GroupIndex = 2 });
                            imgs.Add(System.Tuple.Create(pos, kv[1]));
                            pos = (pos + len + (1 << 20) - 1) / (1 << 20) * (1 << 20);
                        }
                        using (var o = new FileStream(a[1], FileMode.Create))
                        {
                            o.SetLength(size);
                            md.WriteAll(o);
                            foreach (var t in imgs) { o.Position = t.Item1; var d = File.ReadAllBytes(t.Item2); o.Write(d, 0, d.Length); }
                        }
                        return 0;
                    }
                case "magisk":
                    {
                        var outb = MagiskPatcher.Patch(File.ReadAllBytes(a[1]), a[2], new MagiskOptions(), Console.WriteLine);
                        File.WriteAllBytes(a[3], outb);
                        return 0;
                    }
                case "lk":
                    {
                        var b = File.ReadAllBytes(a[1]);
                        var r = LkPatcher.Patch(b, true, true, Console.WriteLine);
                        File.WriteAllBytes(a[2], b);
                        Console.WriteLine("delay=" + r.DelayPatched + " text=" + r.TextsCleared);
                        return 0;
                    }
                case "vbmeta":
                    {
                        var b = File.ReadAllBytes(a[1]);
                        VbmetaPatcher.Patch(b, Console.WriteLine);
                        File.WriteAllBytes(a[2], b);
                        return 0;
                    }
                case "project":
                    {
                        // project <dir> <magisk.apk>
                        var proj = new Project { Name = Path.GetFileName(a[1]), Root = a[1] };
                        Directory.CreateDirectory(proj.Edited);
                        Directory.CreateDirectory(Project.ToolsDir);
                        File.Copy(a[2], Path.Combine(Project.ToolsDir, "Magisk-test.apk"), true);
                        var files = proj.Scan();
                        foreach (var f in files) Console.WriteLine(f.Name + " | " + f.Kind + " | " + f.Action);
                        var ed = new Editor { Log = Console.WriteLine };
                        int ok = ed.Run(proj, files, new EditSettings { Ro2rw = new Settings { SparseOutput = true } });
                        int want = files.Count(f => f.Selected && f.Action != EditAction.None);
                        Console.WriteLine("ok=" + ok + "/" + want);
                        return ok == want ? 0 : 1;
                    }
                case "dump":
                    {
                        var proj = new Project { Name = "Test_Device", Root = a[1] };
                        Directory.CreateDirectory(proj.Edited);
                        var mc = new MtkClient { Log = Console.WriteLine, Status = st => Console.WriteLine("[status] " + st) };
                        int n = mc.DumpProject(proj, true);
                        Console.WriteLine("moved=" + n);
                        foreach (var f in proj.Scan()) Console.WriteLine(f.Name + " | " + f.Kind + " | " + f.Action + " | sel=" + f.Selected);
                        return 0;
                    }
                case "classify":
                    {
                        var pf = new ProjectFile { Path = a[1], Size = new FileInfo(a[1]).Length };
                        Project.Classify(pf);
                        Console.WriteLine(pf.Kind + "|" + pf.Action);
                        return 0;
                    }
                case "scatter":
                    {
                        var proj = new Project { Name = "T", Root = a[1] };
                        Console.WriteLine(EditedScatter.Create(proj, Console.WriteLine) ?? "null");
                        return 0;
                    }
                case "flash":
                    {
                        // flash <dir> <gpt.bin|-> : plan + flash with whatever mtk.exe is in tools/
                        var items = Flasher.Plan(a[1], a[2] == "-" ? null : a[2]);
                        foreach (var it in items) Console.WriteLine("plan " + Path.GetFileName(it.File) + " -> " + it.Partition + " | " + (it.Problem ?? "ok") + (it.Sparse ? " | sparse" : ""));
                        new Flasher { Log = Console.WriteLine }.Flash(items, a[1], false);
                        return 0;
                    }
                case "vbmeta2":
                    {
                        var b = File.ReadAllBytes(a[1]);
                        VbmetaPatcher.Patch(b, int.Parse(a[3]), Console.WriteLine);
                        File.WriteAllBytes(a[2], b);
                        return 0;
                    }
                case "lk2":
                    {
                        var b = File.ReadAllBytes(a[1]);
                        var r = LkPatcher.Patch(b, true, false, int.Parse(a[3]), Console.WriteLine);
                        File.WriteAllBytes(a[2], b);
                        Console.WriteLine("delay=" + r.DelayPatched + " warn=" + r.WarningsPatched + " cats=" + string.Join(",", r.Categories.ToArray()));
                        return 0;
                    }
                case "hex":
                    {
                        var b = File.ReadAllBytes(a[1]);
                        int n = HexPatcher.Replace(b, a[3], a[4], a.Length > 5 && a[5] == "all", Console.WriteLine);
                        File.WriteAllBytes(a[2], b);
                        Console.WriteLine("n=" + n);
                        return 0;
                    }
                case "magisk2":
                    {
                        var outb = MagiskPatcher.Patch(File.ReadAllBytes(a[1]), a[2], new MagiskOptions { PatchVbmetaFlag = true, RecoveryMode = true }, Console.WriteLine);
                        File.WriteAllBytes(a[3], outb);
                        return 0;
                    }
                case "fastdump":
                    {
                        FastEngine.Selected = MtkEngine.Fast;
                        var proj = new Project { Name = "FD", Root = a[1] };
                        Directory.CreateDirectory(proj.Edited);
                        int n = FastEngine.DumpProject(proj, true, Console.WriteLine, st => {}, () => false);
                        Console.WriteLine("dumped=" + n);
                        return 0;
                    }
                case "fastflash":
                    {
                        FastEngine.Selected = MtkEngine.Fast;
                        var items = Flasher.Plan(a[1], a[2]);
                        var fl = new Flasher { Log = Console.WriteLine, GptPath = a[2] };
                        fl.Flash(items, a[1], false);
                        return 0;
                    }
                case "spconfig":
                    {
                        // spconfig <scatter> <filesDir> <daFile>
                        var roms = SpFlashTool.EnabledRoms(a[1], a[2]);
                        foreach (var r in roms) Console.WriteLine("ROM " + r.Index + " " + r.Name + " -> " + Path.GetFileName(r.File));
                        var cfg = SpFlashTool.BuildConfig(a[2], a[1], a[3], "MT6765", false, roms, null);
                        Console.WriteLine("CONFIG=" + cfg);
                        return 0;
                    }
                case "spreadback":
                    {
                        // spreadback <outDir> <daFile> <platform> [COMx]  -> writes sp_readback_config.xml; also a download config with the same COM
                        string com = a.Length > 4 ? a[4] : null;
                        var parts = SpFlashTool.DefaultReadbackPartitions;
                        Console.WriteLine("CONFIG=" + SpFlashTool.BuildReadbackConfig(a[1], a[1], a[2], a[3], false, parts, com));
                        return 0;
                    }
                case "spdlconfig":
                    {
                        // spdlconfig <outDir> <scatter> <daFile> [COMx]
                        var roms = SpFlashTool.EnabledRoms(a[2], a[1]);
                        Console.WriteLine("CONFIG=" + SpFlashTool.BuildConfig(a[1], a[2], a[3], "MT6765", false, roms, a.Length > 4 ? a[4] : null));
                        return 0;
                    }
                case "magisktag":
                    {
                        // magisktag <input> <tag>...  -> prints the resolved tag or "null"
                        Console.WriteLine(MagiskPatcher.ResolveTag(a[1], a.Skip(2)) ?? "null");
                        return 0;
                    }
                case "pumplines":
                    {
                        // pumplines <text with \r and \n escapes>  -> prints L:<log line> / S:<status line>
                        string text = a[1].Replace("\\r", "\r").Replace("\\n", "\n");
                        var cap = new System.Text.StringBuilder();
                        MtkClient.PumpLines(new StringReader(text), cap, s => Console.WriteLine("L:" + s.Trim()), s => Console.WriteLine("S:" + s.Trim()));
                        return 0;
                    }
                case "backuptest":
                    {
                        // backuptest <emptyDir>  -> scenario over a throwaway project folder, one result per line
                        string root = a[1], ed = Path.Combine(root, "ערוך");
                        Directory.CreateDirectory(ed);
                        File.WriteAllText(Path.Combine(root, "a.img"), "AAAA");
                        File.WriteAllText(Path.Combine(root, "b.img"), "BBBB");
                        File.WriteAllText(Path.Combine(root, "gpt.bin"), "GPT");
                        File.WriteAllText(Path.Combine(root, "x_scatter.txt"), "S");
                        File.WriteAllText(Path.Combine(ed, "a.img"), "EDITED");
                        var t0 = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
                        Console.WriteLine("candidates:" + string.Join(",", Backup.CandidateImages(root)));
                        var m = Backup.Create(root, Backup.CandidateImages(root), 24, t0, () => false, s => { });
                        Console.WriteLine("hash:" + m.Files.First(f => f.Name == "a.img").Sha256);
                        var m2 = Backup.Load(root);
                        Console.WriteLine("roundtrip:" + m2.RetentionHours + "," + m2.CreatedUtc.ToString("o") + "," + m2.Files.Count);
                        Console.WriteLine("verify1:" + string.Join(",", Backup.Verify(root, m2, null, () => false)));
                        Console.WriteLine("restoreset:" + string.Join(",", Backup.RestoreSet(root, ed, m2)));
                        File.WriteAllText(Path.Combine(root, "b.img"), "BBBX");
                        Console.WriteLine("verify2:" + string.Join(",", Backup.Verify(root, m2, null, () => false)));
                        Console.WriteLine("verify-only-a:" + string.Join(",", Backup.Verify(root, m2, new[] { "a.img" }, () => false)));
                        var never = new BackupManifest { CreatedUtc = t0, RetentionHours = 0 };
                        Console.WriteLine("expired:" + Backup.IsExpired(m2, t0.AddHours(23)) + "," + Backup.IsExpired(m2, t0.AddHours(24)) + "," + Backup.IsExpired(never, t0.AddYears(10)));
                        Console.WriteLine("remaining:" + Backup.Remaining(m2, t0.AddHours(6)).Value.TotalHours + "," + (Backup.Remaining(never, t0) == null));
                        int del = Backup.Expire(root, m2);
                        Console.WriteLine("expire:" + del + " gpt=" + File.Exists(Path.Combine(root, "gpt.bin")) + " edited=" + File.Exists(Path.Combine(ed, "a.img")) + " manifest=" + File.Exists(Backup.ManifestPath(root)) + " a=" + File.Exists(Path.Combine(root, "a.img")));
                        return 0;
                    }
                case "mustconvert":
                    {
                        // mustconvert <name> <fs> <size> <allNames...>
                        Console.WriteLine(Converter.MustConvert(a[1], a[2], long.Parse(a[3]), a.Skip(4)));
                        return 0;
                    }
                case "spdumptest":
                    {
                        // spdumptest <emptyDir>: synthetic 512-byte-sector disk with a GPT, then split / configs / scatter, one result per line
                        string dir = a[1]; Directory.CreateDirectory(dir);
                        const int ss = 512;
                        var parts = new[] { Tuple.Create("boot_a", 64, 127, (byte)'B'), Tuple.Create("lk_a", 128, 191, (byte)'L'), Tuple.Create("super", 192, 1215, (byte)'S'), Tuple.Create("userdata", 1216, 4000, (byte)'U') };
                        var disk = new byte[1216 * ss];
                        Array.Copy(System.Text.Encoding.ASCII.GetBytes("EFI PART"), 0, disk, ss, 8);
                        BitConverter.GetBytes(4095L).CopyTo(disk, ss + 32);
                        BitConverter.GetBytes(4062L).CopyTo(disk, ss + 48);
                        BitConverter.GetBytes(2L).CopyTo(disk, ss + 72);
                        BitConverter.GetBytes(128).CopyTo(disk, ss + 80);
                        BitConverter.GetBytes(128).CopyTo(disk, ss + 84);
                        for (int i = 0; i < parts.Length; i++)
                        {
                            int o = 2 * ss + i * 128;
                            disk[o] = 0xAF;   // non-zero type GUID
                            BitConverter.GetBytes((long)parts[i].Item2).CopyTo(disk, o + 32);
                            BitConverter.GetBytes((long)parts[i].Item3).CopyTo(disk, o + 40);
                            System.Text.Encoding.Unicode.GetBytes(parts[i].Item1).CopyTo(disk, o + 56);
                            for (int s = parts[i].Item2; s <= parts[i].Item3 && (s + 1) * ss <= disk.Length; s++)
                                for (int k = 0; k < ss; k++) disk[s * ss + k] = parts[i].Item4;
                        }
                        var gpt = Gpt.Parse(disk);
                        Console.WriteLine("gptok:" + SpDump.LooksLikeGpt(disk) + "," + SpDump.LooksLikeGpt(new byte[0x8000]) + "," + SpDump.LooksLikeGpt(null));
                        Console.WriteLine("range:" + SpDump.RangeLength(gpt, new[] { "boot_a", "super" }).ToString("x"));
                        string full = Path.Combine(dir, "full.bin"); File.WriteAllBytes(full, disk);
                        var skipped = new System.Collections.Generic.List<string>();
                        var written = SpDump.Split(full, gpt, new[] { "boot_a", "super", "userdata" }, Path.Combine(dir, "out"), () => false, p => { }, out skipped);
                        Console.WriteLine("split:" + string.Join(",", written) + " skipped=" + string.Join(",", skipped));
                        byte[] b = File.ReadAllBytes(Path.Combine(dir, "out", "boot_a.img")), sp = File.ReadAllBytes(Path.Combine(dir, "out", "super.img"));
                        Console.WriteLine("content:" + b.Length + "," + b.All(x => x == (byte)'B') + "," + sp.Length + "," + sp.All(x => x == (byte)'S') + "," + File.Exists(Path.Combine(dir, "out", "userdata.img")));
                        Console.WriteLine("preset:" + string.Join(",", new[] { "boot_a", "userdata", "vbmeta_system_b", "vendor_a", "super", "lk", "BOOT_A", "recovery" }.Select(n => SpDump.IsEditPartition(n))));
                        string da = Path.Combine(dir, "DA.bin"); File.WriteAllText(da, "x");
                        string rc = SpDump.BuildRangeConfig(dir, "rb.xml", da, "MT6765", false, null, null, new[] { new SpRange { PartId = 8, Start = 0, Length = SpDump.GptReadBytes, OutPath = Path.Combine(dir, "gpt.bin") } });
                        string rtext = File.ReadAllText(rc);
                        Console.WriteLine("rangecfg:" + rtext.Contains("<readback>") + "," + rtext.Contains("part-id=\"8\"") + "," + rtext.Contains("readback-length=\"0x0000000000008000\"") + "," + rtext.Contains("start-address=\"0x0000000000000000\"") + "," + rtext.Contains("<readback-withoutscatter>"));
                        string sc2 = SpDump.WriteMinimalScatter(dir, "MT6765", false);
                        string r2 = File.ReadAllText(SpDump.BuildRangeConfig(dir, "rb2.xml", da, "MT6765", false, null, sc2, new[] { new SpRange { PartId = 34, Start = 0, Length = 0x8000, OutPath = Path.Combine(dir, "g.bin") } }));
                        Console.WriteLine("rangecfg2:" + r2.Contains("<rom index=\"0\" enable=\"false\">") + "," + File.Exists(Path.Combine(dir, "placeholder.img")) + "," + r2.Contains("MT6765_empty_scatter.txt"));
                        string fc = SpDump.BuildByNameFlashConfig(dir, da, "MT6765", false, "COM5", new[] { new System.Collections.Generic.KeyValuePair<string, string>("boot_a", Path.Combine(dir, "out", "boot_a.img")) });
                        string ftext = File.ReadAllText(fc);
                        Console.WriteLine("flashcfg:" + ftext.Contains("<download-withoutScatter>") + "," + ftext.Contains("partition=\"boot_a\"") + "," + ftext.Contains("BromUART") + "," + ftext.Contains("<da-download-all"));
                        string sc = ScatterWriter.Write(dir, "t", gpt, "MT6765", false, n => File.Exists(Path.Combine(dir, "out", n + ".img")) ? n + ".img" : null);
                        var ent = SpDump.ReadScatter(sc);
                        Console.WriteLine("scatter:" + string.Join(",", ent.Select(e => e.Name)));
                        Console.WriteLine("scatterdl:" + string.Join(",", ent.Where(e => e.Download).Select(e => e.Name)));
                        Console.WriteLine("scattersize:" + ent.First(e => e.Name == "super").Start.ToString("x") + "," + ent.First(e => e.Name == "super").Size.ToString("x"));
                        Console.WriteLine("enabled:" + SpFlashTool.EnabledRoms(sc, Path.Combine(dir, "out")).Count);
                        string ms = SpDump.WriteMinimalScatter(dir, "MT6765", false);
                        Console.WriteLine("minimal:" + string.Join(",", SpDump.ReadScatter(ms).Select(e => e.Name)) + "," + Path.GetFileName(ms));
                        Console.WriteLine("partids:" + string.Join(",", SpDump.UserPartIds(false)) + "|" + string.Join(",", SpDump.UserPartIds(true)) + "|" + string.Join(",", SpDump.Boot1PartIds));
                        Console.WriteLine("preloader:" + SpDump.LooksLikePreloader(new byte[] { 0x4D, 0x4D, 0x4D, 0x01, 0 }) + "," + SpDump.LooksLikePreloader(new byte[] { 1, 2, 3, 4, 5 }) + "," + SpDump.LooksLikePreloader(null));
                        return 0;
                    }
                case "ls":
                    using (var src = new RawFileSource(a[1]))
                    {
                        IFsReader r = ErofsReader.Detect(src) ? (IFsReader)new ErofsReader(src) : new Ext4Reader(src);
                        foreach (var e in FsUtil.Walk(r.ReadTree()))
                            Console.WriteLine(e.Path + " " + e.Inode.Type + " " + e.Inode.Size + " " + string.Join(",", e.Inode.Xattrs.Select(x => x.Name)));
                    }
                    return 0;
            }
        }
        catch (Exception ex) { Console.WriteLine("ERROR: " + ex); return 1; }
        return 2;
    }
}
