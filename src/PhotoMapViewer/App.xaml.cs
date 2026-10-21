using System.Windows;
using System.Windows.Threading;

namespace PhotoMapViewer;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        DispatcherUnhandledException += OnUnhandled;
        base.OnStartup(e);
    }

    void OnUnhandled(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        // Don't let one odd file take the whole slideshow down: log it and carry on.
        try
        {
            var log = System.IO.Path.Combine(PhotoMap.Core.AppSettings.Folder, "errors.log");
            System.IO.Directory.CreateDirectory(PhotoMap.Core.AppSettings.Folder);
            System.IO.File.AppendAllText(log, $"{DateTime.Now:u}  {e.Exception}\n\n");
        }
        catch { }
        e.Handled = true;
    }
}
