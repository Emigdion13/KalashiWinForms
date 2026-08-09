namespace KalashiWinForms;

static class Program
{
    /// <summary>
    ///  The main entry point for the application.
    /// </summary>
    [STAThread]
    static void Main()
    {
        ApplicationConfiguration.Initialize();
        try
        {
            Application.Run(new MainForm());
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Startup error: {ex.Message}\n\n{ex.StackTrace}", "Fatal Error",
                MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }    
}