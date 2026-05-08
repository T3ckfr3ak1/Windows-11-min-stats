namespace TaskbarResourceMonitor;

static class Program
{
    [STAThread]
    static void Main()
    {
        ApplicationConfiguration.Initialize();
        using var single = new SingleInstance("TaskbarResourceMonitor.SingleInstance");
        if (!single.IsPrimaryInstance) return;

        Application.Run(new TrayContext());
    }    
}