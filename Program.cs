namespace IWCoyoteBridge;

internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        ApplicationConfiguration.Initialize();
        if (args.Contains("--relay-diagnostic"))
        {
            var lines = new List<string>();
            var result = Task.Run(() => Coyote.RelayDiagnostics.RunAsync(message => lines.Add(DateTime.Now.ToString("HH:mm:ss") + " " + message))).GetAwaiter().GetResult();
            lines.Add(result);
            File.WriteAllLines(Path.Combine(AppContext.BaseDirectory, "relay-diagnostic.txt"), lines);
            Environment.ExitCode = result.Contains("Target ID：✓") ? 0 : 1;
            return;
        }
        if (args.Contains("--test-window"))
        {
            using var fixture = new Form { Text = args.Contains("--plain-fixture") ? "Ordinary Window - no counter" : args.Contains("--custom-fixture")
                ? "I Wanna Scanner Fixture | 死亡：42" : "I Wanna Scanner Fixture | Deaths: 42", ClientSize = new Size(360, 80) };
            using var timeout = new System.Windows.Forms.Timer { Interval = 15000 };
            timeout.Tick += (_, _) => fixture.Close();
            timeout.Start();
            Application.Run(fixture);
            return;
        }
        if (args.Contains("--self-test"))
        {
            Environment.ExitCode = SelfTests.Run();
            return;
        }
        Application.Run(new MainForm());
    }
}
