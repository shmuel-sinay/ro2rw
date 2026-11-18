using System;
using System.Windows.Forms;

namespace Ro2rwPc
{
    static class Program
    {
        public const string Version = "2.10.1";

        [STAThread]
        static void Main(string[] args)
        {
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.ThreadException += (s, e) => ShowError(e.Exception);
            AppDomain.CurrentDomain.UnhandledException += (s, e) => ShowError(e.ExceptionObject as Exception);
            var f = new MainForm();
            if (Array.IndexOf(args, "--wizard") >= 0) f.Shown += (s, e) => f.BeginInvoke(new Action(f.OpenWizard));
            Application.Run(f);
        }

        static void ShowError(Exception ex)
        {
            MessageBox.Show("שגיאה לא צפויה:\n" + (ex != null ? ex.Message : "?"), "RO2RW PC",
                MessageBoxButtons.OK, MessageBoxIcon.Error, MessageBoxDefaultButton.Button1, MessageBoxOptions.RtlReading | MessageBoxOptions.RightAlign);
        }
    }
}
