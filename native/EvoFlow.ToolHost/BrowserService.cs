using Microsoft.Playwright;
using System.Net;
using System.Text.Json.Nodes;

namespace EvoFlow.ToolHost;

public sealed class BrowserService(string home, string workspace) : IAsyncDisposable
{
    IPlaywright? playwright;
    IBrowserContext? context;
    IPage? page;
    readonly Dictionary<string, IElementHandle> controls = [];
    public static void Validate(JsonObject a, PathPolicy paths)
    {
        string[] fields = ["operation", "url", "reference", "value", "key", "path"];
        if (a.Any(p => !fields.Contains(p.Key))) throw new ArgumentException("Unknown browser argument.");
        var op = LocalTools.Text(a, "operation");
        if (!new[] { "navigate", "inspect", "click", "fill", "press", "screenshot", "back", "close" }.Contains(op)) throw new ArgumentException("Unknown browser operation.");
        if (op == "navigate" && !PublicUrl(LocalTools.Text(a, "url"))) throw new ArgumentException("Browser navigation requires a public HTTP(S) URL without credentials. Private/loopback addresses and file/javascript URLs are unavailable.");
        if (op is "click" or "fill" && !System.Text.RegularExpressions.Regex.IsMatch(LocalTools.Text(a, "reference"), "^e[0-9]{1,3}$")) throw new ArgumentException("Use an element reference from the most recent browser inspection.");
        if (op == "fill" && LocalTools.Text(a, "value").Length > 8000) throw new ArgumentException("Browser input exceeds limit.");
        if (op == "press" && !new[] { "Enter", "Tab", "Escape", "ArrowDown", "ArrowUp", "ArrowLeft", "ArrowRight" }.Contains(LocalTools.Text(a, "key"))) throw new ArgumentException("Unsupported browser key.");
        if (op == "screenshot")
        {
            var path = paths.Normalize(LocalTools.Text(a, "path"), true);
            if (!PathPolicy.Within(path, paths.Workspace) || !path.EndsWith(".png", StringComparison.OrdinalIgnoreCase) || File.Exists(path)) throw new ArgumentException("Use a new PNG path in the workspace.");
        }
    }
    public static bool PublicUrl(string input)
    {
        if (!Uri.TryCreate(input, UriKind.Absolute, out var uri) || uri.Scheme is not ("https" or "http") || uri.UserInfo.Length > 0 || uri.IsLoopback || uri.Host.EndsWith(".localhost") || uri.Host.EndsWith(".local")) return false;
        try
        {
            return Dns.GetHostAddresses(uri.DnsSafeHost).All(ip =>
            {
                if (IPAddress.IsLoopback(ip) || ip.IsIPv6LinkLocal || ip.IsIPv6SiteLocal) return false;
                if (ip.IsIPv4MappedToIPv6) ip = ip.MapToIPv4();
                var b = ip.GetAddressBytes();
                if (b.Length == 16) return (b[0] & 0xFE) != 0xFC && ip != IPAddress.IPv6Any;
                return b[0] is not (0 or 10 or 127 or 169) && !(b[0] == 172 && b[1] is >= 16 and <= 31) && !(b[0] == 192 && b[1] == 168) && b[0] < 224;
            });
        }
        catch (System.Net.Sockets.SocketException) { return false; }
    }
    async Task<IPage> ReadyAsync()
    {
        if (page is not null && !page.IsClosed) return page;
        playwright ??= await Playwright.CreateAsync();
        context = await playwright.Chromium.LaunchPersistentContextAsync(Path.Combine(home, "BrowserProfile"), new() { ExecutablePath = ApplicationLauncher.Resolve("chrome"), Headless = false, AcceptDownloads = false, Timeout = 30000 });
        await context.RouteAsync("**/*", async route =>
        {
            if (PublicUrl(route.Request.Url)) await route.ContinueAsync(); else await route.AbortAsync();
        });
        page = context.Pages.FirstOrDefault() ?? await context.NewPageAsync(); page.SetDefaultTimeout(10000);
        return page;
    }
    public async Task<ToolResult> ExecuteAsync(JsonObject a)
    {
        Validate(a, new PathPolicy(workspace, home));
        string op = LocalTools.Text(a, "operation");
        if (op == "close") { await DisposeAsync(); return Wire.Success("Owned browser session closed."); }
        var current = await ReadyAsync();
        switch (op)
        {
            case "navigate": await current.GotoAsync(LocalTools.Text(a, "url"), new() { WaitUntil = WaitUntilState.DOMContentLoaded, Timeout = 25000 }); controls.Clear(); break;
            case "back": await current.GoBackAsync(new() { WaitUntil = WaitUntilState.DOMContentLoaded, Timeout = 25000 }); controls.Clear(); break;
            case "click":
            case "fill":
                if (!controls.TryGetValue(LocalTools.Text(a, "reference"), out var element)) return Wire.Failure("stale_reference", "Inspect the browser first; references expire after navigation or a click.");
                if ((await element.GetAttributeAsync("type"))?.Equals("password", StringComparison.OrdinalIgnoreCase) == true) return Wire.Failure("protected_control", "Enter passwords manually in the owned browser.");
                if (op == "fill")
                {
                    var value = LocalTools.Text(a, "value"); await element.FillAsync(value);
                    if (await element.InputValueAsync() != value) return Wire.Failure("verification_failed", "Browser value did not match readback.");
                }
                else { await element.ClickAsync(); controls.Clear(); }
                break;
            case "press": await current.Keyboard.PressAsync(LocalTools.Text(a, "key")); controls.Clear(); break;
            case "screenshot":
                var path = new PathPolicy(workspace, home).Normalize(LocalTools.Text(a, "path"), true);
                await current.ScreenshotAsync(new() { Path = path, FullPage = false });
                return Wire.Success("Browser screenshot saved and verified.", new() { ["path"] = path, ["bytes"] = new FileInfo(path).Length });
        }
        var observation = await InspectAsync(current);
        return Wire.Success(op == "inspect" ? "Browser state inspected." : $"Browser {op} performed; resulting page state returned. Confirm task outcome from this observation.", observation);
    }
    async Task<JsonObject> InspectAsync(IPage current)
    {
        controls.Clear(); var elements = await current.QuerySelectorAllAsync("a,button,input,textarea,select,[role=button]");
        var rows = new JsonArray();
        foreach (var element in elements.Take(80))
        {
            if (!await element.IsVisibleAsync()) continue;
            string id = "e" + (controls.Count + 1); controls[id] = element;
            var type = await element.GetAttributeAsync("type");
            string label = await element.GetAttributeAsync("aria-label") ?? await element.GetAttributeAsync("placeholder") ?? await element.InnerTextAsync();
            rows.Add(new JsonObject { ["reference"] = id, ["label"] = label[..Math.Min(label.Length, 200)], ["type"] = type, ["protected"] = type == "password" });
        }
        string text = await current.Locator("body").InnerTextAsync();
        return new() { ["url"] = current.Url, ["title"] = await current.TitleAsync(), ["text"] = text[..Math.Min(text.Length, 12000)], ["elements"] = rows };
    }
    public async ValueTask DisposeAsync()
    {
        controls.Clear(); page = null;
        if (context is not null) { await context.CloseAsync(); context = null; }
        playwright?.Dispose(); playwright = null;
    }
}
