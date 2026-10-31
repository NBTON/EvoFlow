using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Markup;
using System.Text.Json.Nodes;
using Windows.Graphics;

namespace EvoFlow;

internal sealed class SettingsWindow : Window
{
    private readonly WorkerClient worker;
    private readonly CompanionSettings settings;
    private readonly Action applySettings;
    private readonly PasswordBox keyBox;
    private readonly TextBlock connectionText;
    private readonly ComboBox monitorChoice, shortcutChoice;
    private readonly InfoBar notice;
    private IReadOnlyList<DisplayArea> monitors = [];
    private bool updatingDisplays;

    public SettingsWindow(WorkerClient worker, CompanionSettings settings, Action applySettings, FrameworkElement? tools = null)
    {
        this.worker = worker; this.settings = settings; this.applySettings = applySettings;
        Title = "EvoFlow Settings";
        var stack = new StackPanel { Spacing = 16, Padding = new Thickness(24) };
        stack.Children.Add(new TextBlock { Text = "Settings", FontSize = 28, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold });
        stack.Children.Add(new TextBlock { Text = "Make EvoFlow feel at home on your desktop.", TextWrapping = TextWrapping.Wrap });

        if (tools is not null) stack.Children.Add(tools);
        stack.Children.Add(Heading("Presence"));
        var topmost = Identify(new ToggleSwitch { Header = "Keep above other windows", IsOn = settings.AlwaysOnTop }, "TopmostToggle");
        topmost.Toggled += (_, _) => ChangePreference(() => settings.AlwaysOnTop = topmost.IsOn);
        stack.Children.Add(topmost);
        var hover = Identify(new ToggleSwitch { Header = "Expand when I hover", IsOn = settings.ExpandOnHover }, "HoverToggle");
        hover.Toggled += (_, _) => ChangePreference(() => settings.ExpandOnHover = hover.IsOn);
        stack.Children.Add(hover);
        stack.Children.Add(new TextBlock { Text = "Hover previews without taking focus. Click or use your shortcut to keep the task open. Press Escape to collapse.", TextWrapping = TextWrapping.Wrap, FontSize = 12 });
        monitorChoice = Identify(new ComboBox { Header = "Display", HorizontalAlignment = HorizontalAlignment.Stretch }, "MonitorChoice");
        monitorChoice.SelectionChanged += (_, _) =>
        {
            int index = monitorChoice.SelectedIndex;
            if (!updatingDisplays && index >= 0 && index < monitors.Count) ChangePreference(() => settings.DisplayId = monitors[index].DisplayId.Value);
        };
        stack.Children.Add(monitorChoice);
        shortcutChoice = Identify(new ComboBox { Header = "Global shortcut", HorizontalAlignment = HorizontalAlignment.Stretch }, "HotkeyChoice");
        shortcutChoice.Items.Add("Ctrl + Alt + E"); shortcutChoice.Items.Add("Ctrl + Alt + F"); shortcutChoice.SelectedIndex = settings.ShortcutIndex;
        shortcutChoice.SelectionChanged += (_, _) => ChangePreference(() => settings.ShortcutIndex = shortcutChoice.SelectedIndex);
        stack.Children.Add(shortcutChoice);
        stack.Children.Add(new TextBlock { Text = "EvoFlow is also available in the Windows notification area. Windows controls whether its icon is shown beside the clock or in the overflow menu.", TextWrapping = TextWrapping.Wrap, FontSize = 12 });

        stack.Children.Add(Heading("Connections"));
        if (worker.Connections is not null) stack.Children.Add(new ConnectionsPanel(worker));
        connectionText = Identify(new TextBlock { Text = "Connecting automatically…", TextWrapping = TextWrapping.Wrap }, "ConnectionStatus");
        stack.Children.Add(connectionText);
        stack.Children.Add(new TextBlock { Text = "Your private cloud workspace connects automatically using this device's protected session.", TextWrapping = TextWrapping.Wrap, FontSize = 12 });
        keyBox = Identify(new PasswordBox { Header = "OpenRouter API key", PlaceholderText = "Stored with Windows DPAPI" }, "OpenRouterKey");
        AutomationProperties.SetName(keyBox, "OpenRouter API key"); stack.Children.Add(keyBox);
        var saveKey = Identify(new Button { Content = "Save key securely" }, "SaveKeyButton");
        saveKey.Click += async (_, _) =>
        {
            saveKey.IsEnabled = false;
            try
            {
                if (string.IsNullOrWhiteSpace(keyBox.Password)) throw new InvalidOperationException("Enter your OpenRouter API key first.");
                await worker.PostAsync("settings/key", new JsonObject { ["key"] = keyBox.Password.Trim() });
                keyBox.Password = ""; ShowNotice("OpenRouter key saved with Windows DPAPI.", InfoBarSeverity.Success);
            }
            catch (Exception ex) { ShowNotice(ex.Message, InfoBarSeverity.Error); }
            finally { saveKey.IsEnabled = true; }
        };
        stack.Children.Add(saveKey);
        stack.Children.Add(new TextBlock { Text = "Models use explicitly configured providers and request budgets; paid usage may apply. Provider keys load from LocalAppData\\EvoFlow\\provider-keys.env at worker startup. No automatic generator substitution.", TextWrapping = TextWrapping.Wrap, FontSize = 12 });
        var sync = Identify(new Button { Content = "Retry synchronization" }, "SyncButton");
        sync.Click += async (_, _) =>
        {
            sync.IsEnabled = false;
            try
            {
                var status = await worker.PostAsync("sync");
                ShowNotice($"Synchronized {status["synced"]} records.", InfoBarSeverity.Success);
                var state = await worker.GetAsync("state");
                UpdateConnectionStatus($"Cloud: {state["health"]?["supabase"]} · Windows-protected device session");
            }
            catch (Exception ex) { ShowNotice(ex.Message, InfoBarSeverity.Error); }
            finally { sync.IsEnabled = true; }
        };
        stack.Children.Add(sync);
        notice = Identify(new InfoBar { IsOpen = false, IsClosable = true }, "SettingsNotice"); stack.Children.Add(notice);
        var done = Identify(new Button { Content = "Done", HorizontalAlignment = HorizontalAlignment.Right }, "CloseSettingsButton");
        done.Click += (_, _) => Close(); stack.Children.Add(done);
        // Retain ThemeResource resolution so Contrast-theme changes update the surface live.
        var scroll = (ScrollViewer)XamlReader.Load("<ScrollViewer xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation' RequestedTheme='Dark' Background='{ThemeResource CompanionSurfaceBrush}' HorizontalScrollBarVisibility='Disabled' />");
        scroll.Content = stack; Content = scroll;
        AppWindow.SetIcon(Path.Combine(AppContext.BaseDirectory, "Assets", "AppIcon.ico"));
        var area = DisplayArea.GetFromWindowId(AppWindow.Id, DisplayAreaFallback.Primary);
        double scale = NativePresence.DpiForArea(area);
        int width = Math.Min((int)(520 * scale), area.WorkArea.Width - 32), height = Math.Min((int)(760 * scale), area.WorkArea.Height - 32);
        AppWindow.MoveAndResize(new RectInt32(area.WorkArea.X + (area.WorkArea.Width - width) / 2, area.WorkArea.Y + (area.WorkArea.Height - height) / 2, width, height));
        if (AppWindow.Presenter is OverlappedPresenter presenter) presenter.IsMaximizable = false;
        NativePresence.UseDarkTitleBar(WinRT.Interop.WindowNative.GetWindowHandle(this));
        Closed += (_, _) => keyBox.Password = "";
    }

    public void UpdateConnectionStatus(string message) => connectionText.Text = message;
    public void ShowShortcutStatus(bool registered)
    {
        if (!registered) ShowNotice("That global shortcut is already in use. Choose the other shortcut; the tray and pill remain available.", InfoBarSeverity.Warning);
    }
    public void UpdateDisplays(IReadOnlyList<DisplayArea> displays)
    {
        updatingDisplays = true;
        try
        {
            monitors = displays; monitorChoice.Items.Clear();
            for (int i = 0; i < monitors.Count; i++) monitorChoice.Items.Add($"Display {i + 1} · {monitors[i].WorkArea.Width} × {monitors[i].WorkArea.Height}");
            int index = monitors.ToList().FindIndex(m => m.DisplayId.Value == settings.DisplayId);
            if (index < 0)
            {
                var current = DisplayArea.GetFromWindowId(AppWindow.Id, DisplayAreaFallback.Primary);
                index = monitors.ToList().FindIndex(m => m.DisplayId.Value == current.DisplayId.Value);
            }
            monitorChoice.SelectedIndex = index >= 0 ? index : monitors.Count > 0 ? 0 : -1;
        }
        finally { updatingDisplays = false; }
    }
    private void ChangePreference(Action change)
    {
        try { change(); settings.Save(); applySettings(); }
        catch (Exception ex) { ShowNotice($"Could not save this preference: {ex.Message}", InfoBarSeverity.Error); }
    }
    private void ShowNotice(string message, InfoBarSeverity severity) { notice.Message = message; notice.Severity = severity; notice.IsOpen = true; }
    private static TextBlock Heading(string text) => new() { Text = text, FontSize = 18, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, Margin = new Thickness(0, 8, 0, 0) };
    private static T Identify<T>(T element, string id) where T : DependencyObject { AutomationProperties.SetAutomationId(element, id); return element; }
}
