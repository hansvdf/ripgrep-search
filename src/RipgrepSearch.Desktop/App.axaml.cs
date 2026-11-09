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
            // macOS Quit and OS shutdown must use the same cancel-and-await path as window close.
            desktop.ShutdownRequested += (_, e) =>
            {
                if (desktop.MainWindow is MainWindow window && window.ViewModel.IsSearching)
                {
                    e.Cancel = true;
                    window.Close();
                }
            };
        }
        base.OnFrameworkInitializationCompleted();
    }
}
