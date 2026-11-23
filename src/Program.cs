namespace MaxScanning;

internal static class Program
{
    [STAThread]
    static int Main(string[] args)
    {
        System.Runtime.GCSettings.LatencyMode = System.Runtime.GCLatencyMode.SustainedLowLatency;
        if (args.Contains("--selftest")) return SelfTest.Run(args);
        if (Cli.IsCli(args)) return Cli.Run(args);
        ApplicationConfiguration.Initialize();
        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
        Application.ThreadException += (_, e) => MessageBox.Show(e.Exception.ToString(), "Max Scanning", MessageBoxButtons.OK, MessageBoxIcon.Error);
        Application.Run(new UI.MainForm(args));
        return 0;
    }
}
