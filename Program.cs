namespace Recorder;

static class Program
{
    [STAThread]
    static void Main()
    {
        try
        {
            AppLog.Info("Starting Laptop Output Recorder.");
            ApplicationConfiguration.Initialize();
            Application.Run(new TrayApplicationContext());
            AppLog.Info("Laptop Output Recorder exited.");
        }
        catch (Exception ex)
        {
            AppLog.Error("Startup failed.", ex);
            MessageBox.Show(
                $"Laptop Output Recorder failed to start.\n\n{ex.Message}\n\nLog: {AppLog.FilePath}",
                "Laptop Output Recorder",
                MessageBoxButtons.OK,
                MessageBoxIcon.Error);
        }
    }    
}
