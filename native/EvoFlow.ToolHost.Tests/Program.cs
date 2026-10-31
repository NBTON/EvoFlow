using System.Diagnostics;
using EvoFlow.ToolHost;

if (args.Length == 2 && args[0] == "--verify-app")
{
    Console.WriteLine((await ApplicationLauncher.VerifyAsync(args[1], CancellationToken.None)).ToJsonString()); return;
}
if (args.Length == 2 && args[0] == "--search-app") { Console.WriteLine(InstalledApplications.Catalog(args[1]).ToJsonString()); return; }
if (args.Length == 1 && args[0] == "--ui-smoke")
{
    var ready = new TaskCompletionSource<System.Windows.Forms.Form>();
    var thread = new Thread(() =>
    {
        var form = new System.Windows.Forms.Form { Text = "EvoFlow accessibility verification", Width = 360, Height = 180 };
        var label = new System.Windows.Forms.Label { Text = "Waiting", Top = 60, Width = 200 };
        var button = new System.Windows.Forms.Button { Text = "Test action", Width = 150 };
        button.Click += (_, _) => label.Text = "Action observed";
        form.Controls.Add(button); form.Controls.Add(label); form.Shown += (_, _) => ready.SetResult(form);
        System.Windows.Forms.Application.Run(form);
    });
    thread.SetApartmentState(ApartmentState.STA); thread.Start();
    var form = await ready.Task.WaitAsync(TimeSpan.FromSeconds(10));
    try
    {
        string handle = form.Handle.ToInt64().ToString();
        var observed = WindowsTools.Execute(new() { ["operation"] = "inspect", ["windowHandle"] = handle }, CancellationToken.None);
        var button = observed.Data["controls"]!.AsArray().First(c => c?["name"]?.ToString() == "Test action")!;
        var invoked = WindowsTools.Execute(new() { ["operation"] = "invoke", ["windowHandle"] = handle, ["controlPath"] = button["controlPath"]!.ToString() }, CancellationToken.None);
        if (invoked.Status != "succeeded") throw new Exception("General application control invocation failed");
        var after = WindowsTools.Execute(new() { ["operation"] = "inspect", ["windowHandle"] = handle }, CancellationToken.None);
        if (!after.Data.ToJsonString().Contains("Action observed")) throw new Exception("Independent UI state verification failed");
        Console.WriteLine("PASS: FlaUI inspected and invoked a control in a general application outside the former allowlist; subsequent inspection confirmed the effect.");
    }
    finally { form.BeginInvoke(() => form.Close()); thread.Join(TimeSpan.FromSeconds(5)); }
    return;
}
if (args.Length == 1 && args[0] == "--browser-smoke")
{
    string browserRoot = Path.Combine(Path.GetTempPath(), "EvoFlowBrowser-" + Guid.NewGuid().ToString("N"));
    string workspace = Path.Combine(browserRoot, "files"); Directory.CreateDirectory(workspace);
    await using var browser = new BrowserService(Path.Combine(browserRoot, "state"), workspace);
    var result = await browser.ExecuteAsync(new() { ["operation"] = "navigate", ["url"] = "https://example.com" });
    if (!result.Verified || result.Data["title"]!.ToString() != "Example Domain") throw new Exception("Browser navigation or inspection failed");
    var clicked = await browser.ExecuteAsync(new() { ["operation"] = "click", ["reference"] = "e1" });
    if (!clicked.Verified || !clicked.Data["url"]!.ToString().Contains("iana.org")) throw new Exception("Browser click failed");
    if (BrowserService.PublicUrl("http://127.0.0.1") || BrowserService.PublicUrl("file:///C:/Windows") || BrowserService.PublicUrl("http://192.168.1.1")) throw new Exception("Browser URL policy bypass");
    Console.WriteLine("PASS: Playwright navigation, visible DOM inspection, reference-based link click, private/file URL denial."); return;
}

if (args.Length > 0 && args[0] == "--wait") { await Task.Delay(TimeSpan.FromMinutes(5)); return; }
if (args.Length > 0 && args[0] == "--output") { Console.Write(new string('a', 60000)); Console.Error.Write(new string('e', 60000)); Environment.ExitCode = 7; return; }
if (args.Length > 0 && args[0] == "--tree")
{
    using var child = Process.Start(new ProcessStartInfo(Environment.ProcessPath!) { ArgumentList = { "--wait" }, UseShellExecute = false, CreateNoWindow = true })!;
    await File.WriteAllTextAsync(args[1], child.Id.ToString());
    await Task.Delay(TimeSpan.FromMinutes(5)); return;
}
void Assert(bool condition, string message) { if (!condition) throw new Exception(message); }
var root = Path.Combine(Path.GetTempPath(), "EvoFlow-tools-tests-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(root);
var exe = Environment.ProcessPath!;
using (var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10)))
{
    var output = await ProcessRunner.RunAsync(ProcessRunner.StartInfo(exe, ["--output"], root), null, timeout.Token);
    Assert(output.ExitCode == 7 && output.Truncated && output.Stdout.Length == 16000 && output.Stderr.Length == 16000, "Bounded output and nonzero exit code");
}
var pidFile = Path.Combine(root, "child.pid");
using (var cancel = new CancellationTokenSource())
{
    var work = ProcessRunner.RunAsync(ProcessRunner.StartInfo(exe, ["--tree", pidFile], root), null, cancel.Token);
    for (int i = 0; !File.Exists(pidFile) && i < 100; i++) await Task.Delay(50);
    Assert(File.Exists(pidFile), "Child process must start before cancellation");
    int child = int.Parse(await File.ReadAllTextAsync(pidFile));
    cancel.Cancel();
    try { await work; throw new Exception("Cancellation was ignored"); } catch (OperationCanceledException) { }
    await Task.Delay(300);
    bool alive; try { using var p = Process.GetProcessById(child); alive = !p.HasExited; } catch (ArgumentException) { alive = false; }
    Assert(!alive, "Cancellation must terminate the grandchild process");
}
var paths = new PathPolicy(root, Path.Combine(root, "protected"));
foreach (var host in new[] { "powershell", "cmd.exe", "python3.exe", "node", "rundll32.exe" })
    Assert(ExecutionPolicy.ScriptHost(host), "Shell/script host denial");
try { ExecutionPolicy.ValidateArguments("git.exe", ["-c", "alias.run=!powershell"]); throw new Exception("Inline execution allowed"); } catch (ArgumentException) { }
var knownWord = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Microsoft Office", "root", "Office16", "WINWORD.EXE");
if (File.Exists(knownWord)) Assert(ApplicationLauncher.Resolve("Word").Equals(knownWord, StringComparison.OrdinalIgnoreCase), "Word must resolve beyond the legacy four-app list");
Assert(paths.Normalize("sub/../file.txt") == Path.Combine(root, "file.txt"), "Path normalization");
foreach (var path in new[] { @"\\server\share\a.txt", @"\\?\C:\a.txt", "a.txt:secret", ".ssh/id_rsa", ".agents/skills/x.txt", "NUL.txt" })
{
    try { paths.Normalize(path); throw new Exception("Unsafe path accepted: " + path); } catch (ArgumentException) { }
}
Console.WriteLine("PASS: bounded stdout/stderr, nonzero exits, cancellation kills process trees, path normalization, credential/device/stream path denial.");
