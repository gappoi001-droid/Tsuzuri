using VNsave.Core;

namespace VNsave.App;

internal static class Program
{
    [STAThread]
    private static void Main()
    {
        ApplicationConfiguration.Initialize();
        try
        {
            var known = KnownFolders.Windows();
            using var defaults = new StreamReader(UiTheme.Resource("appsettings.json"));
            using var runtime = new AppRuntime(Path.Combine(known.Paths["Local"], "VNsave"), defaults.ReadToEnd(), known);
            UiTheme.Load(runtime.Repository.Root);
            Application.Run(new MainForm(runtime));
        }
        catch (Exception error) { MessageBox.Show(new SecretRedactor().FormatError(error), "Tsuzuri", MessageBoxButtons.OK, MessageBoxIcon.Error); }
    }
}
