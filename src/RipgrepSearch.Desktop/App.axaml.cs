using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;

namespace RipgrepSearch.Desktop;

public sealed partial class App : Application
{
    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            desktop.MainWindow = new MainWindow(new SearchViewModel(desktop.Args?.FirstOrDefault()));
            // Quit/shutdown must await both search cleanup and settings writes.
            desktop.ShutdownRequested += (_, e) =>
            {
                if (desktop.MainWindow is MainWindow window && !window.IsCloseReady)
                {
                    e.Cancel = true;
                    window.Close();
                }
            };
        }
        base.OnFrameworkInitializationCompleted();
    }
}
