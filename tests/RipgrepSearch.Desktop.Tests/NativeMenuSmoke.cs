using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;
using RipgrepSearch.Desktop;

internal static partial class Tests
{
    // An opt-in real-backend check with injected test settings, no OS input
    // synthesis and no dependency on a user's running app/profile.
    private static int NativeMenuSmoke()
    {
        if (!OperatingSystem.IsMacOS())
        {
            Console.WriteLine("SKIP: native macOS menu smoke check requires macOS.");
            return 0;
        }
        Directory.CreateDirectory(Root);
        AppBuilder.Configure<App>().UsePlatformDetect().SetupWithoutStarting();
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var vm = CreateViewModel(Root);
        var window = new MainWindow(vm);
        int result = 1;
        window.Closed += (_, _) => stop.Cancel();
        window.Opened += async (_, _) =>
        {
            try
            {
                await vm.InitializeAsync();
                if (!NativeMenu.GetIsNativeMenuExported(window) || window.FindControl<Menu>("MainMenu")!.IsVisible)
                    throw new Exception("macOS did not export the native menu or the fallback stayed visible.");
                Console.WriteLine("PASS: real macOS backend exports the native menu; in-window fallback hidden.");
                result = 0;
            }
            catch (Exception ex) { Console.Error.WriteLine(ex); }
            finally { window.Close(); }
        };
        window.Show();
        Dispatcher.UIThread.MainLoop(stop.Token);
        return result;
    }
}
