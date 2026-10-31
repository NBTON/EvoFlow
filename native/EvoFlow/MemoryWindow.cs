using Microsoft.UI;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using System.Runtime.InteropServices;
using System.Text.Json.Nodes;
using Windows.Foundation;
using Windows.Graphics;
using Windows.UI;
using Windows.UI.ViewManagement;

namespace EvoFlow;

/// <summary>Native memory graph with separate, explicit memory management dialogs.</summary>
public sealed class MemoryWindow : Window
{
    private readonly WorkerClient worker;
    private readonly Grid root = new() { Padding = new Thickness(24), RowSpacing = 12, RequestedTheme = ElementTheme.Dark };
    private readonly Canvas canvas = new() { Width = 1800, Height = 1300 };
    private readonly ScrollViewer viewport = new()
    {
        ZoomMode = ZoomMode.Enabled, MinZoomFactor = .25f, MaxZoomFactor = 2.5f,
        HorizontalScrollMode = ScrollMode.Enabled, VerticalScrollMode = ScrollMode.Enabled,
        HorizontalScrollBarVisibility = ScrollBarVisibility.Auto, VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
        IsTabStop = true
    };
    private readonly AutoSuggestBox search = new() { Header = "Search saved memory", PlaceholderText = "Search title, content or state", QueryIcon = new SymbolIcon(Symbol.Find), MaxWidth = 340, HorizontalAlignment = HorizontalAlignment.Stretch };
    private readonly ComboBox typeFilter = new() { Header = "Record type", MinWidth = 150, SelectedIndex = -1 };
    private readonly CheckBox history = new() { Content = "Include history", VerticalAlignment = VerticalAlignment.Bottom, Margin = new Thickness(0, 0, 0, 7) };
    private readonly TextBlock status = new() { Text = "Loading recorded memory…", TextWrapping = TextWrapping.Wrap, FontSize = 12 };
    private readonly TextBlock empty = new() { TextWrapping = TextWrapping.Wrap, MaxWidth = 430, FontSize = 19, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, Visibility = Visibility.Collapsed };
    private readonly InfoBar notice = new() { IsOpen = false, IsClosable = true, Severity = InfoBarSeverity.Warning };
    private readonly ListView nodeList = new() { SelectionMode = ListViewSelectionMode.Single, MaxHeight = 220 };
    private readonly StackPanel details = new() { Spacing = 10 };
    private readonly TextBlock detailsTitle = new() { Text = "Explore a memory", FontSize = 20, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock detailsIntro = new() { Text = "Select a node to inspect its recorded origin and relationships. The list below offers the same nodes with keyboard navigation.", TextWrapping = TextWrapping.Wrap };
    private readonly Dictionary<string, JsonNode> nodes = new();
    private readonly List<JsonNode> edges = new();
    private readonly Dictionary<string, Point> positions = new();
    private readonly Dictionary<string, Button> nodeButtons = new();
    private readonly Dictionary<string, Ellipse> nodeCircles = new();
    private readonly List<(JsonNode Edge, Line Line, Polygon Arrow)> edgeVisuals = new();
    private readonly List<(Ellipse Circle, string Type)> legendCircles = new();
    private Border? graphFrame;
    private readonly AccessibilitySettings accessibility = new();
    private readonly DispatcherQueueTimer debounce;
    private DispatcherQueueTimer? contrastPoll;
    private bool contrastSubscribed;
    private bool highContrast;
    private string? selectedId;
    private string? hoverId;
    private bool loaded;
    private bool closed;
    private bool managementOpen;
    private int requestVersion;
    private bool dragging;
    private Point dragStart;
    private double dragHorizontal, dragVertical;
    private static readonly string[] Types = { "knowledge", "preference", "skill", "run", "source", "contract" };
    private static readonly string[] TypeNames = { "Knowledge", "Preferences", "Learned skills", "Task runs", "Sources", "Contracts" };

    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(IntPtr window);

    [StructLayout(LayoutKind.Sequential)]
    private struct HighContrastInfo
    {
        public uint Size;
        public uint Flags;
        public IntPtr DefaultScheme;
    }

    [DllImport("user32.dll", EntryPoint = "SystemParametersInfoW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ReadSystemParameters(uint action, uint size, ref HighContrastInfo value, uint flags);

    public MemoryWindow(WorkerClient worker)
    {
        this.worker = worker;
        highContrast = ReadHighContrast();
        Title = "EvoFlow · Memory";
        AppWindow.SetIcon(System.IO.Path.Combine(AppContext.BaseDirectory, "Assets", "AppIcon.ico"));
        var scale = GetDpiForWindow(Win32Interop.GetWindowFromWindowId(AppWindow.Id)) / 96.0;
        AppWindow.Resize(new SizeInt32((int)(1300 * scale), (int)(840 * scale)));
        debounce = DispatcherQueue.CreateTimer(); debounce.Interval = TimeSpan.FromMilliseconds(300);
        debounce.Tick += async (_, _) => { debounce.Stop(); await RefreshAsync(); };
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var header = new Grid { ColumnSpacing = 18 };
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var heading = new StackPanel { Spacing = 4 };
        heading.Children.Add(new TextBlock { Text = "Memory", FontSize = 30, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold });
        heading.Children.Add(new TextBlock { Text = "What EvoFlow remembers, where it came from, and what depends on it.", TextWrapping = TextWrapping.Wrap });
        header.Children.Add(heading);
        var refresh = Command("Refresh", "MemoryRefresh", "Reload saved memory", async () => await RefreshAsync());
        var management = new Button { Content = "Manage memory", UseSystemFocusVisuals = true };
        Label(management, "MemoryManage", "Manage saved preferences, policies, source documents and skill versions");
        var menu = new MenuFlyout();
        foreach (var entry in new[] { ("Save preference", "preference"), ("Add policy", "policy"), ("Add source document", "source"), ("Restore skill version", "rollback") })
        {
            var action = entry.Item2; var item = new MenuFlyoutItem { Text = entry.Item1 };
            Label(item, "MemoryManage_" + action, entry.Item1);
            item.Click += async (_, _) => { if (action == "rollback") await ShowRollbackAsync(); else await ShowMemoryEditorAsync(action); };
            menu.Items.Add(item);
        }
        management.Flyout = menu;
        var headerActions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, VerticalAlignment = VerticalAlignment.Center };
        headerActions.Children.Add(management); headerActions.Children.Add(refresh);
        Grid.SetColumn(headerActions, 1); header.Children.Add(headerActions);
        root.Children.Add(header);

        var filters = new Grid { ColumnSpacing = 14 };
        filters.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        filters.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        filters.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        Label(search, "MemorySearch", "Search saved memory"); search.AccessKey = "F";
        search.QuerySubmitted += async (_, _) => { debounce.Stop(); await RefreshAsync(); };
        search.TextChanged += (_, args) => { if (loaded && args.Reason == AutoSuggestionBoxTextChangeReason.UserInput) { debounce.Stop(); debounce.Start(); } };
        filters.Children.Add(search);
        typeFilter.Items.Add(new ComboBoxItem { Content = "All types", Tag = "" });
        for (var i = 0; i < Types.Length; i++) typeFilter.Items.Add(new ComboBoxItem { Content = TypeNames[i], Tag = Types[i] });
        typeFilter.SelectedIndex = 0; Label(typeFilter, "MemoryTypeFilter", "Filter memory by record type");
        typeFilter.SelectionChanged += async (_, _) => { if (loaded) await RefreshAsync(); };
        Grid.SetColumn(typeFilter, 1); filters.Children.Add(typeFilter);
        Label(history, "MemoryHistory", "Include superseded, retired and rejected records");
        history.Checked += async (_, _) => { if (loaded) await RefreshAsync(); };
        history.Unchecked += async (_, _) => { if (loaded) await RefreshAsync(); };
        Grid.SetColumn(history, 2); filters.Children.Add(history); Grid.SetRow(filters, 1); root.Children.Add(filters);
        Grid.SetRow(notice, 2); root.Children.Add(notice);

        var workspace = new Grid { ColumnSpacing = 18 };
        workspace.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        workspace.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(320) });
        var graphPanel = new Grid { RowSpacing = 8 };
        graphPanel.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        graphPanel.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        var commands = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        commands.Children.Add(Command("−", "MemoryZoomOut", "Zoom graph out", () => Zoom(.8f)));
        commands.Children.Add(Command("+", "MemoryZoomIn", "Zoom graph in", () => Zoom(1.25f)));
        commands.Children.Add(Command("Fit graph", "MemoryFit", "Fit all visible nodes", Fit));
        commands.Children.Add(new TextBlock { Text = "Drag to pan · pinch / Ctrl+wheel to zoom", FontSize = 12, VerticalAlignment = VerticalAlignment.Center, TextWrapping = TextWrapping.Wrap });
        graphPanel.Children.Add(commands);
        viewport.Content = canvas; Label(viewport, "MemoryGraphViewport", "Memory graph. Use scroll bars or arrow keys to pan, and zoom buttons to change scale.");
        canvas.Background = Brush("surface");
        canvas.PointerPressed += BeginPan; canvas.PointerMoved += Pan; canvas.PointerReleased += EndPan;
        canvas.PointerCaptureLost += (_, _) => dragging = false;
        var graphSurface = new Grid(); graphSurface.Children.Add(viewport); graphSurface.Children.Add(empty);
        var frame = new Border { CornerRadius = new CornerRadius(12), BorderThickness = new Thickness(1), BorderBrush = Brush("stroke"), Child = graphSurface };
        graphFrame = frame;
        Grid.SetRow(frame, 1); graphPanel.Children.Add(frame); workspace.Children.Add(graphPanel);

        var inspector = new Grid { RowSpacing = 12 };
        inspector.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        inspector.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        inspector.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        var listHeading = new TextBlock { Text = "Saved nodes · keyboard navigation", FontWeight = Microsoft.UI.Text.FontWeights.SemiBold };
        inspector.Children.Add(listHeading); Label(nodeList, "MemoryNodeList", "Saved memory nodes. Select a node to inspect its details and relationships.");
        nodeList.SelectionChanged += (_, _) => { if (nodeList.SelectedItem is ListViewItem item && item.Tag is string id) Select(id); };
        Grid.SetRow(nodeList, 1); inspector.Children.Add(nodeList);
        details.Children.Add(detailsTitle); details.Children.Add(detailsIntro);
        var detailScroll = new ScrollViewer { Content = details, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
        Label(detailScroll, "MemoryNodeDetails", "Selected memory details and relationships");
        Grid.SetRow(detailScroll, 2); inspector.Children.Add(detailScroll); Grid.SetColumn(inspector, 1); workspace.Children.Add(inspector);
        Grid.SetRow(workspace, 3); root.Children.Add(workspace);

        var footer = new StackPanel { Spacing = 8 };
        var legend = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 14 };
        for (var i = 0; i < Types.Length; i++)
        {
            var item = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 5 };
            var circle = new Ellipse { Width = 9, Height = 9, Fill = Brush(Types[i]), VerticalAlignment = VerticalAlignment.Center };
            item.Children.Add(circle); legendCircles.Add((circle, Types[i]));
            item.Children.Add(new TextBlock { Text = TypeNames[i], FontSize = 12 }); legend.Children.Add(item);
        }
        footer.Children.Add(legend);
        footer.Children.Add(new TextBlock { Text = "DEMO / FICTIONAL appears only when recorded source metadata says so. Lines show explicit references; proximity has no meaning.", FontSize = 11, TextWrapping = TextWrapping.Wrap });
        Label(status, "MemoryGraphStatus", "Memory graph status"); AutomationProperties.SetLiveSetting(status, Microsoft.UI.Xaml.Automation.Peers.AutomationLiveSetting.Polite);
        footer.Children.Add(status); Grid.SetRow(footer, 4); root.Children.Add(footer);
        Content = root;
        root.Loaded += async (_, _) => { if (!loaded) { loaded = true; ApplyPalette(); await RefreshAsync(); } };
        root.ActualThemeChanged += (_, _) => ApplyPalette();
        StartContrastMonitoring();
        Closed += (_, _) =>
        {
            closed = true; requestVersion++; debounce.Stop(); contrastPoll?.Stop(); contrastPoll = null;
            if (contrastSubscribed)
            {
                try { accessibility.HighContrastChanged -= ContrastChanged; }
                catch (COMException exception) when (UnsupportedContrastEvent(exception)) { }
                contrastSubscribed = false;
            }
        };
    }

    private async Task RefreshAsync()
    {
        if (closed) return;
        var version = ++requestVersion; status.Text = "Loading recorded memory…";
        try
        {
            var type = (typeFilter.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "";
            var query = search.Text.Length > 200 ? search.Text[..200] : search.Text;
            var graph = await worker.GetAsync($"memory/graph?query={Uri.EscapeDataString(query)}&types={Uri.EscapeDataString(type)}&include_history={(history.IsChecked == true ? "true" : "false")}&limit=160");
            if (closed || version != requestVersion) return;
            nodes.Clear(); edges.Clear(); positions.Clear(); hoverId = null;
            if (graph["nodes"] is JsonArray values) foreach (var node in values) if (node is not null) nodes[Text(node, "id")] = node;
            if (graph["edges"] is JsonArray relationships) foreach (var edge in relationships) if (edge is not null) edges.Add(edge);
            nodeList.Items.Clear();
            foreach (var pair in nodes)
            {
                var node = pair.Value;
                var item = new ListViewItem { Content = $"{Text(node, "label")}\n{TypeName(Text(node, "type"))} · {Text(node, "state")}{(IsFictional(node) ? " · DEMO / FICTIONAL" : "")}", Tag = pair.Key };
                Label(item, "MemoryList_" + pair.Key, item.Content.ToString()!); nodeList.Items.Add(item);
            }
            if (selectedId is not null && !nodes.ContainsKey(selectedId)) selectedId = null;
            empty.Text = Text(graph, "empty_message"); empty.Visibility = nodes.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            Layout(); Draw();
            if (selectedId is not null) Select(selectedId); else ResetDetails();
            var scope = graph["scope"];
            var partial = graph["truncated"]?["nodes"]?.GetValue<bool>() == true || graph["truncated"]?["edges"]?.GetValue<bool>() == true;
            var unresolved = graph["counts"]?["unresolved_references"]?.ToString() ?? "0";
            status.Text = $"{nodes.Count} nodes · {edges.Count} recorded links · workspace {scope?["workspace"]} · {scope?["mode"]} · {unresolved} unresolved references" + (partial ? " · Partial view: narrow search or type filter to see more." : "");
            notice.IsOpen = false; notice.Severity = InfoBarSeverity.Warning; DispatcherQueue.TryEnqueue(Fit);
        }
        catch
        {
            if (closed || version != requestVersion) return;
            notice.Message = "Memory could not be loaded. Use Refresh to retry." + (nodes.Count > 0 ? " Showing the previous snapshot." : "");
            notice.IsOpen = true; status.Text = nodes.Count > 0 ? "Previous snapshot · refresh failed" : "Memory unavailable · refresh to retry";
        }
    }

    private async Task ShowMemoryEditorAsync(string mode)
    {
        if (managementOpen || closed) return;
        managementOpen = true;
        var preference = mode == "preference"; var upload = mode == "source";
        var form = new StackPanel { Spacing = 12, MinWidth = 370, MaxWidth = 500 };
        form.Children.Add(new TextBlock
        {
            Text = preference ? "Save an explicit preference for future tasks in this workspace. Reusing its key creates a versioned update."
                : upload ? "Paste the selected source document below. Choosing Upload source stores this content in your private workspace storage."
                : "Save a source-backed policy in this workspace. Unconfirmed policies remain candidates until authority is confirmed.",
            TextWrapping = TextWrapping.Wrap
        });
        var key = new TextBox { Header = upload ? "Source document name" : "Memory key", PlaceholderText = preference ? "For example: report-format" : "For example: shipping-policy", MaxLength = 100 };
        Label(key, "MemoryEditorKey", upload ? "Source document name" : "Memory key"); form.Children.Add(key);
        var content = new TextBox { Header = preference ? "Preference to remember" : "Policy / source content", AcceptsReturn = true, MinHeight = 120, MaxHeight = 220, TextWrapping = TextWrapping.Wrap, MaxLength = 4000 };
        Label(content, "MemoryEditorContent", preference ? "Explicit preference content" : "Selected policy or source content"); form.Children.Add(content);
        var source = new TextBox { Header = "Source reference", PlaceholderText = "Document title, version or official URL", MaxLength = 500 };
        if (!preference && !upload) { Label(source, "MemoryEditorSource", "Policy source reference"); form.Children.Add(source); }
        var authoritative = new CheckBox { Content = "I confirm this source is authoritative for this policy", IsChecked = false };
        if (!preference) { Label(authoritative, "MemoryEditorAuthority", "Confirm source authority for this policy"); form.Children.Add(authoritative); }
        var feedback = new InfoBar { IsOpen = false, IsClosable = false, Severity = InfoBarSeverity.Error };
        Label(feedback, "MemoryEditorFeedback", "Memory save status"); form.Children.Add(feedback);
        var dialog = new ContentDialog
        {
            XamlRoot = root.XamlRoot, Title = preference ? "Save preference" : upload ? "Add source document" : "Add policy",
            Content = new ScrollViewer { Content = form, MaxHeight = 550, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled },
            PrimaryButtonText = preference ? "Save preference" : upload ? "Upload source" : "Save policy", CloseButtonText = "Cancel", DefaultButton = ContentDialogButton.None,
            RequestedTheme = root.RequestedTheme
        };
        Label(dialog, "MemoryEditorDialog", dialog.Title.ToString()!);
        var saving = false;
        dialog.Closing += (_, args) => { if (saving) args.Cancel = true; };
        dialog.PrimaryButtonClick += async (_, args) =>
        {
            args.Cancel = true;
            var memoryKey = key.Text.Trim(); var text = content.Text.Trim(); var reference = source.Text.Trim();
            if (memoryKey.Length == 0 || text.Length == 0 || (!preference && !upload && reference.Length == 0) || (upload && (memoryKey.Contains('/') || memoryKey.Contains('\\'))))
            {
                feedback.Severity = InfoBarSeverity.Error; feedback.Message = upload ? "Enter a source name without slashes and paste its selected content." : preference ? "Enter a memory key and the explicit preference to save." : "Enter a memory key, policy content and source reference."; feedback.IsOpen = true; return;
            }
            var deferral = args.GetDeferral(); saving = true; dialog.IsPrimaryButtonEnabled = false;
            feedback.Severity = InfoBarSeverity.Informational; feedback.Message = upload ? "Uploading selected source…" : "Saving memory…"; feedback.IsOpen = true;
            try
            {
                JsonNode result;
                if (upload) result = await worker.PostAsync("knowledge/source", new JsonObject { ["name"] = memoryKey, ["content"] = text, ["authoritative"] = authoritative.IsChecked == true });
                else result = await worker.PostAsync("memory", new JsonObject
                {
                    ["kind"] = preference ? "preference" : "knowledge", ["key"] = memoryKey, ["content"] = text,
                    ["source"] = preference ? "explicit-user-preference" : reference,
                    ["explicit"] = preference, ["authoritative"] = !preference && authoritative.IsChecked == true
                });
                var state = await worker.GetAsync("state");
                var records = state[preference ? "preferences" : "knowledge"] as JsonArray;
                var saved = records?.FirstOrDefault(record => record?["id"]?.ToString() == result["id"]?.ToString() && record?["content_hash"]?.ToString() == result["content_hash"]?.ToString());
                if (saved is null) throw new InvalidOperationException("Saved memory could not be confirmed");
                await RefreshAsync(); notice.Severity = InfoBarSeverity.Success;
                notice.Message = $"{(preference ? "Preference" : "Policy / source")} saved and verified · {saved["state"]}."; notice.IsOpen = true;
                args.Cancel = false;
            }
            catch
            {
                feedback.Severity = InfoBarSeverity.Error; feedback.Message = "The save could not be confirmed. Refresh memory before trying again; a record may already have been saved."; feedback.IsOpen = true;
            }
            finally { saving = false; dialog.IsPrimaryButtonEnabled = true; deferral.Complete(); }
        };
        try { await dialog.ShowAsync(); }
        finally { managementOpen = false; }
    }

    private async Task ShowRollbackAsync()
    {
        if (managementOpen || closed) return;
        managementOpen = true;
        var form = new StackPanel { Spacing = 12, MinWidth = 370, MaxWidth = 500 };
        form.Children.Add(new TextBlock { Text = "Restore a previously verified, retired skill. The selected version becomes active and the current active version returns to history.", TextWrapping = TextWrapping.Wrap });
        var versions = new ComboBox { Header = "Retired skill version", HorizontalAlignment = HorizontalAlignment.Stretch, IsEnabled = false };
        Label(versions, "MemoryRollbackVersion", "Previously verified retired skill version"); form.Children.Add(versions);
        var feedback = new InfoBar { IsOpen = true, IsClosable = false, Severity = InfoBarSeverity.Informational, Message = "Loading actual saved versions…" };
        Label(feedback, "MemoryRollbackFeedback", "Skill restore status"); form.Children.Add(feedback);
        var dialog = new ContentDialog { XamlRoot = root.XamlRoot, Title = "Restore skill version", Content = form, PrimaryButtonText = "Restore selected version", CloseButtonText = "Cancel", DefaultButton = ContentDialogButton.None, IsPrimaryButtonEnabled = false, RequestedTheme = root.RequestedTheme };
        Label(dialog, "MemoryRollbackDialog", "Restore a previously verified skill version");
        var saving = false; var dismissed = false;
        dialog.Closing += (_, args) => { if (saving) args.Cancel = true; else dismissed = true; };
        dialog.Opened += async (_, _) =>
        {
            try
            {
                var state = await worker.GetAsync("state"); if (dismissed || closed) return;
                if (state["skills"] is JsonArray skills) foreach (var skill in skills)
                {
                    if (skill is null || Text(skill, "state") != "retired") continue;
                    versions.Items.Add(new ComboBoxItem { Content = $"v{skill["version"]} · {skill["title"]}", Tag = Text(skill, "id") });
                }
                versions.IsEnabled = versions.Items.Count > 0; dialog.IsPrimaryButtonEnabled = versions.Items.Count > 0;
                if (versions.Items.Count > 0) versions.SelectedIndex = 0;
                feedback.Message = versions.Items.Count == 0 ? "No retired, previously verified skill versions are saved in this workspace." : "Select the exact version to restore. Candidate and rejected versions cannot be restored.";
            }
            catch { if (!dismissed) { feedback.Severity = InfoBarSeverity.Error; feedback.Message = "Saved versions could not be loaded. Close this dialog and retry."; } }
        };
        dialog.PrimaryButtonClick += async (_, args) =>
        {
            args.Cancel = true;
            if (versions.SelectedItem is not ComboBoxItem item || item.Tag is not string identifier) return;
            var deferral = args.GetDeferral(); saving = true; dialog.IsPrimaryButtonEnabled = false; versions.IsEnabled = false;
            feedback.Severity = InfoBarSeverity.Informational; feedback.Message = "Restoring selected version…";
            try
            {
                await worker.PostAsync($"skills/{Uri.EscapeDataString(identifier)}/rollback");
                var state = await worker.GetAsync("state");
                var active = (state["skills"] as JsonArray)?.Where(skill => skill?["state"]?.ToString() == "active").ToList();
                if (active is null || active.Count != 1 || active[0]?["id"]?.ToString() != identifier) throw new InvalidOperationException("Active version could not be confirmed");
                await RefreshAsync(); notice.Severity = InfoBarSeverity.Success; notice.Message = $"Restored and verified {item.Content}."; notice.IsOpen = true; args.Cancel = false;
            }
            catch { feedback.Severity = InfoBarSeverity.Error; feedback.Message = "Restore could not be confirmed. Refresh memory before retrying; the active version may have changed."; }
            finally { saving = false; dialog.IsPrimaryButtonEnabled = true; versions.IsEnabled = true; deferral.Complete(); }
        };
        try { await dialog.ShowAsync(); }
        finally { managementOpen = false; }
    }

    private void Layout()
    {
        if (nodes.Count == 0) return;
        var ordered = nodes.Keys.ToList();
        var width = Math.Max(1200, Math.Sqrt(nodes.Count) * 165); var height = Math.Max(850, Math.Sqrt(nodes.Count) * 130);
        canvas.Width = width; canvas.Height = height;
        for (var i = 0; i < ordered.Count; i++)
        {
            var angle = i * Math.PI * 2 / ordered.Count;
            positions[ordered[i]] = new Point(width / 2 + Math.Cos(angle) * width * .3, height / 2 + Math.Sin(angle) * height * .3);
        }
        // Deterministic force layout changes only coordinates, never graph relationships.
        for (var step = 0; step < 130; step++)
        {
            var force = ordered.ToDictionary(id => id, _ => new Point(0, 0));
            for (var i = 0; i < ordered.Count; i++) for (var j = i + 1; j < ordered.Count; j++)
            {
                var a = ordered[i]; var b = ordered[j]; var dx = positions[a].X - positions[b].X; var dy = positions[a].Y - positions[b].Y;
                var distance = Math.Max(24, Math.Sqrt(dx * dx + dy * dy)); var push = 1700 / (distance * distance);
                force[a] = new Point(force[a].X + dx * push, force[a].Y + dy * push);
                force[b] = new Point(force[b].X - dx * push, force[b].Y - dy * push);
            }
            foreach (var edge in edges)
            {
                var a = Text(edge, "source"); var b = Text(edge, "target"); if (!positions.ContainsKey(a) || !positions.ContainsKey(b)) continue;
                var dx = positions[b].X - positions[a].X; var dy = positions[b].Y - positions[a].Y;
                var distance = Math.Max(1, Math.Sqrt(dx * dx + dy * dy)); var pull = (distance - 180) * .018;
                force[a] = new Point(force[a].X + dx / distance * pull, force[a].Y + dy / distance * pull);
                force[b] = new Point(force[b].X - dx / distance * pull, force[b].Y - dy / distance * pull);
            }
            foreach (var id in ordered)
            {
                var p = positions[id]; var f = force[id];
                positions[id] = new Point(Math.Clamp(p.X + Math.Clamp(f.X, -14, 14), 90, width - 90), Math.Clamp(p.Y + Math.Clamp(f.Y, -14, 14), 55, height - 75));
            }
        }
    }

    private void Draw()
    {
        canvas.Children.Clear(); nodeButtons.Clear(); nodeCircles.Clear(); edgeVisuals.Clear();
        foreach (var edge in edges)
        {
            if (!positions.TryGetValue(Text(edge, "source"), out var a) || !positions.TryGetValue(Text(edge, "target"), out var b)) continue;
            var dx = b.X - a.X; var dy = b.Y - a.Y; var length = Math.Max(1, Math.Sqrt(dx * dx + dy * dy));
            var ux = dx / length; var uy = dy / length; var end = new Point(b.X - ux * 20, b.Y - uy * 20);
            var line = new Line { X1 = a.X + ux * 20, Y1 = a.Y + uy * 20, X2 = end.X, Y2 = end.Y, Stroke = Brush("edge"), StrokeThickness = 1.5, IsHitTestVisible = false };
            var arrow = new Polygon { Fill = Brush("edge"), IsHitTestVisible = false, Points = new PointCollection { end, new Point(end.X - ux * 8 - uy * 4, end.Y - uy * 8 + ux * 4), new Point(end.X - ux * 8 + uy * 4, end.Y - uy * 8 - ux * 4) } };
            canvas.Children.Add(line); canvas.Children.Add(arrow); edgeVisuals.Add((edge, line, arrow));
        }
        foreach (var pair in nodes)
        {
            var id = pair.Key; var node = pair.Value; if (!positions.TryGetValue(id, out var point)) continue;
            var content = new StackPanel { Spacing = 3, HorizontalAlignment = HorizontalAlignment.Center };
            var circle = new Ellipse { Width = 32, Height = 32, Fill = Brush(Text(node, "type")), Stroke = Brush("surface"), StrokeThickness = 2, HorizontalAlignment = HorizontalAlignment.Center };
            content.Children.Add(circle);
            content.Children.Add(new TextBlock { Text = Short(Text(node, "label"), 25), FontSize = 12, TextAlignment = TextAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center, MaxWidth = 148, TextTrimming = TextTrimming.CharacterEllipsis });
            content.Children.Add(new TextBlock { Text = $"{TypeName(Text(node, "type"))} · {Text(node, "state")}{(IsFictional(node) ? " · DEMO" : "")}", FontSize = 9, TextAlignment = TextAlignment.Center, MaxWidth = 158, TextTrimming = TextTrimming.CharacterEllipsis });
            var button = new Button { Content = content, Width = 164, Padding = new Thickness(0), BorderThickness = new Thickness(0), Background = new SolidColorBrush(Colors.Transparent), Foreground = Brush("text"), UseSystemFocusVisuals = true };
            Label(button, "MemoryNode_" + id, $"{Text(node, "label")}. {TypeName(Text(node, "type"))}, {Text(node, "state")}{(IsFictional(node) ? ", demo or fictional source" : "")}. Inspect details.");
            ToolTipService.SetToolTip(button, $"{Text(node, "label")}\n{Text(node, "provenance")}");
            button.Click += (_, _) => Select(id);
            button.PointerEntered += (_, _) => { hoverId = id; Highlight(); };
            button.PointerExited += (_, _) => { hoverId = null; Highlight(); };
            button.GotFocus += (_, _) => { hoverId = id; Highlight(); };
            button.LostFocus += (_, _) => { hoverId = null; Highlight(); };
            Canvas.SetLeft(button, point.X - 82); Canvas.SetTop(button, point.Y - 16); canvas.Children.Add(button);
            nodeButtons[id] = button; nodeCircles[id] = circle;
        }
        Highlight();
    }

    private void Highlight()
    {
        var focus = hoverId ?? selectedId; var related = new HashSet<string>(); if (focus is not null) related.Add(focus);
        foreach (var (edge, line, arrow) in edgeVisuals)
        {
            var source = Text(edge, "source"); var target = Text(edge, "target"); var connected = source == focus || target == focus;
            if (connected) { related.Add(source); related.Add(target); }
            line.Stroke = arrow.Fill = Brush(connected ? "highlight" : "edge"); line.StrokeThickness = connected ? 2.5 : 1.5;
            line.Opacity = arrow.Opacity = focus is null || connected || highContrast ? 1 : .22;
        }
        foreach (var pair in nodeButtons)
        {
            pair.Value.Opacity = focus is null || related.Contains(pair.Key) || highContrast ? 1 : .34;
            var circle = nodeCircles[pair.Key]; circle.Stroke = Brush(pair.Key == selectedId ? "highlight" : "surface"); circle.StrokeThickness = pair.Key == selectedId ? 4 : 2;
        }
    }

    private void Select(string id)
    {
        if (!nodes.TryGetValue(id, out var node)) return;
        selectedId = id; details.Children.Clear(); detailsTitle.Text = Text(node, "label"); details.Children.Add(detailsTitle);
        AddDetail($"{TypeName(Text(node, "type"))} · {Text(node, "state")}");
        if (IsFictional(node)) AddDetail("DEMO / FICTIONAL · recorded source metadata", true);
        AddDetail(Text(node, "provenance"));
        if (!string.IsNullOrWhiteSpace(Text(node, "summary"))) AddDetail(Text(node, "summary"));
        if (node["properties"] is JsonObject properties) foreach (var property in properties) AddDetail($"{property.Key.Replace('_', ' ')}: {property.Value}");
        AddDetail("Recorded relationships", true);
        var relationships = edges.Where(e => Text(e, "source") == id || Text(e, "target") == id).ToList();
        if (relationships.Count == 0) AddDetail("No explicit relationships are visible with these filters.");
        foreach (var edge in relationships)
        {
            var outgoing = Text(edge, "source") == id; var otherId = Text(edge, outgoing ? "target" : "source");
            if (!nodes.TryGetValue(otherId, out var other)) continue;
            var relationship = $"{(outgoing ? "→" : "←")} {Text(edge, "type").Replace('_', ' ')} · {Text(other, "label")}";
            var button = Command(relationship, "MemoryRelationship_" + Text(edge, "id"), relationship + ". " + Text(edge, "provenance"), () => { Select(otherId); CenterOn(otherId); });
            button.Content = new TextBlock { Text = relationship, TextWrapping = TextWrapping.Wrap }; button.HorizontalAlignment = HorizontalAlignment.Stretch; button.HorizontalContentAlignment = HorizontalAlignment.Left;
            ToolTipService.SetToolTip(button, Text(edge, "provenance")); details.Children.Add(button); AddDetail(Text(edge, "provenance"));
        }
        foreach (var item in nodeList.Items.OfType<ListViewItem>()) if (item.Tag?.ToString() == id && !ReferenceEquals(nodeList.SelectedItem, item)) { nodeList.SelectedItem = item; nodeList.ScrollIntoView(item); break; }
        Highlight();
    }

    private void ResetDetails()
    {
        details.Children.Clear(); detailsTitle.Text = "Explore a memory"; details.Children.Add(detailsTitle); details.Children.Add(detailsIntro);
    }

    private void AddDetail(string text, bool heading = false) => details.Children.Add(new TextBlock { Text = text, FontSize = heading ? 14 : 12, FontWeight = heading ? Microsoft.UI.Text.FontWeights.SemiBold : Microsoft.UI.Text.FontWeights.Normal, TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true });
    private void Zoom(float multiplier) => viewport.ChangeView(null, null, Math.Clamp(viewport.ZoomFactor * multiplier, viewport.MinZoomFactor, viewport.MaxZoomFactor));
    private void Fit()
    {
        if (positions.Count == 0 || viewport.ActualWidth <= 0 || viewport.ActualHeight <= 0) return;
        var minX = positions.Values.Min(p => p.X) - 100; var minY = positions.Values.Min(p => p.Y) - 60;
        var maxX = positions.Values.Max(p => p.X) + 100; var maxY = positions.Values.Max(p => p.Y) + 85;
        var zoom = (float)Math.Clamp(Math.Min(viewport.ActualWidth / (maxX - minX), viewport.ActualHeight / (maxY - minY)), .25, 1.4);
        viewport.ChangeView(Math.Max(0, (minX + maxX) / 2 * zoom - viewport.ActualWidth / 2), Math.Max(0, (minY + maxY) / 2 * zoom - viewport.ActualHeight / 2), zoom, true);
    }
    private void CenterOn(string id)
    {
        if (positions.TryGetValue(id, out var point)) viewport.ChangeView(Math.Max(0, point.X * viewport.ZoomFactor - viewport.ActualWidth / 2), Math.Max(0, point.Y * viewport.ZoomFactor - viewport.ActualHeight / 2), null);
    }
    private void BeginPan(object sender, PointerRoutedEventArgs args)
    {
        if (args.Pointer.PointerDeviceType != Microsoft.UI.Input.PointerDeviceType.Mouse || !args.GetCurrentPoint(canvas).Properties.IsLeftButtonPressed) return;
        for (var element = args.OriginalSource as DependencyObject; element is not null && !ReferenceEquals(element, canvas); element = VisualTreeHelper.GetParent(element)) if (element is Button) return;
        dragging = true; dragStart = args.GetCurrentPoint(viewport).Position; dragHorizontal = viewport.HorizontalOffset; dragVertical = viewport.VerticalOffset;
        canvas.CapturePointer(args.Pointer); args.Handled = true;
    }
    private void Pan(object sender, PointerRoutedEventArgs args)
    {
        if (!dragging) return; var point = args.GetCurrentPoint(viewport).Position;
        viewport.ChangeView(Math.Max(0, dragHorizontal - point.X + dragStart.X), Math.Max(0, dragVertical - point.Y + dragStart.Y), null, true); args.Handled = true;
    }
    private void EndPan(object sender, PointerRoutedEventArgs args) { if (!dragging) return; dragging = false; canvas.ReleasePointerCapture(args.Pointer); args.Handled = true; }

    private static bool UnsupportedContrastEvent(COMException exception) =>
        exception.HResult == unchecked((int)0x80070490) || // Observed desktop event registration failure.
        exception.HResult == unchecked((int)0x80004001) || // E_NOTIMPL.
        exception.HResult == unchecked((int)0x80070032);   // ERROR_NOT_SUPPORTED.

    private bool ReadHighContrast()
    {
        // SPI_GETHIGHCONTRAST is a desktop API and does not require WinRT event support.
        var value = new HighContrastInfo { Size = (uint)Marshal.SizeOf<HighContrastInfo>() };
        return ReadSystemParameters(0x0042, value.Size, ref value, 0) ? (value.Flags & 0x1) != 0 : highContrast;
    }

    private void StartContrastMonitoring()
    {
        try { accessibility.HighContrastChanged += ContrastChanged; contrastSubscribed = true; }
        catch (COMException exception) when (UnsupportedContrastEvent(exception))
        {
            // Some desktop environments expose the property but cannot register this
            // WinRT event. Poll only for this window's lifetime, and redraw on a delta.
            contrastPoll = DispatcherQueue.CreateTimer(); contrastPoll.Interval = TimeSpan.FromSeconds(2);
            contrastPoll.Tick += (_, _) => RefreshContrast(); contrastPoll.Start();
        }
    }

    private void RefreshContrast()
    {
        if (closed) return;
        var current = ReadHighContrast();
        if (current == highContrast) return;
        highContrast = current; ApplyPalette();
    }

    private void ContrastChanged(AccessibilitySettings sender, object args)
    {
        if (!closed) DispatcherQueue.TryEnqueue(RefreshContrast);
    }
    private void ApplyPalette()
    {
        root.Background = Brush("background"); canvas.Background = Brush("surface");
        if (graphFrame is not null) graphFrame.BorderBrush = Brush("stroke");
        foreach (var (circle, type) in legendCircles) circle.Fill = Brush(type);
        Draw();
    }
    private SolidColorBrush Brush(string role)
    {
        // Purpose-based palette supplies light, dark and Contrast variants. Theme
        // events rebuild graph brushes; Contrast never uses reduced opacity.
        if (highContrast)
        {
            var settings = new UISettings();
            return new SolidColorBrush(settings.GetColorValue(role is "background" or "surface" ? UIColorType.Background : role == "highlight" ? UIColorType.Accent : UIColorType.Foreground));
        }
        var dark = root.ActualTheme != ElementTheme.Light;
        var rgb = role switch
        {
            "knowledge" => dark ? 0x81B8FFu : 0x1755A8u,
            "preference" => dark ? 0xE4A3F5u : 0x8C338Eu,
            "skill" => dark ? 0x81DEC2u : 0x11745Du,
            "run" => dark ? 0xFFC77Bu : 0x8D5310u,
            "source" => dark ? 0xB6BED1u : 0x4B5365u,
            "contract" => dark ? 0xF397A7u : 0xA72D4Bu,
            "highlight" => dark ? 0xFFFFFFu : 0x000000u,
            "background" => dark ? 0x191A20u : 0xF5F5F7u,
            "surface" => dark ? 0x11131Bu : 0xFFFFFFu,
            "edge" => dark ? 0x636D85u : 0x858B99u,
            "stroke" => dark ? 0x363840u : 0xD8DAE0u,
            _ => dark ? 0xF6F6FAu : 0x17191Eu
        };
        return new SolidColorBrush(Color.FromArgb(255, (byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb));
    }
    private static Button Command(string text, string id, string name, Action action)
    {
        var button = new Button { Content = text, UseSystemFocusVisuals = true }; Label(button, id, name); button.Click += (_, _) => action(); return button;
    }
    private static void Label(DependencyObject control, string id, string name) { AutomationProperties.SetAutomationId(control, id); AutomationProperties.SetName(control, name); }
    private static string Text(JsonNode node, string key) => node[key]?.ToString() ?? "";
    private static bool IsFictional(JsonNode node) => node["fictional"]?.GetValue<bool>() == true;
    private static string Short(string text, int length) => text.Length <= length ? text : text[..(length - 1)] + "…";
    private static string TypeName(string type) { var index = Array.IndexOf(Types, type); return index < 0 ? type : TypeNames[index]; }
}
