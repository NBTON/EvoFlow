using EvoFlow.Connectors;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using System.Text.Json;

namespace EvoFlow;

internal sealed class ConnectionsPanel : StackPanel
{
    readonly McpBroker broker;
    readonly ComboBox servers = Id(new ComboBox { Header = "MCP connections", HorizontalAlignment = HorizontalAlignment.Stretch }, "McpConnections");
    readonly TextBlock status = Id(new TextBlock { TextWrapping = TextWrapping.Wrap }, "McpStatus");
    readonly TextBox identity = Id(new TextBox { Header = "Connection ID", PlaceholderText = "my_server" }, "McpId");
    readonly TextBox name = Id(new TextBox { Header = "Display name" }, "McpName");
    readonly ComboBox transport = Id(new ComboBox { Header = "Transport", ItemsSource = new[] { "stdio", "http" }, SelectedIndex = 0 }, "McpTransport");
    readonly TextBox command = Id(new TextBox { Header = "Local server executable (.exe, absolute path)" }, "McpCommand");
    readonly TextBox arguments = Id(new TextBox { Header = "Launch arguments (JSON array)", Text = "[]" }, "McpArguments");
    readonly TextBox endpoint = Id(new TextBox { Header = "Streamable HTTP endpoint", PlaceholderText = "https://server.example/mcp" }, "McpEndpoint");
    readonly ComboBox authentication = Id(new ComboBox { Header = "Authentication", ItemsSource = new[] { "none", "bearer", "oauth" }, SelectedIndex = 0 }, "McpAuthentication");
    readonly PasswordBox bearer = Id(new PasswordBox { Header = "Bearer credential (leave empty to keep)", PlaceholderText = "Stored with Windows DPAPI" }, "McpBearer");
    readonly TextBox clientId = Id(new TextBox { Header = "OAuth client ID (optional for dynamic registration)" }, "McpOAuthClientId");
    readonly TextBox reviewed = Id(new TextBox { Header = "Reviewed data tool names (comma separated)", PlaceholderText = "Discover tools first, then review and enable" }, "McpReviewedTools");
    readonly CheckBox enabled = Id(new CheckBox { Content = "Enable connection" }, "McpEnabled");
    readonly CheckBox retain = Id(new CheckBox { Content = "Keep local execution evidence (includes observed app/page content)" }, "McpRetainEvidence");
    readonly TextBox tools = Id(new TextBox { Header = "Discovered tools and contracts", IsReadOnly = true, AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, MaxHeight = 230 }, "McpTools");
    readonly ComboBox toolChoice = Id(new ComboBox { Header = "Inspect a discovered tool", HorizontalAlignment = HorizontalAlignment.Stretch }, "McpToolChoice");
    readonly CancellationTokenSource lifetime = new();
    ConnectionConfig? selected;
    bool refreshing;
    public ConnectionsPanel(WorkerClient worker)
    {
        broker = worker.Connections ?? throw new InvalidOperationException("Connection broker is not ready."); Spacing = 10;
        Children.Add(new TextBlock { Text = "MCP and Cua Driver", FontSize = 18, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold });
        Children.Add(new TextBlock { Text = "Cua runs inside EvoFlow through managed private MCP. Desktop is scoped to Calculator; browser work uses a separate isolated profile at selenium.dev. Each call uses native approval. Other data connectors support local stdio and Streamable HTTP.", TextWrapping = TextWrapping.Wrap, FontSize = 12 });
        if (worker.ConnectionSetupError is { } error) Children.Add(new TextBlock { Text = error, TextWrapping = TextWrapping.Wrap });
        foreach (var child in new UIElement[] { servers, status, identity, name, transport, command, arguments, endpoint, authentication, bearer, clientId, reviewed, enabled, retain }) Children.Add(child);
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        AddButton(row, "New", "McpNew", () => { servers.SelectedIndex = -1; selected = null; Fill(); return Task.CompletedTask; });
        AddButton(row, "Save", "McpSave", async () =>
        {
            var config = selected?.Kind.StartsWith("cua_") == true ? selected with { Enabled = enabled.IsChecked == true, RetainEvidence = retain.IsChecked == true } : new ConnectionConfig {
                Id = identity.Text.Trim(), Name = name.Text.Trim(), Transport = transport.SelectedItem?.ToString() ?? "stdio", Command = command.Text.Trim(),
                Arguments = JsonSerializer.Deserialize<string[]>(arguments.Text) ?? [], Endpoint = endpoint.Text.Trim(), Authentication = authentication.SelectedItem?.ToString() ?? "none",
                OAuthClientId = clientId.Text.Trim(), Enabled = enabled.IsChecked == true, RetainEvidence = retain.IsChecked == true, ReviewedTools = reviewed.Text.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries) };
            string? credential = string.IsNullOrEmpty(bearer.Password) ? null : bearer.Password;
            await Task.Run(() => broker.SaveAsync(config, credential)); bearer.Password = ""; Refresh(config.Id); status.Text = "Saved. Test / Sign in to discover tools. Configurations restore when EvoFlow restarts.";
        });
        AddButton(row, "Remove", "McpRemove", async () => { if (selected is not null) await Task.Run(() => broker.RemoveAsync(selected.Id)); selected = null; Refresh(); });
        Children.Add(row);
        var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        AddButton(actions, "Test / Sign in", "McpTest", async () => { if (selected is null) throw new ArgumentException("Save a connection first."); await Task.Run(() => broker.ConnectAsync(selected.Id, true, lifetime.Token)); UpdateStatus(); });
        AddButton(actions, "Disconnect", "McpDisconnect", async () => { if (selected is not null) await Task.Run(() => broker.DisconnectAsync(selected.Id)); UpdateStatus(); });
        Children.Add(actions); Children.Add(toolChoice); Children.Add(tools);
        toolChoice.SelectionChanged += (_, _) => ShowTool();
        Children.Add(new TextBlock { Text = "Register only trusted servers: local executables run as your Windows user. Model content cannot register or edit connections. Tools with execution, file, browser or credential authority need a reviewed native adapter and remain disabled on generic connectors. Prompts, resources, sampling and elicitation are not enabled.", TextWrapping = TextWrapping.Wrap, FontSize = 12 });
        servers.SelectionChanged += (_, _) => { if (!refreshing) { selected = broker.Configurations.FirstOrDefault(c => c.Id == servers.SelectedValue?.ToString()); Fill(); } };
        broker.Changed += OnChanged; Unloaded += (_, _) => { broker.Changed -= OnChanged; lifetime.Cancel(); bearer.Password = ""; };
        Refresh();
    }
    void OnChanged() => DispatcherQueue.TryEnqueue(UpdateStatus);
    void Refresh(string? id = null)
    {
        refreshing = true;
        servers.ItemsSource = broker.Configurations; servers.DisplayMemberPath = "Name"; servers.SelectedValuePath = "Id";
        servers.SelectedValue = id ?? selected?.Id ?? broker.Configurations.FirstOrDefault()?.Id;
        selected = broker.Configurations.FirstOrDefault(c => c.Id == servers.SelectedValue?.ToString()); refreshing = false; Fill();
    }
    void Fill()
    {
        identity.Text = selected?.Id ?? ""; name.Text = selected?.Name ?? ""; command.Text = selected?.Command ?? "";
        transport.SelectedItem = selected?.Transport ?? "stdio"; arguments.Text = JsonSerializer.Serialize(selected?.Arguments ?? []); endpoint.Text = selected?.Endpoint ?? "";
        authentication.SelectedItem = selected?.Authentication ?? "none"; clientId.Text = selected?.OAuthClientId ?? ""; reviewed.Text = string.Join(", ", selected?.ReviewedTools ?? []);
        enabled.IsChecked = selected?.Enabled ?? false; retain.IsChecked = selected?.RetainEvidence ?? false; bearer.Password = "";
        bool editable = selected?.Kind.StartsWith("cua_") != true;
        foreach (var control in new Control[] { identity, name, command, transport, arguments, endpoint, authentication, bearer, clientId, reviewed }) { control.IsEnabled = editable; control.Visibility = editable ? Visibility.Visible : Visibility.Collapsed; }
        identity.IsEnabled = selected is null; UpdateStatus();
    }
    void UpdateStatus()
    {
        var state = broker.States.FirstOrDefault(s => s.Id == selected?.Id);
        status.Text = state is null ? "New connection" : $"{state.Name}: {state.Status} · server {state.ServerVersion ?? "unknown"} · {state.Tools.Count} tools · owned PID {state.OwnedPid?.ToString() ?? "none"}\n{state.Error}";
        int previous = toolChoice.SelectedIndex;
        toolChoice.ItemsSource = state?.Tools.Select(t => t.RemoteName + (t.Enabled ? " · enabled" : " · disabled")).ToArray() ?? [];
        toolChoice.SelectedIndex = state is null || state.Tools.Count == 0 ? -1 : Math.Clamp(previous, 0, state.Tools.Count - 1); ShowTool();
    }
    void ShowTool()
    {
        var state = broker.States.FirstOrDefault(s => s.Id == selected?.Id);
        var tool = state is not null && toolChoice.SelectedIndex >= 0 && toolChoice.SelectedIndex < state.Tools.Count ? state.Tools[toolChoice.SelectedIndex] : null;
        tools.Text = tool is null ? "" : $"{tool.RemoteName} · {(tool.Enabled ? "enabled" : "disabled: review/policy required")}\nIdentity: {tool.Name}\nSHA-256: {tool.Hash}\n{tool.Description}\n{tool.Schema.ToJsonString()}";
    }
    void AddButton(StackPanel parent, string text, string id, Func<Task> action)
    {
        var button = Id(new Button { Content = text }, id); parent.Children.Add(button);
        button.Click += async (_, _) => { button.IsEnabled = false; try { await action(); } catch (Exception ex) { status.Text = ex.Message; } finally { button.IsEnabled = true; } };
    }
    static T Id<T>(T value, string id) where T : DependencyObject { AutomationProperties.SetAutomationId(value, id); return value; }
}
