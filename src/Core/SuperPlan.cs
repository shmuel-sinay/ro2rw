using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace Ro2rwPc
{
    /// <summary>Per-partition RO2RW plan for one super file, stored next to it as "&lt;file&gt;.ro2rw-plan.txt".</summary>
    public sealed class SuperPlan
    {
        public sealed class Entry { public bool Convert; public int ExtraMb; }
        public readonly Dictionary<string, Entry> Parts = new Dictionary<string, Entry>(StringComparer.Ordinal);

        public static string PathFor(string superFile) { return superFile + ".ro2rw-plan.txt"; }

        public static SuperPlan Load(string superFile)
        {
            string p = PathFor(superFile);
            if (!File.Exists(p)) return null;
            var plan = new SuperPlan();
            foreach (var raw in File.ReadAllLines(p))
            {
                var l = raw.Trim();
                if (l.Length == 0 || l.StartsWith("#")) continue;
                var kv = l.Split('=');
                if (kv.Length != 2) continue;
                var v = kv[1].Split(',');
                int mb = 0;
                if (v.Length > 1) int.TryParse(v[1], out mb);
                plan.Parts[kv[0].Trim()] = new Entry { Convert = v[0].Trim() == "convert", ExtraMb = Math.Max(0, mb) };
            }
            return plan;
        }

        public void Save(string superFile)
        {
            var sb = new StringBuilder();
            sb.AppendLine("# RO2RW PC - תכנון מחיצות עבור " + Path.GetFileName(superFile));
            sb.AppendLine("# partition=convert|copy,extraMB");
            foreach (var kv in Parts) sb.AppendLine(kv.Key + "=" + (kv.Value.Convert ? "convert" : "copy") + "," + kv.Value.ExtraMb);
            File.WriteAllText(PathFor(superFile), sb.ToString(), new UTF8Encoding(true));
        }

        /// <summary>Applies the plan to settings + converter selection (call after Open, before Analyse).</summary>
        public void Apply(Converter c, Settings st)
        {
            st.ConvertOnly = new HashSet<string>(Parts.Where(p => p.Value.Convert).Select(p => p.Key));
            st.PerPartitionMb = Parts.ToDictionary(p => p.Key, p => p.Value.ExtraMb);
            st.MaxMode = false;
            foreach (var p in c.Parts)
                if (p.Size > 0 && (p.Fs == "EROFS" || p.Fs == "EXT4")) p.Convert = st.ConvertOnly.Contains(p.Name);
        }
    }
}
