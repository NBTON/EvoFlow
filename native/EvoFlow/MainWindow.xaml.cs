using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using Windows.Graphics;
using Windows.Storage;
using Windows.Storage.Pickers;
using Windows.System;

namespace EvoFlow;

public sealed partial class MainWindow : Window
{
    private readonly WorkerClient worker = new();
    private readonly ComboBox DemoStage = new() { Header = "Sample dataset", SelectedIndex = 0, HorizontalAlignment = HorizontalAlignment.Stretch };
    private readonly TextBlock AttachmentText = new() { Text = "No dataset attached", TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock RouteText = new() { Text = "No execution yet.", TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock MetricsText = new() { TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock EvidenceText = new() { TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true };
    private readonly DispatcherTimer thinkingTimer = new() { Interval = TimeSpan.FromMilliseconds(40) };
    private readonly DispatcherTimer completionTimer = new() { Interval = TimeSpan.FromSeconds(3) };
    private readonly Windows.UI.ViewManagement.UISettings uiSettings = new();
    private readonly Windows.UI.ViewManagement.AccessibilitySettings accessibility = new();
    private long thinkingStarted;
    private StackPanel? demoTools, diagnosticTools;
    private ScrollViewer? evidenceScroll;
    private bool submitting;
    private bool navigating, demoRequested;
    private string? sessionId;
    private int viewVersion;
    private CancellationTokenSource? viewEvents;
    private int turnCount;
    private NativePresence? presence;
    private readonly CompanionSettings settings = CompanionSettings.Load();
    private readonly DispatcherTimer hoverTimer = new() { Interval = TimeSpan.FromMilliseconds(40) };
    private readonly DispatcherTimer animationTimer = new() { Interval = TimeSpan.FromMilliseconds(16) };
    private SettingsWindow? settingsWindow;
    private MemoryWindow? memoryWindow;
    private bool expanded, closing, ready, positioning, running, interactionHeld, suppressHoverUntilExit;
    private bool changingPin;
    private long enteredAt, outsideAt, animationStarted;
    private RectInt32 animationFrom, animationTo;
    private string connectionStatus = "Connecting automatically…";
    private string? runId;
    private JsonNode? attachment, lastRun;
    private DispatcherTimer? monitorTimer;
    private IReadOnlyList<DisplayArea> monitors = [];
    public MainWindow()
    {
        InitializeComponent();
        worker.ApproveTool = ReviewToolAsync;
        worker.ToolProgress = tool => DispatcherQueue.TryEnqueue(() =>
        {
            if (closing) return;
            string summary = tool["summary"]?.ToString() ?? "Local tool";
            WorkingText.Text = summary;
            AddMessage("Local action", summary + (tool["status"] is null ? "" : $"\n{tool["status"]} · verified: {tool["verified"]}"));
        });
        foreach (string stage in new[] { "1 · First task", "2 · New records, same rules", "3 · Split shipments and new schema", "4 · Updated shipping policy" }) DemoStage.Items.Add(stage);
        DemoStage.SelectedIndex = 0;
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetAutomationId(DemoStage, "DemoStage");
        thinkingTimer.Tick += (_, _) => UpdateThinking();
        completionTimer.Tick += (_, _) => { completionTimer.Stop(); if (!running) SetStatus("Ready"); };
        var presenter = OverlappedPresenter.Create(); presenter.SetBorderAndTitleBar(false, false);
        presenter.IsResizable = false; presenter.IsMaximizable = false; presenter.IsMinimizable = false; presenter.IsAlwaysOnTop = settings.AlwaysOnTop;
        AppWindow.SetPresenter(presenter); AppWindow.IsShownInSwitchers = false;
        AppWindow.SetIcon(Path.Combine(AppContext.BaseDirectory, "Assets", "AppIcon.ico"));
        presence = new NativePresence(this, TogglePanel, async command =>
        {
            if (command == 1) SetExpanded(true);
            else if (command == 2 && ready) await Guard(async () => { var r = await worker.PostAsync("pause"); Say(r["paused"]!.GetValue<bool>() ? "Paused" : "Ready"); });
            else if (command == 3) OpenSettings();
            else if (command == 4) Close();
            else if (command == 5) OpenMemory();
        }, () => { RefreshMonitors(); Position(); });
        presence.SetHotkey(settings.ShortcutIndex == 0 ? 0x45u : 0x46u);
        hoverTimer.Tick += CheckHover;
        animationTimer.Tick += AnimateFrame;
        Root.GotFocus += (_, _) =>
        {
            if (Microsoft.UI.Xaml.Input.FocusManager.GetFocusedElement(Root.XamlRoot) is Control control && control.FocusState == FocusState.Keyboard)
                HoldForInteraction();
        };
        Closed += async (_, _) =>
        {
            closing = true; viewEvents?.Cancel(); viewEvents?.Dispose(); monitorTimer?.Stop(); hoverTimer.Stop(); animationTimer.Stop(); thinkingTimer.Stop(); completionTimer.Stop();
            settingsWindow?.Close(); memoryWindow?.Close(); presence?.Dispose(); await worker.DisposeAsync();
        };
        Root.Loaded += async (_, _) =>
        {
            RefreshMonitors(); Position();
            monitorTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
            monitorTimer.Tick += (_, _) => { if (!closing) { presence?.AvoidFullscreen(expanded, settings.AlwaysOnTop); if (!animationTimer.IsEnabled) Position(); } };
            monitorTimer.Start();
            await Guard(async () => { await worker.StartAsync(); ready = true; await RestoreConversation(); NewChatButton.IsEnabled = ChatHistoryButton.IsEnabled = true; await Refresh(); });
        };
        Position();
    }
    private void Position()
    {
        if (positioning || closing) return; positioning = true;
        try
        {
            AppWindow.MoveAndResize(TargetBounds());
            presence?.RoundCorners(expanded);
        }
        finally { positioning = false; }
    }
    private RectInt32 TargetBounds()
    {
        var area = monitors.FirstOrDefault(m => m.DisplayId.Value == settings.DisplayId) ?? DisplayArea.GetFromWindowId(AppWindow.Id, DisplayAreaFallback.Primary);
        double scale = NativePresence.DpiForArea(area);
        var display = area.OuterBounds;
        int width = Math.Max(1, Math.Min((int)((expanded ? 560 : 280) * scale), display.Width - 16));
        int height = Math.Max(1, Math.Min((int)((expanded ? 520 : 44) * scale), Math.Min(display.Height, area.WorkArea.Y + area.WorkArea.Height - display.Y)));
        // Anchor to the physical display, not the taskbar-adjusted work area.
        return new RectInt32(display.X + (display.Width - width) / 2, display.Y, width, height);
    }
    private void RefreshMonitors()
    {
        // Keep WinRT's indexed collection at this boundary. Its IEnumerable projection can
        // fail under packaged activation, while the Count/indexer ABI remains available.
        var displays = DisplayArea.FindAll();
        var snapshot = new DisplayArea[displays.Count];
        for (int index = 0; index < snapshot.Length; index++) snapshot[index] = displays[index];
        monitors = snapshot;
        settingsWindow?.UpdateDisplays(monitors);
    }
    public void SetExpanded(bool value) => ChangeExpanded(value, activate: value);
    private void ChangeExpanded(bool value, bool activate)
    {
        enteredAt = outsideAt = 0;
        if (activate) HoldForInteraction();
        if (!value)
        {
            interactionHeld = false; suppressHoverUntilExit = true;
            changingPin = true; PinButton.IsChecked = false; changingPin = false;
            InteractionHint.Text = "Enter to send · Shift+Enter for a new line";
        }
        bool changed = expanded != value;
        expanded = value;
        AppWindow.IsShownInSwitchers = value;
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(Pill, value ? interactionHeld ? "EvoFlow, collapse task panel" : "EvoFlow, keep task panel open" : "EvoFlow, open task panel");
        if (value) { Body.Visibility = Visibility.Visible; presence?.ShowWithoutActivation(); }
        hoverTimer.Start();
        if (changed)
        {
            animationTimer.Stop();
            animationFrom = new RectInt32(AppWindow.Position.X, AppWindow.Position.Y, AppWindow.Size.Width, AppWindow.Size.Height);
            animationTo = TargetBounds(); animationStarted = Stopwatch.GetTimestamp();
            if (new Windows.UI.ViewManagement.UISettings().AnimationsEnabled) animationTimer.Start();
            else { Position(); Body.Visibility = value ? Visibility.Visible : Visibility.Collapsed; Body.Opacity = 1; }
        }
        if (activate) { Activate(); TaskText.Focus(FocusState.Programmatic); }
    }
    private void AnimateFrame(object? sender, object e)
    {
        double progress = Math.Clamp(Stopwatch.GetElapsedTime(animationStarted).TotalMilliseconds / 180, 0, 1);
        double eased = 1 - Math.Pow(1 - progress, 3);
        int Tween(int start, int end) => (int)Math.Round(start + (end - start) * eased);
        AppWindow.MoveAndResize(new RectInt32(Tween(animationFrom.X, animationTo.X), animationTo.Y, Tween(animationFrom.Width, animationTo.Width), Tween(animationFrom.Height, animationTo.Height)));
        presence?.RoundCorners(expanded);
        Body.Opacity = expanded ? Math.Min(1, progress * 1.5) : 1 - progress;
        if (progress >= 1) { animationTimer.Stop(); Body.Visibility = expanded ? Visibility.Visible : Visibility.Collapsed; Body.Opacity = 1; }
    }
    private void CheckHover(object? sender, object e)
    {
        if (closing || presence is null) return;
        // A small tolerance around the physical surface keeps resize/corner exits from breaking
        // the continuous pointer corridor from the pill into the expanded task panel.
        bool inside = presence.IsPointerWithinWindow(expanded ? 8 : 0);
        long now = Stopwatch.GetTimestamp();
        if (!inside) { enteredAt = 0; suppressHoverUntilExit = false; if (!expanded) hoverTimer.Stop(); }
        if (!expanded)
        {
            if (!inside || !settings.ExpandOnHover || suppressHoverUntilExit) return;
            if (enteredAt == 0) enteredAt = now;
            if (Stopwatch.GetElapsedTime(enteredAt).TotalMilliseconds >= 180) ChangeExpanded(true, activate: false);
            return;
        }
        if (inside || interactionHeld || PinButton.IsChecked == true || running || presence.IsMenuOpen || animationTimer.IsEnabled) { outsideAt = 0; return; }
        if (outsideAt == 0) outsideAt = now;
        if (Stopwatch.GetElapsedTime(outsideAt).TotalMilliseconds >= 700) ChangeExpanded(false, activate: false);
    }
    private void OnPointerEntered(object sender, PointerRoutedEventArgs e) { outsideAt = 0; if (enteredAt == 0) enteredAt = Stopwatch.GetTimestamp(); hoverTimer.Start(); }
    private void OnPointerExited(object sender, PointerRoutedEventArgs e) { enteredAt = 0; }
    private void HoldForInteraction()
    {
        interactionHeld = true; outsideAt = 0; InteractionHint.Text = "Enter to send · Shift+Enter for a new line";
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(Pill, "EvoFlow, collapse task panel");
    }
    private void OnBodyInteraction(object sender, PointerRoutedEventArgs e) => HoldForInteraction();
    private void PinChanged(object sender, RoutedEventArgs e)
    {
        if (changingPin || InteractionHint is null) return;
        interactionHeld = PinButton.IsChecked == true;
        InteractionHint.Text = interactionHeld ? "Panel pinned · Enter to send · Escape to collapse" : "Enter to send · Shift+Enter for a new line";
        outsideAt = 0;
    }
    private void ToggleExpanded(object sender, RoutedEventArgs e) => TogglePanel();
    private void TogglePanel()
    {
        if (expanded && !interactionHeld) { HoldForInteraction(); Activate(); TaskText.Focus(FocusState.Programmatic); }
        else SetExpanded(!expanded);
    }
    private void OnKeyDown(object sender, KeyRoutedEventArgs e) { if (e.Key == VirtualKey.Escape) { SetExpanded(false); e.Handled = true; } }
    private void SetStatus(string status) { PillStatus.Text = status; presence?.UpdateStatus(status); }
    private Task<bool> ReviewToolAsync(JsonNode action, CancellationToken cancellationToken)
    {
        var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        DispatcherQueue.TryEnqueue(() =>
        {
            if (closing || cancellationToken.IsCancellationRequested) { completion.TrySetResult(false); return; }
            ChangeExpanded(true, activate: false); HoldForInteraction(); SetStatus("Approval needed");
            var panel = new StackPanel { Spacing = 8, Padding = new Thickness(12), Background = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["CompanionSurfaceBrush"] };
            panel.Children.Add(new TextBlock { Text = "Approve local action", FontWeight = Microsoft.UI.Text.FontWeights.SemiBold });
            var description = new TextBlock { Text = $"{action["summary"]}\nRisk: {action["risk"]}\nApproval applies to this exact action in this request.", TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true };
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetAutomationId(description, "ToolApprovalDescription");
            // Keep the decision buttons visible even when paths/arguments wrap over
            // several lines. The complete action remains scrollable and selectable.
            panel.Children.Add(new ScrollViewer { Content = description, MaxHeight = 84,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled });
            var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
            var approve = new Button { Content = "Approve action" }; var deny = new Button { Content = "Decline action" };
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetAutomationId(approve, "ApproveToolButton");
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetAutomationId(deny, "DeclineToolButton");
            void Finish(bool approved)
            {
                approve.IsEnabled = deny.IsEnabled = false;
                ApprovalItems.Children.Remove(panel);
                if (ApprovalItems.Children.Count == 0) ApprovalsScroll.Visibility = Visibility.Collapsed;
                completion.TrySetResult(approved);
            }
            approve.Click += (_, _) => Finish(true); deny.Click += (_, _) => Finish(false);
            buttons.Children.Add(approve); buttons.Children.Add(deny); panel.Children.Add(buttons);
            ApprovalItems.Children.Add(panel); ApprovalsScroll.Visibility = Visibility.Visible;
            var registration = cancellationToken.Register(() => DispatcherQueue.TryEnqueue(() => Finish(false)));
            _ = completion.Task.ContinueWith(_ => registration.Dispose(), TaskScheduler.Default);
        });
        return completion.Task.WaitAsync(cancellationToken);
    }
    private void Say(string message, InfoBarSeverity severity = InfoBarSeverity.Informational) { Notice.Message = message; Notice.Severity = severity; Notice.IsOpen = true; }
    private async Task Guard(Func<Task> action) { try { await action(); } catch (Exception ex) { SetStatus("Needs attention"); Say(ex.Message, InfoBarSeverity.Error); } }
    private void OnTaskKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key != VirtualKey.Enter) return;
        bool shift = (Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Shift) & Windows.UI.Core.CoreVirtualKeyStates.Down) != 0;
        if (shift) return;
        e.Handled = true;
        RunTask(sender, e);
    }
    private async void RunTask(object sender, RoutedEventArgs e)
    {
        if (!ready || navigating || running || submitting || sessionId is null || string.IsNullOrWhiteSpace(TaskText.Text)) return;
        string submittedSession = sessionId;
        int submittedView = viewVersion;
        string task = TaskText.Text.Trim();
        bool isDemo = demoRequested;
        demoRequested = false;
        AddMessage("You", task); TaskText.Text = "";
        submitting = true;
        BeginRunPresentation();
        try
        {
            JsonNode response;
            if (isDemo)
            {
                // Sample workflows are available only through the explicit Settings demo action.
                var body = new JsonObject { ["task"] = task, ["demo_stage"] = DemoStage.SelectedIndex + 1, ["session_id"] = submittedSession, ["demo"] = true };
                if (attachment is not null) body["dataset"] = attachment.DeepClone();
                response = await worker.PostAsync("runs", body);
            }
            else
            {
                var body = new JsonObject { ["message"] = task };
                if (attachment is not null) body["dataset"] = attachment.DeepClone();
                response = await worker.PostAsync($"chats/{submittedSession}/messages", body);
            }
            if (!IsCurrentView(submittedSession, submittedView)) return;
            ClearAttachedDataset();
            runId = response["run_id"]!.GetValue<string>();
            submitting = false;
            await MonitorRun(runId, submittedSession, submittedView, isDemo);
        }
        catch (Exception ex)
        {
            if (!IsCurrentView(submittedSession, submittedView)) return;
            SetStatus("Needs attention"); AddMessage("EvoFlow", "Unable to complete the request: " + ex.Message); EndRunPresentation();
        }
    }
    private bool IsCurrentView(string expectedSession, int expectedView) => !closing && sessionId == expectedSession && viewVersion == expectedView;
    private void BeginRunPresentation()
    {
        running = true; HoldForInteraction(); runId = null; lastRun = null;
        RunButton.IsEnabled = false; CancelButton.IsEnabled = false; CancelButton.Visibility = Visibility.Visible;
        DemoStage.IsEnabled = false; completionTimer.Stop(); WorkingPanel.Visibility = Visibility.Visible; WorkingRing.IsActive = true;
        RouteText.Text = "Choosing a model for this message…"; MetricsText.Text = ""; EvidenceText.Text = "";
        thinkingStarted = Stopwatch.GetTimestamp(); thinkingTimer.Start(); UpdateThinking(); SetStatus("Working");
    }
    private void EndRunPresentation()
    {
        running = false; submitting = false; outsideAt = 0; RunButton.IsEnabled = ready && !navigating;
        CancelButton.IsEnabled = false; CancelButton.Visibility = Visibility.Collapsed; DemoStage.IsEnabled = true;
        thinkingTimer.Stop(); ThinkingGlow.Visibility = Visibility.Collapsed; WorkingRing.IsActive = false; WorkingPanel.Visibility = Visibility.Collapsed;
    }
    private async Task MonitorRun(string observedRun, string observedSession, int observedView, bool isDemo = false)
    {
        viewEvents?.Cancel(); viewEvents?.Dispose(); viewEvents = new CancellationTokenSource();
        var cancellation = viewEvents.Token;
        CancelButton.IsEnabled = true;
        try
        {
            await foreach (var ev in worker.EventsAsync(observedRun, cancellation))
            {
                if (!IsCurrentView(observedSession, observedView)) return;
                EvidenceText.Text += $"• {ev["message"]}\n";
                if (ev["kind"]?.ToString() is "tool_requested" or "tool_result") WorkingText.Text = ev["message"]!.ToString();
                SetStatus(ev["kind"]?.ToString() switch { "reuse" => "Reusing a skill", "evaluation" => "Testing improvement", _ => "Working" });
            }
            var completed = await worker.GetAsync($"runs/{observedRun}", cancellation);
            if (!IsCurrentView(observedSession, observedView)) return;
            if (!isDemo && completed["session_id"]?.ToString() != observedSession)
                throw new InvalidOperationException("The response belongs to a different conversation.");
            lastRun = completed;
            if (completed["status"]?.ToString() == "complete")
            {
                string? reply = completed["assistant_response"]?.ToString();
                if (isDemo && completed["result"] is JsonArray rows)
                {
                    int delayed = rows.Count(r => r!["delayed"]!.GetValue<int>() == 1), incomplete = rows.Count(r => r!["remaining"]!.GetValue<int>() > 0);
                    string origin = completed["data_origin"]?.ToString() == "fictional-demo" ? "Fictional sample demonstration" : "Attached dataset reconciliation";
                    reply = $"{origin}\n{rows.Count} orders checked · {delayed} delayed · {incomplete} incomplete\n\n{completed["report"]}";
                }
                if (string.IsNullOrWhiteSpace(reply)) throw new InvalidOperationException("The model returned no response. Please try again.");
                AddMessage("EvoFlow", reply);
                bool toolAttention = completed["tool_evidence"] is JsonArray toolEvidence && toolEvidence.Any(t => t?["status"]?.ToString() != "succeeded" || t?["verified"]?.GetValue<bool>() != true);
                SetStatus(toolAttention ? "Needs attention" : "✓ Complete");
                if (!toolAttention) completionTimer.Start();
            }
            else if (completed["status"]?.ToString() == "canceled") { SetStatus("Ready"); AddMessage("EvoFlow", "Request canceled."); }
            else { SetStatus("Needs attention"); AddMessage("EvoFlow", completed["error"]?.ToString() ?? "The request could not be completed."); }
            UpdateRunDiagnostics(completed);
            await Refresh();
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
        catch (Exception ex)
        {
            if (IsCurrentView(observedSession, observedView)) { SetStatus("Needs attention"); AddMessage("EvoFlow", "Unable to complete the request: " + ex.Message); }
        }
        finally { if (IsCurrentView(observedSession, observedView)) EndRunPresentation(); }
    }
    private void UpdateRunDiagnostics(JsonNode completed)
    {
        var route = completed["routing"]?["decisions"]?.AsArray().LastOrDefault();
        if (route?["effort_status"]?.ToString() == "no_inference") RouteText.Text = "Verified workflow reused · no synthesis inference requested";
        else if (route is not null)
        {
            string model = route["effective_model"]?.ToString() ?? route["requested_model"]?.ToString() ?? "Configured model";
            string? effort = route["submitted_effort"]?.ToString();
            RouteText.Text = effort is null ? $"{model} · effort control not submitted" : $"{model} · effort {effort} submitted; provider unconfirmed";
        }
        else RouteText.Text = "No model invocation details were returned. See execution evidence.";
        if (completed["metrics"] is JsonNode m)
            MetricsText.Text = $"{m["model_calls"]} model calls · {m["input_tokens"]} input + {m["output_tokens"]} output tokens\n{m["end_to_end_ms"]?.GetValue<double>() / 1000:F1}s elapsed";
    }
    private async Task RestoreConversation()
    {
        var chats = await worker.GetAsync("chats");
        string? latest = chats["sessions"]?.AsArray().FirstOrDefault()?["session_id"]?.ToString();
        var chat = latest is null ? await worker.PostAsync("chats") : await worker.GetAsync($"chats/{latest}");
        await LoadConversation(chat);
    }
    private async void NewChat(object sender, RoutedEventArgs e)
    {
        if (!ready || navigating) return;
        await NavigateConversation(async () => await worker.PostAsync("chats"));
    }
    private async Task NavigateConversation(Func<Task<JsonNode>> load)
    {
        navigating = true; NewChatButton.IsEnabled = ChatHistoryButton.IsEnabled = RunButton.IsEnabled = false;
        try { await LoadConversation(await load()); }
        catch (Exception ex) { Say("Could not open the conversation: " + ex.Message, InfoBarSeverity.Error); }
        finally
        {
            navigating = false; NewChatButton.IsEnabled = ChatHistoryButton.IsEnabled = ready;
            RunButton.IsEnabled = ready && !running;
        }
        TaskText.Focus(FocusState.Programmatic);
    }
    private async Task LoadConversation(JsonNode chat)
    {
        viewEvents?.Cancel(); viewEvents?.Dispose(); viewEvents = null;
        ++viewVersion;
        sessionId = chat["session_id"]!.GetValue<string>();
        string loadedSession = sessionId;
        int loadedView = viewVersion;
        EndRunPresentation(); completionTimer.Stop(); SetStatus("Ready");
        runId = null; lastRun = null; demoRequested = false; ClearAttachedDataset();
        RouteText.Text = "No execution in this conversation."; MetricsText.Text = EvidenceText.Text = "";
        TaskText.Text = ""; Notice.IsOpen = false;
        Conversation.Children.Clear(); Conversation.Children.Add(WelcomeText); WelcomeText.Visibility = Visibility.Visible; turnCount = 0;
        if (chat["messages"] is JsonArray messages)
            foreach (var message in messages)
            {
                string? role = message?["role"]?.ToString();
                if (role is "user" or "assistant") AddMessage(role == "user" ? "You" : "EvoFlow", message?["content"]?.ToString() ?? "");
            }
        string? active = chat["active_run_id"]?.ToString();
        if (!string.IsNullOrEmpty(active))
        {
            BeginRunPresentation(); runId = active;
            _ = MonitorRun(active, loadedSession, loadedView);
        }
        else
        {
            string? previous = chat["messages"]?.AsArray().LastOrDefault(m => m?["run_id"] is not null)?["run_id"]?.ToString();
            if (previous is not null)
            {
                var completed = await worker.GetAsync($"runs/{previous}");
                if (IsCurrentView(loadedSession, loadedView) && completed["session_id"]?.ToString() == loadedSession)
                {
                    runId = previous; lastRun = completed; UpdateRunDiagnostics(completed);
                    if (completed["events"] is JsonArray events)
                        EvidenceText.Text = string.Join("\n", events.Select(ev => "• " + ev?["message"]?.ToString()));
                }
            }
        }
        TaskText.Focus(FocusState.Programmatic);
    }
    private async void ShowChatHistory(object sender, RoutedEventArgs e)
    {
        if (!ready || navigating) return;
        ChatHistoryButton.IsEnabled = false;
        try
        {
            var chatsRequest = worker.GetAsync("chats");
            var stateRequest = worker.GetAsync("state");
            await Task.WhenAll(chatsRequest, stateRequest);
            var result = await chatsRequest;
            var state = await stateRequest;
            if (closing) return;
            var menu = new MenuFlyout();
            if (result["sessions"] is JsonArray sessions)
                foreach (var session in sessions)
                {
                    string? id = session?["session_id"]?.ToString();
                    if (id is null) continue;
                    string title = session?["title"]?.ToString() ?? "New Chat";
                    if (title.Length > 44) title = title[..41] + "…";
                    var item = new MenuFlyoutItem { Text = title + (id == sessionId ? " · current" : "") };
                    Microsoft.UI.Xaml.Automation.AutomationProperties.SetAutomationId(item, "Chat_" + id);
                    item.Click += async (_, _) => { if (!navigating) await NavigateConversation(async () => await worker.GetAsync($"chats/{id}")); };
                    menu.Items.Add(item);
                }
            if (menu.Items.Count == 0) menu.Items.Add(new MenuFlyoutItem { Text = "No saved chats yet", IsEnabled = false });
            if (state["runs"] is JsonArray runs)
            {
                var previousResults = new MenuFlyoutSubItem { Text = "Previous task results" };
                Microsoft.UI.Xaml.Automation.AutomationProperties.SetAutomationId(previousResults, "LegacyTaskHistory");
                foreach (var run in runs.Where(run => run?["session_id"] is null))
                {
                    if (run is null) continue;
                    var archived = run.DeepClone();
                    string title = archived["task"]?.ToString() ?? "Previous task";
                    if (title.Length > 40) title = title[..37] + "…";
                    string origin = archived["data_origin"]?.ToString() == "fictional-demo" ? " · fictional demo" : " · task result";
                    var item = new MenuFlyoutItem { Text = title + origin };
                    Microsoft.UI.Xaml.Automation.AutomationProperties.SetAutomationId(item, "LegacyTask_" + archived["id"]?.ToString());
                    item.Click += (_, _) => new DetailWindow(archived).Activate();
                    previousResults.Items.Add(item);
                }
                if (previousResults.Items.Count > 0) { menu.Items.Add(new MenuFlyoutSeparator()); menu.Items.Add(previousResults); }
            }
            menu.ShowAt(ChatHistoryButton);
        }
        catch (Exception ex) { Say("Could not load chat history: " + ex.Message, InfoBarSeverity.Error); }
        finally { ChatHistoryButton.IsEnabled = ready && !navigating; }
    }
    private void AddMessage(string author, string text)
    {
        WelcomeText.Visibility = Visibility.Collapsed;
        var message = new StackPanel { Spacing = 4 };
        message.Children.Add(new TextBlock { Text = author, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold });
        message.Children.Add(new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true });
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetAutomationId(message, "Message_" + ++turnCount);
        Conversation.Children.Add(message);
        // Bound the visible transcript; durable run evidence remains in the worker store.
        if (Conversation.Children.Count > 81) Conversation.Children.RemoveAt(1);
        DispatcherQueue.TryEnqueue(() => { ConversationScroll.UpdateLayout(); ConversationScroll.ChangeView(null, ConversationScroll.ScrollableHeight, null, true); });
    }
    private void UpdateThinking()
    {
        ThinkingGlow.Visibility = running && !expanded && !accessibility.HighContrast ? Visibility.Visible : Visibility.Collapsed;
        double phase = uiSettings.AnimationsEnabled ? (Stopwatch.GetElapsedTime(thinkingStarted).TotalSeconds % 2.8) / 2.8 : 0.5;
        double center = -0.25 + 1.5 * phase;
        GlowStart.Offset = center - 0.3; GlowCenter.Offset = center; GlowEnd.Offset = center + 0.3;
        ThinkingGlow.Opacity = uiSettings.AnimationsEnabled ? 0.16 + 0.04 * Math.Sin(phase * Math.PI * 2) : 0.16;
        WorkingText.Text = PillStatus.Text;
    }
    private async void CancelTask(object sender, RoutedEventArgs e)
    {
        if (!running || runId is null || sessionId is null || !CancelButton.IsEnabled) return;
        string canceledSession = sessionId, canceledRun = runId;
        int canceledView = viewVersion;
        CancelButton.IsEnabled = false; SetStatus("Canceling…");
        try { await worker.PostAsync($"runs/{canceledRun}/cancel"); }
        catch (Exception ex)
        {
            if (IsCurrentView(canceledSession, canceledView)) { CancelButton.IsEnabled = running; Say("Could not cancel: " + ex.Message, InfoBarSeverity.Error); }
        }
    }
    private async void Attach(object sender, RoutedEventArgs e) => await Guard(async () =>
    {
        string? attachedSession = sessionId;
        int attachedView = viewVersion;
        var picker = new FileOpenPicker(); picker.FileTypeFilter.Add(".json"); WinRT.Interop.InitializeWithWindow.Initialize(picker, App.WindowHandle); var file = await picker.PickSingleFileAsync(); if (file is null) return;
        if ((await file.GetBasicPropertiesAsync()).Size > 8_000_000) throw new InvalidOperationException("Dataset must be below 8 MB");
        var selected = JsonNode.Parse(await FileIO.ReadTextAsync(file)) ?? throw new InvalidOperationException("The JSON file contains no data.");
        if (selected.ToJsonString().Length > 32_000) throw new InvalidOperationException("Use a smaller JSON dataset (at most 32,000 characters) so the model can read all attached records.");
        if (attachedSession is null || !IsCurrentView(attachedSession, attachedView)) return;
        attachment = selected; AttachmentText.Text = file.Name + " · attached to the next message";
        TaskText.Header = "Message · " + (file.Name.Length > 36 ? file.Name[..33] + "…" : file.Name) + " attached";
    });
    private void ClearAttachedDataset() { attachment = null; AttachmentText.Text = "No dataset attached"; TaskText.Header = "Message"; }
    private void ClearAttachment(object sender, RoutedEventArgs e) => ClearAttachedDataset();
    private void ShowSettings(object sender, RoutedEventArgs e) => OpenSettings();
    private void OpenSettings()
    {
        if (closing) return;
        ChangeExpanded(false, activate: false);
        if (settingsWindow is null)
        {
            var tools = BuildTools();
            settingsWindow = new SettingsWindow(worker, settings, ApplySettings, tools);
            settingsWindow.Closed += (_, _) =>
            {
                // Explicitly detach reusable controls from the closed window's logical tree.
                // FrameworkElement.Parent can be null once its visual tree is disconnected.
                demoTools?.Children.Clear(); diagnosticTools?.Children.Clear();
                if (evidenceScroll is not null) evidenceScroll.Content = null;
                demoTools = diagnosticTools = null; evidenceScroll = null;
                settingsWindow = null;
            };
            settingsWindow.UpdateDisplays(monitors);
        }
        settingsWindow.UpdateConnectionStatus(connectionStatus);
        settingsWindow.Activate();
    }
    private FrameworkElement BuildTools()
    {
        var tools = new StackPanel { Spacing = 12 };
        Button Command(string label, string id, RoutedEventHandler handler)
        {
            var button = new Button { Content = label };
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetAutomationId(button, id);
            button.Click += handler; return button;
        }
        void Section(string title, string id, StackPanel content)
        {
            var section = new Expander { Header = title, Content = content, HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Stretch };
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetAutomationId(section, id); tools.Children.Add(section);
        }
        var demos = demoTools = new StackPanel { Spacing = 10 };
        demos.Children.Add(new TextBlock { Text = "Ordinary messages always use the conversational assistant. Attach JSON to include your data in the next message, or run a separate, explicitly labeled reconciliation demonstration below.", TextWrapping = TextWrapping.Wrap });
        demos.Children.Add(DemoStage); demos.Children.Add(AttachmentText);
        demos.Children.Add(Command("Attach JSON", "AttachButton", Attach));
        demos.Children.Add(Command("Clear attachment", "ClearAttachmentButton", ClearAttachment));
        demos.Children.Add(Command("Run sample demonstration", "UseDemoButton", (sender, args) =>
        {
            if (!ready || navigating || running || submitting) { Say("Wait for this request or start a new chat before running a demonstration."); return; }
            // Execute directly from this explicit action. Editing a later chat message cannot retain a demo flag.
            ClearAttachedDataset();
            TaskText.Text = "Reconcile orders, inventory and shipments. Identify delayed and incomplete orders and explain supported causes.";
            demoRequested = true; settingsWindow?.Close(); SetExpanded(true); RunTask(sender, args);
        }));
        Section("Demos", "DemosSection", demos);
        var diagnostics = diagnosticTools = new StackPanel { Spacing = 10 };
        diagnostics.Children.Add(RouteText); diagnostics.Children.Add(MetricsText);
        diagnostics.Children.Add(Command("View results and model effort", "DetailsButton", ShowDetails));
        diagnostics.Children.Add(Command("Export evidence", "ExportButton", Export));
        evidenceScroll = new ScrollViewer { Content = EvidenceText, MaxHeight = 180 };
        diagnostics.Children.Add(evidenceScroll);
        Section("Advanced / Diagnostics", "DiagnosticsSection", diagnostics);
        var benchmarks = new StackPanel { Spacing = 10 };
        benchmarks.Children.Add(Command("Measured benchmark comparison", "BenchmarkButton", ShowBenchmark));
        Section("Benchmarks", "BenchmarksSection", benchmarks);
        return tools;
    }
    private void ApplySettings()
    {
        if (AppWindow.Presenter is OverlappedPresenter presenter) presenter.IsAlwaysOnTop = settings.AlwaysOnTop;
        bool shortcutRegistered = presence?.SetHotkey(settings.ShortcutIndex == 0 ? 0x45u : 0x46u) == true;
        settingsWindow?.ShowShortcutStatus(shortcutRegistered);
        Position();
    }
    private void ShowMemory(object sender, RoutedEventArgs e) => OpenMemory();
    private void OpenMemory()
    {
        if (closing) return;
        ChangeExpanded(false, activate: false);
        if (memoryWindow is null) { memoryWindow = new MemoryWindow(worker); memoryWindow.Closed += (_, _) => memoryWindow = null; }
        memoryWindow.Activate();
    }
    private async Task Refresh()
    {
        var state = await worker.GetAsync("state");
        connectionStatus = $"Cloud: {state["health"]?["supabase"]} · Windows-protected device session";
        settingsWindow?.UpdateConnectionStatus(connectionStatus);
    }
    private async void Export(object sender, RoutedEventArgs e) => await Guard(async () =>
    {
        if (runId is null) return; var evidence = await worker.GetAsync($"export/{runId}"); var picker = new FileSavePicker { SuggestedFileName = $"EvoFlow-evidence-{runId[..8]}" }; picker.FileTypeChoices.Add("JSON evidence", new List<string> { ".json" });
        WinRT.Interop.InitializeWithWindow.Initialize(picker, App.WindowHandle); var file = await picker.PickSaveFileAsync(); if (file is not null) { await FileIO.WriteTextAsync(file, evidence.ToJsonString(new JsonSerializerOptions { WriteIndented = true })); Say("Evidence bundle exported."); }
    });
    private void ShowDetails(object sender, RoutedEventArgs e) { new DetailWindow(lastRun).Activate(); }
    private void ShowBenchmark(object sender, RoutedEventArgs e) { new DetailWindow(lastRun, true).Activate(); }
}
