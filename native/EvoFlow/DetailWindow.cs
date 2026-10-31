using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using System.Text.Json.Nodes;
using Windows.Graphics;

namespace EvoFlow;

public sealed class DetailWindow : Window
{
    public DetailWindow(JsonNode? run, bool compare = false)
    {
        Title = "EvoFlow · Results & evidence"; AppWindow.Resize(new SizeInt32(1100, 760));
        AppWindow.SetIcon(Path.Combine(AppContext.BaseDirectory, "Assets", "AppIcon.ico"));
        var grid = new Grid { Padding = new Thickness(24), RowSpacing = 16 }; grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        bool conversation = run?["assistant_response"] is not null || run?["run_kind"]?.ToString() == "conversation";
        var heading = new StackPanel { Spacing = 8 };
        heading.Children.Add(new TextBlock { Text = conversation ? "Conversation response & execution" : "Reconciliation results", FontSize = 28 });
        if (run?["data_origin"]?.ToString() == "fictional-demo")
        {
            bool legacy = run["session_id"] is null;
            var originNotice = new InfoBar
            {
                Title = legacy ? "Archived fictional demonstration" : "Fictional sample demonstration",
                Message = legacy ? "This historical task used fictional sample data. It is preserved as an execution result, not as a conversational AI response." : "This explicit demonstration used fictional sample data.",
                IsOpen = true, IsClosable = false, Severity = InfoBarSeverity.Informational
            };
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetAutomationId(originNotice, "DataOriginNotice");
            heading.Children.Add(originNotice);
        }
        if (run?["task"] is JsonNode originalTask)
            heading.Children.Add(new TextBlock { Text = "Submitted task: " + originalTask.ToString(), TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true, MaxHeight = 100 });
        grid.Children.Add(heading);
        var tabs = new TabView(); Grid.SetRow(tabs, 1); grid.Children.Add(tabs);
        var results = new ListView { SelectionMode = ListViewSelectionMode.None };
        results.Header = MakeRow(new[] { "Order", "Warehouse", "Ordered", "Shipped", "Remaining", "Delayed", "Supported cause" }, true);
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetAutomationId(results, "ResultRows");
        if (run?["result"] is JsonArray rows) foreach (var r in rows) results.Items.Add(MakeRow(new[] { r!["order_id"]!.ToString(), r["warehouse"]!.ToString(), r["ordered"]!.ToString(), r["shipped"]!.ToString(), r["remaining"]!.ToString(), r["delayed"]!.ToString(), r["cause"]!.ToString() }, false));
        if (!conversation) tabs.TabItems.Add(new TabViewItem { Header = "Orders", IsClosable = false, Content = results });
        var report = new TextBox { Text = run?["assistant_response"]?.ToString() ?? run?["report"]?.ToString() ?? run?["error"]?.ToString() ?? "Run a task to see a response.", IsReadOnly = true, AcceptsReturn = true, TextWrapping = TextWrapping.Wrap }; Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(report, conversation ? "Assistant response" : "Readable reconciliation report");
        tabs.TabItems.Add(new TabViewItem { Header = conversation ? "Response" : "Report", IsClosable = false, Content = report });
        var routing = new TextBox { Text = RoutingSummary(run), IsReadOnly = true, AcceptsReturn = true, TextWrapping = TextWrapping.Wrap };
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetAutomationId(routing, "RoutingDetails");
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(routing, "Model and thinking effort decisions");
        tabs.TabItems.Add(new TabViewItem { Header = "Model & effort", IsClosable = false, Content = routing });
        var evidence = new TextBox { Text = run?.ToJsonString(new System.Text.Json.JsonSerializerOptions { WriteIndented = true }) ?? "No execution yet.", IsReadOnly = true, AcceptsReturn = true, TextWrapping = TextWrapping.Wrap }; Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(evidence, "Structured execution evidence");
        tabs.TabItems.Add(new TabViewItem { Header = "Evidence", IsClosable = false, Content = evidence });
        var benchmarkPath = Path.Combine(AppContext.BaseDirectory, "evidence", "benchmark.json");
        if (File.Exists(benchmarkPath))
        {
            var benchmark = JsonNode.Parse(File.ReadAllText(benchmarkPath))!;
            var comparison = new Grid { RowSpacing = 16 }; comparison.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); comparison.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            comparison.Children.Add(new TextBlock { Text = "Historical fixed-model experiment before adaptive effort routing · 8 tasks per condition, acquisition included.\nFree versus free is not dollar savings. Tokens measure inference demand, not hardware compute.", TextWrapping = TextWrapping.Wrap, Margin = new Thickness(12) });
            var table = new ListView { SelectionMode = ListViewSelectionMode.None }; Grid.SetRow(table, 1); comparison.Children.Add(table);
            table.Header = MakeRow(new[] { "Condition", "Correct", "Final", "Calls", "Input", "Output", "Cost USD" }, true);
            foreach (var entry in benchmark["conditions"]!.AsObject())
            {
                var t = entry.Value!["totals"]!; table.Items.Add(MakeRow(new[] { entry.Key, $"{t["successes"]}/{t["tasks"]}", $"{t["final_successes"]}/{t["final_tasks"]}", t["model_calls"]!.ToString(), t["input_tokens"]!.ToString(), t["output_tokens"]!.ToString(), t["billed_cost_usd"]!.ToString() }, false));
            }
            tabs.TabItems.Add(new TabViewItem { Header = "Benchmark", IsClosable = false, Content = comparison });
            if (compare) tabs.SelectedItem = tabs.TabItems.Last();
        }
        Content = grid;
    }
    private static string RoutingSummary(JsonNode? run)
    {
        if (run?["routing"]?["decisions"] is not JsonArray decisions)
            return "This older execution has no model/effort routing record. See Evidence for the settings and usage it actually recorded.";
        var lines = new List<string> { $"Policy: {run["routing"]!["policy"]} · {run["routing"]!["policy_version"]}", "Provider-reported tokens do not prove a particular thinking effort was applied.", "" };
        foreach (var decision in decisions)
        {
            if (decision?["effort_status"]?.ToString() == "no_inference") { lines.Add("Verified workflow reuse: no model or thinking effort requested."); continue; }
            lines.Add($"Profile: {decision?["profile"]} — {decision?["reason"]}");
            lines.Add($"Requested model: {decision?["requested_model"] ?? JsonValue.Create("unknown")}");
            lines.Add($"Provider model: {decision?["effective_model"] ?? JsonValue.Create("unconfirmed")}");
            lines.Add($"Requested effort: {decision?["requested_effort"] ?? JsonValue.Create("none requested")}");
            lines.Add($"Submitted effort: {decision?["submitted_effort"] ?? JsonValue.Create("not submitted")}");
            if (decision?["decision_model"] is not null) lines.Add($"Decision model: {decision["decision_model"]}");
            if (decision?["native_reasoning"] is not null) lines.Add($"Native reasoning parameters: {decision["native_reasoning"]!.ToJsonString()}");
            lines.Add($"Effective effort: {decision?["effective_effort"] ?? JsonValue.Create("unknown")} · {decision?["effort_status"]}");
            lines.Add($"Capability check: {decision?["capability_reason"]}");
            lines.Add($"Provider: {decision?["provider"] ?? JsonValue.Create("unconfirmed")} · Request: {decision?["request_id"] ?? JsonValue.Create("not available")}");
            lines.Add("");
        }
        lines.Add("Per-request attempts, retries, reported usage and any missing accounting are preserved in Evidence.");
        return string.Join("\n", lines);
    }
    private static Grid MakeRow(string[] values, bool header)
    {
        var grid = new Grid { ColumnSpacing = 10, Padding = new Thickness(8), HorizontalAlignment = HorizontalAlignment.Stretch };
        double[] widths = { 170, 110, 70, 70, 85, 65 };
        foreach (double width in widths) grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(width) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        for (int i = 0; i < values.Length; i++) { var text = new TextBlock { Text = values[i], TextWrapping = TextWrapping.Wrap, FontSize = header ? 14 : 13, IsTextSelectionEnabled = !header }; Grid.SetColumn(text, i); grid.Children.Add(text); }
        return grid;
    }
}
