namespace RouteFinder.App;

internal static class Program
{
    [STAThread]
    static void Main(string[] args)
    {
        ApplicationConfiguration.Initialize();
        if (args.Contains("--self-test"))
        {
            var output = args.SkipWhile(x => x != "--self-test").Skip(1).FirstOrDefault() ?? Path.Combine(AppContext.BaseDirectory, "self-test.json");
            Environment.ExitCode = SelfTest.Run(output);
            return;
        }
        using var instance = RouteFinder.WinUI.LaunchInstance.Acquire(RouteFinder.WinUI.LaunchInstance.SharedName, () => { });
        if (instance is null)
        {
            MessageBox.Show("RouteFinder is already open.", "RouteFinder");
            return;
        }
        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
        Application.ThreadException += (_, e) => MessageBox.Show(e.Exception.Message, "RouteFinder", MessageBoxButtons.OK, MessageBoxIcon.Error);
        UiLanguage.Load();
        Application.Run(new MainForm());
    }
}
