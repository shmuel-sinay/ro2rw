using System;
using System.Windows.Forms;

namespace Ro2rwPc
{
    static class Program
    {
        public const string Version = "1.1.0";

        [STAThread]
        static void Main()
        {
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.ThreadException += (s, e) => ShowError(e.Exception);
            AppDomain.CurrentDomain.UnhandledException += (s, e) => ShowError(e.ExceptionObject as Exception);
            Application.Run(new MainForm());
        }

        static void ShowError(Exception ex)
        {
            MessageBox.Show("שגיאה לא צפויה:\n" + (ex != null ? ex.Message : "?"), "RO2RW PC",
                MessageBoxButtons.OK, MessageBoxIcon.Error, MessageBoxDefaultButton.Button1, MessageBoxOptions.RtlReading | MessageBoxOptions.RightAlign);
        }
    }
}
