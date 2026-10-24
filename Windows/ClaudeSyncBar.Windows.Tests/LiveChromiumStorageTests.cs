using System.Diagnostics;
using System.Net;
using System.Text.Json.Nodes;
using ClaudeSyncBar.Windows.Core;
using Xunit.Abstractions;

namespace ClaudeSyncBar.Windows.Tests;

/// <summary>Runs only when SYNCBAR_LIVE_CHROMIUM names a Chromium browser to run headless (Edge, Chrome).</summary>
public sealed class LiveChromiumFactAttribute : FactAttribute
{
    public LiveChromiumFactAttribute()
    {
        if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable("SYNCBAR_LIVE_CHROMIUM")))
            Skip = "실제 Chromium 브라우저(SYNCBAR_LIVE_CHROMIUM)가 지정되지 않았습니다.";
    }
}

/// <summary>
/// What SyncBar writes to Local Storage, read back by Chromium itself: a page in a
/// headless browser with a throwaway profile stores entries, SyncBar copies one
/// over another, removes one and adds one larger than a log block while the
/// browser is closed, and the page reads them all back. Then once more, now over
/// the tables Chromium wrote when it took up SyncBar's log. A database Chromium
/// could not open would come back empty, which the checks would show.
/// </summary>
public sealed class LiveChromiumStorageTests(ITestOutputHelper output) : IDisposable
{
    private const string Origin = "file://";
    private readonly string _root = Path.Combine(Path.GetTempPath(), "syncbar-live-chromium-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    [LiveChromiumFact]
    public async Task ChromiumReadsWhatSyncBarWroteToItsLocalStorage()
    {
        var profile = Path.Combine(_root, "profile");
        var storage = new ChromiumLocalStorage(Path.Combine(profile, "Default", "Local Storage", "leveldb"));
        var presetsOfA = "{\"state\":{\"presets\":[{\"name\":\"컴퓨터 관리\",\"cwd\":\"C:\\\\Users\\\\user\\\\프로젝트\"}]},\"version\":4}";
        await Page(profile, "set", $$"""
            localStorage.clear();
            localStorage.setItem('persisted.ccd-session-presets.account-a', {{Js(presetsOfA)}});
            localStorage.setItem('persisted.ccd-session-presets.account-b', 'old of b');
            localStorage.setItem('unrelated', 'removed by SyncBar');
            localStorage.setItem('untouched', 'x'.repeat(100000) + '끝');
            document.body.textContent = 'set';
            """);
        Assert.True(storage.Exists);
        var stored = storage.ReadAll(Origin);
        Assert.Equal(presetsOfA, LevelDbFixtureText(stored["persisted.ccd-session-presets.account-a"]));

        var big = Enumerable.Range(0, 30000).Select(index => (char)('가' + index % 2000)).Aggregate(new System.Text.StringBuilder(), (text, c) => text.Append(c)).ToString();
        storage.Update(Origin, ["persisted.ccd-session-presets.account-a", "persisted.ccd-session-presets.account-b"], current =>
        [
            ("persisted.ccd-session-presets.account-b", current["persisted.ccd-session-presets.account-a"]),
            ("unrelated", null),
            ("big", Value(big)),
        ]);

        var seen = await Read(profile);
        Assert.Equal(presetsOfA, seen["persisted.ccd-session-presets.account-b"]);
        Assert.Equal(presetsOfA, seen["persisted.ccd-session-presets.account-a"]);
        Assert.False(seen.ContainsKey("unrelated"));
        Assert.Equal("len:100001:끝", seen["untouched"]);
        Assert.Equal($"len:{big.Length}:{big[^1]}", seen["big"]);

        // Chromium has taken up the log by now; the second write goes over what it made of it.
        var files = Directory.GetFiles(storage.Location).Select(path => Path.GetFileName(path)).Order().ToArray();
        output.WriteLine("before the second write: " + string.Join(", ", files));
        Assert.Contains(files, file => file.EndsWith(".ldb", StringComparison.Ordinal));
        storage.Update(Origin, ["persisted.ccd-session-presets.account-b"], current =>
            [("persisted.ccd-session-presets.account-a", Value("{\"edited\":true}")), ("big", null)]);
        seen = await Read(profile);
        Assert.Equal("{\"edited\":true}", seen["persisted.ccd-session-presets.account-a"]);
        Assert.Equal(presetsOfA, seen["persisted.ccd-session-presets.account-b"]);
        Assert.False(seen.ContainsKey("big"));
        Assert.Equal("len:100001:끝", seen["untouched"]);
        var log = File.ReadAllText(Path.Combine(storage.Location, "LOG"));
        output.WriteLine(log);
        Assert.DoesNotContain("Corruption", log, StringComparison.OrdinalIgnoreCase);
    }

    private static string LevelDbFixtureText(byte[] value) => value[0] == 0
        ? System.Text.Encoding.Unicode.GetString(value, 1, value.Length - 1)
        : System.Text.Encoding.Latin1.GetString(value, 1, value.Length - 1);

    private static byte[] Value(string text) => text.All(character => character <= '\u00FF')
        ? [1, .. System.Text.Encoding.Latin1.GetBytes(text)]
        : [0, .. System.Text.Encoding.Unicode.GetBytes(text)];

    private static string Js(string text) => System.Text.Json.JsonSerializer.Serialize(text);

    private async Task<Dictionary<string, string>> Read(string profile)
    {
        var dom = await Page(profile, "read", """
            const out = {};
            for (let i = 0; i < localStorage.length; i++) {
              const key = localStorage.key(i), value = localStorage.getItem(key);
              out[key] = value.length > 1000 ? 'len:' + value.length + ':' + value.slice(-1) : value;
            }
            document.body.textContent = JSON.stringify(out);
            """);
        var body = dom[(dom.IndexOf("<body>", StringComparison.Ordinal) + 6)..dom.LastIndexOf("</body>", StringComparison.Ordinal)];
        return JsonNode.Parse(WebUtility.HtmlDecode(body))!.AsObject().ToDictionary(entry => entry.Key, entry => entry.Value!.GetValue<string>());
    }

    // Opens the page in the headless browser, which stores what the page wrote
    // to Local Storage when it closes, and returns the page as it ended.
    private async Task<string> Page(string profile, string name, string script)
    {
        Directory.CreateDirectory(_root);
        var page = Path.Combine(_root, name + ".html");
        await File.WriteAllTextAsync(page, $"<!doctype html><meta charset=\"utf-8\"><body><script>{script}</script></body>");
        var start = new ProcessStartInfo(Environment.GetEnvironmentVariable("SYNCBAR_LIVE_CHROMIUM")!)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            StandardOutputEncoding = System.Text.Encoding.UTF8,
        };
        foreach (var argument in new[]
                 {
                     "--headless=new", "--user-data-dir=" + profile, "--no-first-run", "--no-default-browser-check", "--disable-gpu",
                     "--disable-extensions", "--disable-sync", "--disable-background-networking", "--dump-dom", new Uri(page).AbsoluteUri,
                 })
            start.ArgumentList.Add(argument);
        using var browser = Process.Start(start)!;
        var dom = browser.StandardOutput.ReadToEndAsync();
        var errors = browser.StandardError.ReadToEndAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(90));
        try
        {
            await browser.WaitForExitAsync(timeout.Token);
        }
        catch (OperationCanceledException)
        {
            browser.Kill(entireProcessTree: true);
            throw;
        }

        // The browser's helper processes may still be closing the profile.
        for (var attempt = 0; attempt < 50 && IsLocked(Path.Combine(profile, "Default", "Local Storage", "leveldb", "LOCK")); attempt++)
            await Task.Delay(200);
        output.WriteLine(await errors);
        return await dom;
    }

    private static bool IsLocked(string path)
    {
        if (!File.Exists(path)) return false;
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite | FileShare.Delete);
            stream.Lock(0, 1);
            stream.Unlock(0, 1);
            return false;
        }
        catch (IOException)
        {
            return true;
        }
    }
}
