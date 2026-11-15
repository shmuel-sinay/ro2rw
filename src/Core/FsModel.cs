using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace Ro2rwPc
{
    public sealed class Xattr
    {
        public string Name;   // full name e.g. "security.selinux"
        public byte[] Value;
        public Xattr(string n, byte[] v) { Name = n; Value = v; }
    }

    public enum NodeType { File, Dir, Symlink, CharDev, BlockDev, Fifo, Socket }

    /// <summary>Filesystem-independent description of one inode (shared by hard links).</summary>
    public sealed class FsInode
    {
        public NodeType Type;
        public uint Mode;          // full st_mode incl. type bits
        public uint Uid, Gid;
        public long Mtime;
        public long Size;
        public List<Xattr> Xattrs = new List<Xattr>();
        public string LinkTarget;
        public uint RdevMajor, RdevMinor;
        public object SourceRef;   // reader-specific handle
        public byte[] OverrideData; // replaced content (patched files)
        public int LinkCount;      // filled by tree builder
        // writer-specific
        public uint NewIno;
    }

    public sealed class FsEntry
    {
        public string Name;
        public FsInode Inode;
        public List<FsEntry> Children; // for dirs
        public FsEntry Parent;
        public string Path
        {
            get
            {
                if (Parent == null) return "/";
                var p = Parent.Path;
                return (p == "/" ? "" : p) + "/" + Name;
            }
        }
    }

    public interface IFsReader
    {
        string FsName { get; }
        FsEntry ReadTree();
        /// <summary>Streams the content of a regular file (or long symlink) to the sink.</summary>
        void ReadData(FsInode inode, Action<byte[], int, int> sink);
    }

    public static class FsUtil
    {
        public const uint S_IFMT = 0xF000, S_IFSOCK = 0xC000, S_IFLNK = 0xA000, S_IFREG = 0x8000, S_IFBLK = 0x6000,
            S_IFDIR = 0x4000, S_IFCHR = 0x2000, S_IFIFO = 0x1000;

        public static NodeType TypeFromMode(uint mode)
        {
            switch (mode & S_IFMT)
            {
                case S_IFDIR: return NodeType.Dir;
                case S_IFLNK: return NodeType.Symlink;
                case S_IFCHR: return NodeType.CharDev;
                case S_IFBLK: return NodeType.BlockDev;
                case S_IFIFO: return NodeType.Fifo;
                case S_IFSOCK: return NodeType.Socket;
                default: return NodeType.File;
            }
        }

        static readonly string[] Prefixes = { "", "user.", "system.posix_acl_access", "system.posix_acl_default", "trusted.", "lustre.", "security.", "system.", "system.richacl" };

        /// <summary>Common ext4/erofs xattr name index → prefix.</summary>
        public static string PrefixFor(int idx)
        {
            if (idx >= 0 && idx < Prefixes.Length) return Prefixes[idx];
            return "";
        }

        /// <summary>Splits full name into ext4 (index, suffix).</summary>
        public static void SplitName(string full, out int idx, out string suffix)
        {
            if (full == "system.posix_acl_access") { idx = 2; suffix = ""; return; }
            if (full == "system.posix_acl_default") { idx = 3; suffix = ""; return; }
            if (full.StartsWith("user.")) { idx = 1; suffix = full.Substring(5); return; }
            if (full.StartsWith("trusted.")) { idx = 4; suffix = full.Substring(8); return; }
            if (full.StartsWith("security.")) { idx = 6; suffix = full.Substring(9); return; }
            if (full.StartsWith("system.")) { idx = 7; suffix = full.Substring(7); return; }
            idx = 0; suffix = full;
        }

        public static void CountLinks(FsEntry root)
        {
            var stack = new Stack<FsEntry>();
            stack.Push(root);
            var seen = new HashSet<FsInode>();
            while (stack.Count > 0)
            {
                var e = stack.Pop();
                if (seen.Add(e.Inode)) e.Inode.LinkCount = 0;
                e.Inode.LinkCount++;
                if (e.Children != null) foreach (var c in e.Children) stack.Push(c);
            }
        }

        public static IEnumerable<FsEntry> Walk(FsEntry root)
        {
            var stack = new Stack<FsEntry>();
            stack.Push(root);
            while (stack.Count > 0)
            {
                var e = stack.Pop();
                yield return e;
                if (e.Children != null) for (int i = e.Children.Count - 1; i >= 0; i--) stack.Push(e.Children[i]);
            }
        }

        public static string Utf8(byte[] b, int off, int len) { return Encoding.UTF8.GetString(b, off, len); }
    }

    public static class Lz4
    {
        /// <summary>Decodes an LZ4 block until <paramref name="outLen"/> bytes are produced or input ends.</summary>
        public static int Decode(byte[] src, int sp, int srcEnd, byte[] dst, int dp, int outLen)
        {
            int dEnd = dp + outLen, d0 = dp;
            while (sp < srcEnd && dp < dEnd)
            {
                int token = src[sp++];
                int lit = token >> 4;
                if (lit == 15)
                {
                    int b;
                    do { if (sp >= srcEnd) throw new InvalidDataException("LZ4: truncated"); b = src[sp++]; lit += b; } while (b == 255);
                }
                int n = Math.Min(lit, dEnd - dp);
                if (sp + n > srcEnd) throw new InvalidDataException("LZ4: literal overrun");
                Buffer.BlockCopy(src, sp, dst, dp, n);
                sp += lit; dp += n;
                if (dp >= dEnd || sp >= srcEnd) break;
                if (sp + 2 > srcEnd) throw new InvalidDataException("LZ4: truncated offset");
                int off = src[sp] | (src[sp + 1] << 8); sp += 2;
                if (off == 0 || dp - off < d0) throw new InvalidDataException("LZ4: bad offset");
                int ml = token & 15;
                if (ml == 15)
                {
                    int b;
                    do { if (sp >= srcEnd) throw new InvalidDataException("LZ4: truncated"); b = src[sp++]; ml += b; } while (b == 255);
                }
                ml += 4;
                int m = dp - off;
                int cnt = Math.Min(ml, dEnd - dp);
                if (off >= cnt) { Buffer.BlockCopy(dst, m, dst, dp, cnt); dp += cnt; }
                else for (int i = 0; i < cnt; i++) dst[dp++] = dst[m++];
            }
            return dp - d0;
        }
    }
}
