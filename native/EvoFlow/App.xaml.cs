using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Data;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Navigation;
using System.Text.RegularExpressions;

// To learn more about WinUI, the WinUI project structure,
// and more about our project templates, see: http://aka.ms/winui-project-info.

namespace EvoFlow;

/// <summary>
/// Provides application-specific behavior to supplement the default Application class.
/// </summary>
public partial class App : Application
{
    /// <summary>
    /// The main application window. Use <c>App.Window</c> from any class that needs
    /// the window reference (for dialogs, pickers, interop, etc.).
    /// </summary>
    public static Window Window { get; private set; } = null!;

    /// <summary>
    /// The UI thread dispatcher. Use <c>App.DispatcherQueue</c> to marshal calls
    /// to the UI thread. Fully qualified to avoid CS0104 ambiguity with
    /// <see cref="Windows.System.DispatcherQueue"/>.
    /// </summary>
    public static Microsoft.UI.Dispatching.DispatcherQueue DispatcherQueue { get; private set; } = null!;

    /// <summary>
    /// The native window handle (HWND). Use for file pickers,
    /// <c>DataTransferManager</c>, and any WinRT interop that requires
    /// <c>InitializeWithWindow</c>.
    /// </summary>
    public static nint WindowHandle =>
        WinRT.Interop.WindowNative.GetWindowHandle(Window);

    /// <summary>
    /// Initializes the singleton application object.
    /// </summary>
    public App()
    {
        UnhandledException += (_, args) => RecordStartupError("XAML unhandled exception", args.Exception, args.Message);
        try { InitializeComponent(); }
        catch (Exception exception) { RecordStartupError("Application resources", exception); throw; }
    }

    /// <summary>
    /// Invoked when the application is launched.
    /// </summary>
    /// <param name="args">Details about the launch request and process.</param>
    protected override void OnLaunched(Microsoft.UI.Xaml.LaunchActivatedEventArgs args)
    {
        try
        {
            if (Window is MainWindow existing)
            {
                existing.SetExpanded(true);
                return;
            }
            Window = new MainWindow();
            DispatcherQueue = Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread();
            // The user's launch activates once. Idle presence and hover previews continue
            // to use Show(false), so later background visibility changes do not take focus.
            Window.Activate();
        }
        catch (Exception exception) { RecordStartupError("Main window launch", exception); throw; }
    }

    private static void RecordStartupError(string stage, Exception? exception, string? message = null)
    {
        // Preserve the real failure; the hook neither handles the exception nor records payloads,
        // credentials, exception.Data, or worker traffic.
        try
        {
            var folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "EvoFlow");
            Directory.CreateDirectory(folder);
            string details = $"{DateTimeOffset.Now:O} · {stage}\n";
            if (message is not null) details += message + "\n";
            for (int depth = 0; exception is not null && depth < 4; depth++, exception = exception.InnerException)
                details += $"{exception.GetType().FullName} · HRESULT 0x{exception.HResult:X8}\n{exception.Message}\n{exception.StackTrace}\n";
            details = Regex.Replace(details, @"(?i)\bsk-or-v1-[a-z0-9_-]+|\bBearer\s+\S+", "[redacted credential]");
            File.AppendAllText(Path.Combine(folder, "native-startup-error.txt"), details + "\n");
        }
        catch { /* Diagnostics must never replace or suppress the original startup error. */ }
    }
}
