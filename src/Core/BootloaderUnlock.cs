using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace Ro2rwPc
{
    /// <summary>One way to unlock the bootloader. Each has its own warning, shown before it runs.</summary>
    public sealed class UnlockMethod
    {
        public string Name;
        public string Warning;
    }

    /// <summary>Bootloader unlock: tries mtkclient (DA, seccfg) first, then fastboot. Stops at the first success.</summary>
    public static class BootloaderUnlock
    {
        /// <summary>Arguments for mtk.exe. The serial/UsbDk switch is added by MtkClient.Run.</summary>
        public const string MtkArgs = "da seccfg unlock";
        /// <summary>Arguments for fastboot, after the device is in fastboot mode.</summary>
        public const string FastbootArgs = "flashing unlock";

        public static readonly UnlockMethod Mtk = new UnlockMethod
        {
            Name = "mtkclient (DA, seccfg)",
            Warning = "הפתיחה דרך mtkclient כותבת את מחיצת seccfg במכשיר (דרך ה-DA, בלי צורך במערכת).\n\n" +
                "! המכשיר עלול לאפס את הנתונים (userdata) בהפעלה הראשונה אחרי הפתיחה. לא אומת על חומרה.\n" +
                "! ייתכן שהאחריות תתבטל.\n" +
                "! מומלץ לגבות לפני.",
        };

        public static readonly UnlockMethod Fastboot = new UnlockMethod
        {
            Name = "fastboot flashing unlock",
            Warning = "הפתיחה דרך fastboot דורשת שהמכשיר יעלה למצב fastboot, ושה-OEM unlocking מופעל בהגדרות המפתחים.\n\n" +
                "! הפתיחה מוחקת את כל הנתונים (factory reset) - זו הדרך הרגילה של היצרן.\n" +
                "! ייתכן שהאחריות תתבטל.\n" +
                "! מומלץ לגבות לפני.",
        };

        /// <summary>The order in which the methods are tried.</summary>
        public static IList<UnlockMethod> Order() { return new List<UnlockMethod> { Mtk, Fastboot }; }

        /// <summary>Runs the methods in order. <paramref name="attempt"/> returns true on success. Returns the method that worked, or null if none did.</summary>
        public static UnlockMethod RunFirstThatWorks(Func<UnlockMethod, bool> attempt, Action<UnlockMethod> before = null)
        {
            foreach (var m in Order())
            {
                if (before != null) before(m);
                if (attempt(m)) return m;
            }
            return null;
        }

        /// <summary>Returns true when the fastboot output says the unlock worked.</summary>
        public static bool FastbootSucceeded(int exitCode, string output)
        {
            if (exitCode != 0) return false;
            string o = (output ?? "").ToLowerInvariant();
            return !o.Contains("fail") && !o.Contains("error") && !o.Contains("not allowed");
        }

        /// <summary>Finds fastboot.exe in the usual places, or null if none is installed.</summary>
        public static string FindFastboot()
        {
            var candidates = new List<string>
            {
                Path.Combine(Project.ToolsDir, "platform-tools", "fastboot.exe"),
                @"C:\RO2RW\tools\platform-tools\fastboot.exe",
            };
            var path = Environment.GetEnvironmentVariable("PATH") ?? "";
            candidates.AddRange(path.Split(';').Where(p => !string.IsNullOrWhiteSpace(p)).Select(p => Path.Combine(p.Trim(), "fastboot.exe")));
            return candidates.FirstOrDefault(File.Exists);
        }
    }
}
