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
                            var st = new Settings { SparseOutput = a.Length > 3 && a[3] == "sparse", RemoveWifi = Array.IndexOf(a, "nowifi") >= 0 };
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
