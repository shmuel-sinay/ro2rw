using System;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Text;
using System.Threading;
using System.Windows.Forms;

namespace Ro2rwPc
{
    public sealed partial class MainForm
    {
        /// <summary>Opens the bootloader. The user picks the method first: mtkclient (then fastboot if it fails), or fastboot only. Each shown warning comes before the run.</summary>
        void UnlockBootloader()
        {
            var choice = AskUnlockChoice();
            if (choice == null) return;
            var methods = BootloaderUnlock.Order(choice.Value);
            var text = new StringBuilder("פתיחת בוטלודר. הדרך שנבחרה:\n\n");
            foreach (var m in methods) text.Append("• " + m.Name + "\n" + m.Warning + "\n\n");
            if (MessageBox.Show(this, text.Append("להמשיך?").ToString(), "פתיחת בוטלודר", MessageBoxButtons.OKCancel, MessageBoxIcon.Warning,
                MessageBoxDefaultButton.Button2, MessageBoxOptions.RtlReading | MessageBoxOptions.RightAlign) != DialogResult.OK) return;
            cancel = false;
            SetBusy(true);
            worker = new Thread(() =>
            {
                try
                {
                    Log("");
                    Log("▶ פתיחת בוטלודר", Color.LightSkyBlue);
                    var won = BootloaderUnlock.RunFirstThatWorks(m =>
                    {
                        Log("  מנסה: " + m.Name + " ...");
                        if (m == BootloaderUnlock.Mtk) return UnlockViaMtk();
                        return UnlockViaFastboot();
                    }, null, choice.Value);
                    if (won == null) Log("✖ הפתיחה לא הצליחה. המכשיר לא השתנה לפי הלוג.", Color.Khaki);
                    else Log("✔ הבוטלודר נפתח דרך " + won.Name, Color.LightGreen);
                }
                catch (OperationCanceledException) { Log("בוטל.", Color.Khaki); }
                catch (Exception ex) { Log("✖ " + ex.Message); }
                finally { SetStatus(""); SetBusy(false); }
            }) { IsBackground = true };
            worker.Start();
        }

        /// <summary>null = the user closed the question.</summary>
        BootloaderUnlock.Choice? AskUnlockChoice()
        {
            var r = MessageBox.Show(this,
                "איך לפתוח את הבוטלודר?\n\n" +
                "כן (מומלץ): mtkclient דרך ה-DA. אם לא מזוהה תוך " + (int)BootloaderUnlock.MtkWaitLimit.TotalMinutes + " דקות, עוברים ל-fastboot.\n" +
                "לא: fastboot בלבד (המכשיר חייב לעלות ל-fastboot, ואפשר לבחור את זה מראש).\n" +
                "ביטול: לא לפתוח.",
                "פתיחת בוטלודר", MessageBoxButtons.YesNoCancel, MessageBoxIcon.Question, MessageBoxDefaultButton.Button1,
                MessageBoxOptions.RtlReading | MessageBoxOptions.RightAlign);
            if (r == DialogResult.Yes) return BootloaderUnlock.Choice.Auto;
            if (r == DialogResult.No) return BootloaderUnlock.Choice.FastbootOnly;
            return null;
        }

        bool UnlockViaMtk()
        {
            string exe;
            try { exe = MtkClient.EnsureExe(); }
            catch (Exception ex) { Log("  " + ex.Message, Color.Khaki); return false; }
            var deadline = DateTime.UtcNow + BootloaderUnlock.MtkWaitLimit;
            bool timedOut = false;
            var runner = new MtkClient
            {
                Log = s => Log(s), Status = SetStatus,
                Cancelled = () => { if (DateTime.UtcNow > deadline) timedOut = true; return cancel || timedOut; }
            };
            var cap = new StringBuilder();
            int rc;
            try { rc = runner.Run(BootloaderUnlock.MtkArgs, Path.GetDirectoryName(exe), cap); }
            catch (OperationCanceledException) when (timedOut && !cancel)
            {
                Log("  mtkclient לא זיהה את המכשיר תוך " + (int)BootloaderUnlock.MtkWaitLimit.TotalMinutes + " דקות. ממשיכים לדרך הבאה.", Color.Khaki);
                return false;
            }
            Log("  mtkclient יצא עם קוד " + rc, rc == 0 ? Color.LightGreen : Color.Khaki);
            return rc == 0;
        }

        bool UnlockViaFastboot()
        {
            var fb = BootloaderUnlock.FindFastboot();
            if (fb == null) { Log("  fastboot.exe לא נמצא (לא ב-platform-tools ולא ב-PATH). מדלגים.", Color.Khaki); return false; }
            var psi = new ProcessStartInfo(fb, BootloaderUnlock.FastbootArgs)
            { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true, WorkingDirectory = Path.GetDirectoryName(fb) };
            var output = new StringBuilder();
            using (var p = Process.Start(psi))
            {
                p.OutputDataReceived += (s, e) => { if (e.Data != null) { output.AppendLine(e.Data); Log("  " + e.Data); } };
                p.ErrorDataReceived += (s, e) => { if (e.Data != null) { output.AppendLine(e.Data); Log("  " + e.Data); } };
                p.BeginOutputReadLine(); p.BeginErrorReadLine();
                p.WaitForExit();
                return BootloaderUnlock.FastbootSucceeded(p.ExitCode, output.ToString());
            }
        }
    }
}
