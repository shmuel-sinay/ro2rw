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
        /// <summary>Opens the bootloader: mtkclient (DA, seccfg) first, then fastboot. Each method shows its own warning before it runs.</summary>
        void UnlockBootloader()
        {
            var ok = MessageBox.Show(this,
                "פתיחת בוטלודר. הסדר: mtkclient (DA) ואז fastboot. אם הראשונה נכשלת, עוברים לשנייה.\n\n" +
                "1. " + BootloaderUnlock.Mtk.Warning + "\n\n" +
                "2. " + BootloaderUnlock.Fastboot.Warning + "\n\n" +
                "להמשיך?",
                "פתיחת בוטלודר", MessageBoxButtons.OKCancel, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2, MessageBoxOptions.RtlReading | MessageBoxOptions.RightAlign);
            if (ok != DialogResult.OK) return;
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
                    });
                    if (won == null) Log("✖ אף דרך לא הצליחה. המכשיר לא השתנה לפי הלוג.", Color.Khaki);
                    else Log("✔ הבוטלודר נפתח דרך " + won.Name, Color.LightGreen);
                }
                catch (OperationCanceledException) { Log("בוטל.", Color.Khaki); }
                catch (Exception ex) { Log("✖ " + ex.Message); }
                finally { SetStatus(""); SetBusy(false); }
            }) { IsBackground = true };
            worker.Start();
        }

        bool UnlockViaMtk()
        {
            string exe;
            try { exe = MtkClient.EnsureExe(); }
            catch (Exception ex) { Log("  " + ex.Message, Color.Khaki); return false; }
            var runner = new MtkClient { Log = s => Log(s), Status = SetStatus, Cancelled = () => cancel };
            var cap = new StringBuilder();
            int rc = runner.Run(BootloaderUnlock.MtkArgs, Path.GetDirectoryName(exe), cap);
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
