using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using ClaudeSyncBar.Windows.Core;
using Contracts = ClaudeSyncBar.Windows.Core.Tests.TestAuth;

namespace ClaudeSyncBar.Ubuntu.Tests;

/// <summary>
/// Claude Desktop account switching against a stand-in profile directory and
/// stand-in processes below a temporary home. The real app is never involved.
/// </summary>
[Collection("Linux platform isolation")]
public sealed class DesktopSwitchTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "syncbar-desktop-" + Guid.NewGuid().ToString("N"));
    private readonly List<Process> _processes = [];
    private WindowsPaths Paths => new(Path.Combine(_root, "home"), Path.Combine(_root, "data"));
    private string Active => Paths.DesktopUserDataDirectory;
    private string Kept(string account) => Path.Combine(Paths.DesktopLoginsDirectory, account);
    // Where an earlier version kept a whole profile per account.
    private string Parked(string account) => Path.Combine(Paths.DesktopProfilesDirectory, account);
    private string Records(string root, string account, string organization) => Path.Combine(Active, root, account, organization);

    // What the app leaves behind when an account signs in: the account on
    // record, the tokens it obtained with the sign-in and the cookie file.
    private static void SignIn(string directory, string account)
    {
        Directory.CreateDirectory(directory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        File.WriteAllText(Path.Combine(directory, "config.json"),
            "{\"locale\":\"ko\",\"lastKnownAccountUuid\":\"" + account + "\",\"oauth:tokenCache\":\"tokens of " + account
            + "\",\"oauth:tokenCacheV2\":\"newer tokens of " + account + "\"}");
        File.WriteAllText(Path.Combine(directory, "Cookies"), "session of " + account);
    }

    private static string Session(string directory) => File.ReadAllText(Path.Combine(directory, "Cookies"));

    private JsonObject Config() => JsonNode.Parse(File.ReadAllText(Path.Combine(Active, "config.json")))!.AsObject();

    private string? ConfigEntry(string name) => Config()[name]?.GetValue<string>();

    [Fact]
    public void NothingIsCreatedWhenTheDesktopAppWasNeverUsed()
    {
        var accounts = new ClaudeDesktopAccounts(Paths);
        Assert.False(accounts.RequiresSwap("account-a"));
        Assert.Null(accounts.Activate(new("account-a", "org-a")));
        Assert.False(Directory.Exists(Active));
        Assert.False(Directory.Exists(Paths.DesktopLoginsDirectory));
    }

    [Fact]
    public void SwitchingChangesOnlyTheSignInAndTheProfileStaysAsItIs()
    {
        var accounts = new ClaudeDesktopAccounts(Paths);
        SignIn(Active, "account-a");
        File.WriteAllText(Path.Combine(Active, "window-state.json"), "the user's own");
        Directory.CreateDirectory(Path.Combine(Active, "Local Storage"));
        File.WriteAllText(Path.Combine(Active, "Local Storage", "settings"), "theme and extensions");
        Assert.False(accounts.RequiresSwap("account-a"));
        Assert.Null(accounts.Activate(new("account-a", "org-a")));

        // First switch to an account that has never signed in here.
        Assert.True(accounts.RequiresSwap("account-b"));
        Assert.False(accounts.HasLogin("account-b"));
        Assert.NotNull(accounts.Activate(new("account-b", "org-b")));
        Assert.Equal("session of account-a", Session(Kept("account-a")));
        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(Path.Combine(Kept("account-a"), "Cookies")));
        // The app opens signed out and waits for the one manual sign-in.
        Assert.False(File.Exists(Path.Combine(Active, "Cookies")));
        Assert.Equal(DesktopSignIn.Needed, accounts.SignInState());
        Assert.Equal("account-b", accounts.Presumed());
        Assert.False(accounts.RequiresSwap("account-b"));
        // Everything else is the same profile, and the app's own settings file is not edited.
        Assert.Equal("the user's own", File.ReadAllText(Path.Combine(Active, "window-state.json")));
        Assert.Equal("theme and extensions", File.ReadAllText(Path.Combine(Active, "Local Storage", "settings")));
        Assert.Contains("account-a", File.ReadAllText(Path.Combine(Active, "config.json")));

        // The user signs in once; from then on both directions need nothing from them.
        SignIn(Active, "account-b");
        Assert.Equal(DesktopSignIn.Settled, accounts.SignInState());
        Assert.True(accounts.HasLogin("account-a"));
        Assert.NotNull(accounts.Activate(new("account-a", "org-a")));
        Assert.Equal("session of account-a", Session(Active));
        Assert.Equal("session of account-b", Session(Kept("account-b")));
        // The account on record comes back with its tokens.
        Assert.Equal("account-a", ConfigEntry("lastKnownAccountUuid"));
        Assert.Equal("account-a", accounts.Presumed());
        Assert.Equal(DesktopSignIn.Settled, accounts.SignInState());
        Assert.False(accounts.RequiresSwap("account-a"));
        Assert.NotNull(accounts.Activate(new("account-b", "org-b")));
        Assert.Equal("session of account-b", Session(Active));
        Assert.Equal("session of account-a", Session(Kept("account-a")));
        Assert.Equal("the user's own", File.ReadAllText(Path.Combine(Active, "window-state.json")));
        Assert.Equal(new[] { "account-a", "account-b" }, Directory.GetDirectories(Paths.DesktopLoginsDirectory).Select(Path.GetFileName).Order());
    }

    [Fact]
    public void SessionRecordsAreUnderWhicheverAccountIsActive()
    {
        var accounts = new ClaudeDesktopAccounts(Paths, account => account == "account-a" ? "org-a" : null);
        SignIn(Active, "account-a");
        foreach (var root in new[] { "claude-code-sessions", "local-agent-mode-sessions" })
        {
            Directory.CreateDirectory(Records(root, "account-a", "org-a"));
            File.WriteAllText(Path.Combine(Records(root, "account-a", "org-a"), "local_1.json"), "first session");
        }

        // Filed by organization and by path; neither is an account's set of records.
        var skills = Path.Combine(Active, "local-agent-mode-sessions", "skills-plugin", "org-a", "account-a");
        var scratch = Path.Combine(Active, "scratch-workspaces", "account-a", "org-a", "scratch-1");
        Directory.CreateDirectory(skills);
        Directory.CreateDirectory(scratch);

        Assert.NotNull(accounts.Activate(new("account-b", "org-b")));
        foreach (var root in new[] { "claude-code-sessions", "local-agent-mode-sessions" })
        {
            Assert.Equal("first session", File.ReadAllText(Path.Combine(Records(root, "account-b", "org-b"), "local_1.json")));
            Assert.False(Directory.Exists(Records(root, "account-a", "org-a")));
            // A real directory: the app refuses to write through a link.
            Assert.Null(new DirectoryInfo(Records(root, "account-b", "org-b")).LinkTarget);
        }
        Assert.True(Directory.Exists(skills));
        Assert.True(Directory.Exists(scratch));

        // A session started under the second account is there under the third, and under the first again.
        SignIn(Active, "account-b");
        File.WriteAllText(Path.Combine(Records("claude-code-sessions", "account-b", "org-b"), "local_2.json"), "second session");
        Assert.NotNull(accounts.Activate(new("account-c", "org-c")));
        Assert.Equal(new[] { "local_1.json", "local_2.json" },
            Directory.GetFiles(Records("claude-code-sessions", "account-c", "org-c")).Select(Path.GetFileName).Order());
        Assert.NotNull(accounts.Activate(new("account-a", "org-a")));
        Assert.Equal(new[] { "local_1.json", "local_2.json" },
            Directory.GetFiles(Records("claude-code-sessions", "account-a", "org-a")).Select(Path.GetFileName).Order());
        Assert.False(Directory.Exists(Records("claude-code-sessions", "account-c", "org-c")));

        // Records an account already had under its own name join the common ones; nothing is set apart.
        Directory.CreateDirectory(Records("claude-code-sessions", "account-d", "org-d"));
        File.WriteAllText(Path.Combine(Records("claude-code-sessions", "account-d", "org-d"), "own.json"), "its own");
        Assert.NotNull(accounts.Activate(new("account-d", "org-d")));
        Assert.Equal(new[] { "local_1.json", "local_2.json", "own.json" },
            Directory.GetFiles(Records("claude-code-sessions", "account-d", "org-d")).Select(Path.GetFileName).Order());
        Assert.Empty(Directory.GetDirectories(Path.Combine(Active, "claude-code-sessions", "account-d"), "org-d.before-syncbar-*"));

        // Sessions started while signed in to another account by hand come along with the common records.
        SignIn(Active, "account-x");
        Directory.CreateDirectory(Records("claude-code-sessions", "account-x", "org-x"));
        File.WriteAllText(Path.Combine(Records("claude-code-sessions", "account-x", "org-x"), "local_x.json"), "started by hand");
        Assert.NotNull(accounts.Activate(new("account-a", "org-a")));
        Assert.Equal(4, Directory.GetFiles(Records("claude-code-sessions", "account-a", "org-a")).Length);
        Assert.Empty(Directory.GetFiles(Records("claude-code-sessions", "account-x", "org-x")));

        // Without a known organization the records stay put and are found again later.
        Assert.NotNull(accounts.Activate(new("account-e", null)));
        Assert.Equal(4, Directory.GetFiles(Records("claude-code-sessions", "account-a", "org-a")).Length);
        Assert.NotNull(accounts.Activate(new("account-b", "org-b")));
        Assert.Equal(4, Directory.GetFiles(Records("claude-code-sessions", "account-b", "org-b")).Length);
    }

    [Fact]
    public void RecordsOfEveryRegisteredAccountBecomeOneListAndARollbackSeparatesThemAgain()
    {
        var accounts = new ClaudeDesktopAccounts(Paths, knownAccounts: () => ["account-a", "account-b", "account-c"]);
        SignIn(Active, "account-a");
        void Write(string account, string organization, string name, string text)
        {
            Directory.CreateDirectory(Records("claude-code-sessions", account, organization));
            File.WriteAllText(Path.Combine(Records("claude-code-sessions", account, organization), name), text);
        }

        // Signed in to each by hand before SyncBar was used: three separate lists.
        Write("account-a", "org-a", "local_a.json", "a");
        Write("account-a", "org-a", "scheduled-tasks.json", "schedule of a");
        Write("account-b", "org-b", "local_b.json", "b");
        Write("account-b", "org-b", "scheduled-tasks.json", "schedule of b");
        Write("account-c", "org-c", "local_c.json", "c");
        Write("account-c", "org-c", "deleted_old", "tombstone");
        // Another person's account on this PC is none of this.
        Write("account-z", "org-z", "local_z.json", "z");

        var swap = accounts.Activate(new("account-b", "org-b"))!;
        var shared = Records("claude-code-sessions", "account-b", "org-b");
        Assert.Equal(new[] { "deleted_old", "local_a.json", "local_b.json", "local_c.json", "scheduled-tasks.json" },
            Directory.GetFiles(shared).Select(Path.GetFileName).Order());
        // Of two files by one name, the one of the account in use before is the one kept.
        Assert.Equal("schedule of a", File.ReadAllText(Path.Combine(shared, "scheduled-tasks.json")));
        Assert.Equal("schedule of b", File.ReadAllText(Path.Combine(
            Assert.Single(Directory.GetDirectories(Path.Combine(Active, "claude-code-sessions", "account-b"), "org-b.before-syncbar-*")), "scheduled-tasks.json")));
        Assert.True(File.Exists(Records("claude-code-sessions", "account-z", "org-z") + "/local_z.json"));

        accounts.Undo(swap);
        Assert.Equal(new[] { "local_a.json", "scheduled-tasks.json" },
            Directory.GetFiles(Records("claude-code-sessions", "account-a", "org-a")).Select(Path.GetFileName).Order());
        Assert.Equal(new[] { "local_b.json", "scheduled-tasks.json" },
            Directory.GetFiles(Records("claude-code-sessions", "account-b", "org-b")).Select(Path.GetFileName).Order());
        Assert.Equal("schedule of b", File.ReadAllText(Path.Combine(Records("claude-code-sessions", "account-b", "org-b"), "scheduled-tasks.json")));
        Assert.Equal(new[] { "deleted_old", "local_c.json" },
            Directory.GetFiles(Records("claude-code-sessions", "account-c", "org-c")).Select(Path.GetFileName).Order());
        Assert.Empty(Directory.GetDirectories(Path.Combine(Active, "claude-code-sessions", "account-b"), "org-b.before-syncbar-*"));
    }

    [Fact]
    public void PresetsAndConnectorToolChoicesFollowTheSwitch()
    {
        var accounts = new ClaudeDesktopAccounts(Paths);
        SignIn(Active, "account-a");
        Directory.CreateDirectory(Records("claude-code-sessions", "account-a", "org-a"));
        var presets = Path.Combine(Active, "ccd-session-presets.json");
        var toggles = Path.Combine(Active, "mcp-user-tool-toggles.json");
        File.WriteAllText(presets, "{\n\t\"byAccount\": {\n\t\t\"account-a\": {\n\t\t\t\"presets\": [{ \"id\": \"p1\", \"name\": \"리뷰\" }]\n\t\t},\n\t\t\"account-b\": {\n\t\t\t\"presets\": []\n\t\t}\n\t}\n}");
        File.WriteAllText(toggles, "{ \"v\": 3, \"owners\": { \"account-a\": [\"connector:tool\"] } }");
        var originalPresets = File.ReadAllText(presets);
        var originalToggles = File.ReadAllText(toggles);

        var swap = accounts.Activate(new("account-b", "org-b"))!;
        var after = JsonNode.Parse(File.ReadAllText(presets))!;
        Assert.Equal("리뷰", after["byAccount"]!["account-b"]!["presets"]![0]!["name"]!.GetValue<string>());
        Assert.Equal("p1", after["byAccount"]!["account-a"]!["presets"]![0]!["id"]!.GetValue<string>());
        Assert.Contains("\t\t\"account-b\"", File.ReadAllText(presets));
        Assert.Equal("[\"connector:tool\"]", JsonNode.Parse(File.ReadAllText(toggles))!["owners"]!["account-b"]!.ToJsonString());
        Assert.Equal(3, JsonNode.Parse(File.ReadAllText(toggles))!["v"]!.GetValue<int>());

        accounts.Undo(swap);
        Assert.True(JsonNode.DeepEquals(JsonNode.Parse(originalPresets), JsonNode.Parse(File.ReadAllText(presets))));
        Assert.True(JsonNode.DeepEquals(JsonNode.Parse(originalToggles), JsonNode.Parse(File.ReadAllText(toggles))));
    }

    [Fact]
    public void RecordsGoWhereTheAppFiledTheAccountNotWhereItsClaudeCodeLoginSays()
    {
        var accounts = new ClaudeDesktopAccounts(Paths);
        SignIn(Active, "account-a");
        Directory.CreateDirectory(Records("claude-code-sessions", "account-a", "desktop-org"));
        accounts.Activate(new("account-b", "org-b"));
        Assert.True(Directory.Exists(Records("claude-code-sessions", "account-b", "org-b")));
        SignIn(Active, "account-b");

        // The registered login of the first account names another organization than the app used for it.
        accounts.Activate(new("account-a", "claude-code-org"));
        Assert.True(Directory.Exists(Records("claude-code-sessions", "account-a", "desktop-org")));
        Assert.False(Directory.Exists(Records("claude-code-sessions", "account-a", "claude-code-org")));

        // The same holds for a sign-in taken over from a whole profile an earlier version kept.
        SignIn(Parked("account-c"), "account-c");
        Directory.CreateDirectory(Path.Combine(Parked("account-c"), "claude-code-sessions", "account-c", "desktop-org-c"));
        accounts.Activate(new("account-c", "claude-code-org-c"));
        Assert.True(Directory.Exists(Records("claude-code-sessions", "account-c", "desktop-org-c")));
        Assert.Equal("session of account-c", Session(Active));
    }

    [Fact]
    public void ChoicesFiledUnderTheAccountFollowTheSwitchAndNothingElseInTheSettingsChanges()
    {
        var accounts = new ClaudeDesktopAccounts(Paths);
        SignIn(Active, "account-a");
        Directory.CreateDirectory(Records("claude-code-sessions", "account-a", "org-a"));
        var settings = Path.Combine(Active, "claude_desktop_config.json");
        File.WriteAllText(settings, """
            {
              "mcpServers": { "local": { "command": "/usr/bin/tool", "env": { "TOKEN": "kept-as-is" } } },
              "preferences": {
                "keepAwakeEnabled": true,
                "bypassPermissionsOptInByAccount": { "account-a": true },
                "bypassPermissionsGateByAccount": { "account-a": true },
                "epitaxyPrefs": {
                  "epitaxy-folder-permission-mode": {
                    "account-a": { "/home/user/프로젝트": "bypassPermissions" },
                    "account-b": { "/home/user/other": "auto" }
                  },
                  "dframe-code-sections": { "account-a/org-a": { "sections": [1, 2, 3] } },
                  "epitaxy-perm-mode-acks": { "account-a": ["auto", "bypassPermissions"] }
                }
              }
            }
            """);
        var original = File.ReadAllText(settings);

        var swap = accounts.Activate(new("account-b", "org-b"))!;
        var after = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(settings))!;
        var preferences = after["preferences"]!;
        var folders = preferences["epitaxyPrefs"]!["epitaxy-folder-permission-mode"]!;
        // The account in use before decides; the new account's own earlier choices are replaced.
        Assert.Equal(folders["account-a"]!.ToJsonString(), folders["account-b"]!.ToJsonString());
        Assert.True(preferences["bypassPermissionsOptInByAccount"]!["account-b"]!.GetValue<bool>());
        Assert.Equal("[1,2,3]", preferences["epitaxyPrefs"]!["dframe-code-sections"]!["account-b/org-b"]!["sections"]!.ToJsonString());
        Assert.NotNull(preferences["epitaxyPrefs"]!["epitaxy-perm-mode-acks"]!["account-b"]);
        // What the app decides for an account by itself is not copied.
        Assert.Null(preferences["bypassPermissionsGateByAccount"]!["account-b"]);
        // The rest is the same: servers, values, the first account's choices, the path as written.
        Assert.Equal("kept-as-is", after["mcpServers"]!["local"]!["env"]!["TOKEN"]!.GetValue<string>());
        Assert.True(preferences["keepAwakeEnabled"]!.GetValue<bool>());
        Assert.Equal("bypassPermissions", folders["account-a"]!["/home/user/프로젝트"]!.GetValue<string>());
        Assert.Contains("/home/user/프로젝트", File.ReadAllText(settings));
        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(settings));

        // A rollback puts back exactly what the second account had.
        accounts.Undo(swap);
        Assert.True(System.Text.Json.Nodes.JsonNode.DeepEquals(
            System.Text.Json.Nodes.JsonNode.Parse(original), System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(settings))));

        // What is changed while the second account is in use goes back to the first with the next switch.
        accounts.Activate(new("account-b", "org-b"));
        SignIn(Active, "account-b");
        var changed = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(settings))!;
        changed["preferences"]!["epitaxyPrefs"]!["epitaxy-folder-permission-mode"]!["account-b"]!["/home/user/new"] = "plan";
        File.WriteAllText(settings, changed.ToJsonString());
        accounts.Activate(new("account-a", "org-a"));
        Assert.Equal("plan", System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(settings))!
            ["preferences"]!["epitaxyPrefs"]!["epitaxy-folder-permission-mode"]!["account-a"]!["/home/user/new"]!.GetValue<string>());

        // A settings file the app could not read either is left exactly as it is.
        File.WriteAllText(settings, "{ not json");
        accounts.Activate(new("account-c", "org-c"));
        Assert.Equal("{ not json", File.ReadAllText(settings));
    }

    [Fact]
    public void UndoPutsBackTheSignInAndTheRecordsThatWereThere()
    {
        var accounts = new ClaudeDesktopAccounts(Paths);
        SignIn(Active, "account-a");
        Directory.CreateDirectory(Records("claude-code-sessions", "account-a", "org-a"));
        var first = accounts.Activate(new("account-b", "org-b"))!;
        accounts.Undo(first);
        Assert.Equal("session of account-a", Session(Active));
        Assert.True(Directory.Exists(Records("claude-code-sessions", "account-a", "org-a")));
        Assert.False(Directory.Exists(Records("claude-code-sessions", "account-b", "org-b")));
        Assert.Equal("account-a", accounts.Presumed());
        Assert.False(accounts.RequiresSwap("account-a"));

        // The same after the other account has signed in and its sign-in is kept.
        accounts.Activate(new("account-b", "org-b"));
        SignIn(Active, "account-b");
        accounts.Activate(new("account-a", "org-a"));
        var again = accounts.Activate(new("account-b", "org-b"))!;
        Assert.Equal("session of account-b", Session(Active));
        accounts.Undo(again);
        Assert.Equal("session of account-a", Session(Active));
        Assert.Equal("session of account-b", Session(Kept("account-b")));
        Assert.Equal("account-a", accounts.Presumed());
        Assert.True(Directory.Exists(Records("claude-code-sessions", "account-a", "org-a")));
    }

    [Fact]
    public void ProfileNobodySignedInToIsGivenToTheAccountInPlace()
    {
        var accounts = new ClaudeDesktopAccounts(Paths);
        Directory.CreateDirectory(Active);
        File.WriteAllText(Path.Combine(Active, "config.json"), "{\"locale\":\"ko\"}");
        Assert.False(accounts.RequiresSwap("account-a"));
        Assert.Null(accounts.Activate(new("account-a", "org-a")));
        Assert.Equal("account-a", accounts.Presumed());
        Assert.Equal(DesktopSignIn.Needed, accounts.SignInState());
        Assert.False(Directory.Exists(Paths.DesktopLoginsDirectory));
        // It is that account's from now on, signed in yet or not.
        Assert.True(accounts.RequiresSwap("account-b"));
    }

    [Fact]
    public void SignInOfUncertainOwnerNeverReplacesAKeptOne()
    {
        var accounts = new ClaudeDesktopAccounts(Paths);
        SignIn(Active, "account-a");
        accounts.Activate(new("account-b", "org-b"));
        // The user signs back in to the first account by hand instead. On disk
        // that looks the same as not having signed in at all.
        File.WriteAllText(Path.Combine(Active, "Cookies"), "newer session of account-a");
        accounts.Activate(new("account-c", "org-c"));
        Assert.Equal("session of account-a", Session(Kept("account-a")));
        Assert.Equal("newer session of account-a", Session(Kept("unattributed")));
        // Nor do the app's tokens go with a sign-in whose owner is unknown.
        Assert.False(File.Exists(Path.Combine(Kept("unattributed"), "tokens.json")));
        Assert.False(accounts.HasLogin("account-b"));
        Assert.False(accounts.HasLogin("account-c"));

        // Several switches without the app ever starting never file one account's sign-in under another.
        accounts.Activate(new("account-a", "org-a"));
        accounts.Activate(new("account-b", "org-b"));
        accounts.Activate(new("account-a", "org-a"));
        Assert.Equal("session of account-a", Session(Active));
        Assert.Equal("session of account-a", Session(Kept("account-a")));
        Assert.False(accounts.HasLogin("account-b"));
        Assert.Equal(DesktopSignIn.Settled, accounts.SignInState());
    }

    [Fact]
    public void AccountSignedInToByHandKeepsItsOwnSignIn()
    {
        var accounts = new ClaudeDesktopAccounts(Paths);
        SignIn(Active, "account-a");
        accounts.Activate(new("account-b", "org-b"));
        // Not the account the switch was for.
        SignIn(Active, "account-x");
        Assert.Equal(DesktopSignIn.OtherAccount, accounts.SignInState());
        Assert.Equal("account-x", accounts.Presumed());
        Assert.False(accounts.RequiresSwap("account-x"));

        accounts.Activate(new("account-a", "org-a"));
        Assert.Equal("session of account-x", Session(Kept("account-x")));
        Assert.Equal("newer tokens of account-x", Tokens("account-x")["oauth:tokenCacheV2"]!.GetValue<string>());
        Assert.False(accounts.HasLogin("account-b"));
        Assert.Equal("session of account-a", Session(Active));
    }

    private JsonObject Tokens(string account) => JsonNode.Parse(File.ReadAllText(Path.Combine(Kept(account), "tokens.json")))!.AsObject();

    // The app records a sign-in in the config it already has, the way it writes that file.
    private void SignInAgain(string account, string tokens)
    {
        var config = Config();
        config["lastKnownAccountUuid"] = account;
        config["oauth:tokenCache"] = "older " + tokens;
        config["oauth:tokenCacheV2"] = tokens;
        File.WriteAllText(Path.Combine(Active, "config.json"), config.ToJsonString(new JsonSerializerOptions { WriteIndented = true, IndentCharacter = '\t', IndentSize = 1 }));
        File.WriteAllText(Path.Combine(Active, "Cookies"), "session of " + account);
    }

    [Fact]
    public void KeptSignInBringsBackTheAppsOwnTokensAndTheAccountOnRecord()
    {
        var accounts = new ClaudeDesktopAccounts(Paths);
        Directory.CreateDirectory(Active, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        var config = Path.Combine(Active, "config.json");
        File.WriteAllText(config, "{\n\t\"locale\": \"ko-KR\",\n\t\"first_launch_at\": 1791171128080,\n\t\"lastKnownAccountUuid\": \"account-a\",\n"
            + "\t\"oauth:tokenCache\": \"older tokens of account-a\",\n\t\"oauth:tokenCacheV2\": \"tokens of account-a\",\n"
            + "\t\"bootFrameLayout\": {\n\t\t\"collapsed\": false\n\t}\n}");
        File.WriteAllText(Path.Combine(Active, "Cookies"), "session of account-a");
        var original = File.ReadAllText(config);

        // An account that never signed in here: the app is left to see the change of account by itself.
        accounts.Activate(new("account-b", "org-b"));
        Assert.Equal(original, File.ReadAllText(config));
        Assert.Equal("tokens of account-a", Tokens("account-a")["oauth:tokenCacheV2"]!.GetValue<string>());
        Assert.Equal("older tokens of account-a", Tokens("account-a")["oauth:tokenCache"]!.GetValue<string>());
        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(Path.Combine(Kept("account-a"), "tokens.json")));
        Assert.True(accounts.HasTokens("account-a"));
        Assert.False(accounts.HasTokens("account-b"));

        SignInAgain("account-b", "tokens of account-b");
        accounts.Activate(new("account-a", "org-a"));
        Assert.Equal("account-a", ConfigEntry("lastKnownAccountUuid"));
        Assert.Equal("tokens of account-a", ConfigEntry("oauth:tokenCacheV2"));
        Assert.Equal("older tokens of account-a", ConfigEntry("oauth:tokenCache"));
        Assert.Equal("tokens of account-b", Tokens("account-b")["oauth:tokenCacheV2"]!.GetValue<string>());
        // The rest of the app's config is as it was, written the way the app writes it.
        Assert.Equal("ko-KR", ConfigEntry("locale"));
        Assert.Equal(1791171128080, Config()["first_launch_at"]!.GetValue<long>());
        Assert.False(Config()["bootFrameLayout"]!["collapsed"]!.GetValue<bool>());
        Assert.Contains("\n\t\"locale\": \"ko-KR\"", File.ReadAllText(config));
        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(config));
        Assert.Equal("account-a", accounts.Presumed());
        Assert.Equal(DesktopSignIn.Settled, accounts.SignInState());
        Assert.False(accounts.RequiresSwap("account-a"));

        // Tokens the app replaced while the account was in use are the ones kept, and
        // one the app no longer has is not left behind from the other account either.
        var refreshed = Config();
        refreshed["oauth:tokenCacheV2"] = "refreshed tokens of account-a";
        refreshed.Remove("oauth:tokenCache");
        File.WriteAllText(config, refreshed.ToJsonString());
        accounts.Activate(new("account-b", "org-b"));
        Assert.Equal("tokens of account-b", ConfigEntry("oauth:tokenCacheV2"));
        Assert.Equal("account-b", ConfigEntry("lastKnownAccountUuid"));
        Assert.Equal(new[] { "oauth:tokenCacheV2" }, Tokens("account-a").Select(entry => entry.Key));
        accounts.Activate(new("account-a", "org-a"));
        Assert.Equal("refreshed tokens of account-a", ConfigEntry("oauth:tokenCacheV2"));
        Assert.False(Config().ContainsKey("oauth:tokenCache"));

        // A sign-in to another account by hand is still told apart from the one put back.
        SignInAgain("account-x", "tokens of account-x");
        Assert.Equal(DesktopSignIn.OtherAccount, accounts.SignInState());
        Assert.Equal("account-x", accounts.Presumed());

        // A config the app could not read either is left exactly as it is.
        File.WriteAllText(config, "{ not json");
        accounts.Activate(new("account-b", "org-b"));
        Assert.Equal("{ not json", File.ReadAllText(config));
        Assert.Equal("session of account-b", Session(Active));
    }

    [Fact]
    public void SignInKeptWithoutTokensLeavesTheAppsConfigToTheApp()
    {
        var accounts = new ClaudeDesktopAccounts(Paths);
        SignIn(Active, "account-a");
        accounts.Activate(new("account-b", "org-b"));
        SignIn(Active, "account-b");
        accounts.Activate(new("account-a", "org-a"));
        // As an earlier build kept it: the cookie file alone.
        File.Delete(Path.Combine(Kept("account-b"), "tokens.json"));
        Assert.True(accounts.HasLogin("account-b"));
        Assert.False(accounts.HasTokens("account-b"));
        var before = File.ReadAllText(Path.Combine(Active, "config.json"));

        accounts.Activate(new("account-b", "org-b"));
        Assert.Equal("session of account-b", Session(Active));
        Assert.Equal(before, File.ReadAllText(Path.Combine(Active, "config.json")));
        Assert.Equal("account-b", accounts.Presumed());
        Assert.Equal(DesktopSignIn.Settled, accounts.SignInState());

        // Once the user has signed in again, the tokens are kept from then on.
        SignIn(Active, "account-b");
        accounts.Activate(new("account-a", "org-a"));
        Assert.True(accounts.HasTokens("account-b"));
    }

    [Fact]
    public void UndoPutsBackTheAppsConfigAndKeepsTheTokensTheAppReplacedMeanwhile()
    {
        var accounts = new ClaudeDesktopAccounts(Paths);
        SignIn(Active, "account-a");
        accounts.Activate(new("account-b", "org-b"));
        SignIn(Active, "account-b");
        accounts.Activate(new("account-a", "org-a"));
        var before = Config();

        var swap = accounts.Activate(new("account-b", "org-b"))!;
        Assert.Equal("account-b", ConfigEntry("lastKnownAccountUuid"));
        // The app ran with the account and replaced its tokens before another device failed.
        SignInAgain("account-b", "replaced tokens of account-b");
        accounts.Undo(swap);

        Assert.True(JsonNode.DeepEquals(before, Config()));
        Assert.Equal("session of account-a", Session(Active));
        Assert.Equal("account-a", accounts.Presumed());
        Assert.Equal("replaced tokens of account-b", Tokens("account-b")["oauth:tokenCacheV2"]!.GetValue<string>());
        accounts.Activate(new("account-b", "org-b"));
        Assert.Equal("replaced tokens of account-b", ConfigEntry("oauth:tokenCacheV2"));
    }

    [Theory]
    [InlineData("account-b")]
    [InlineData("account-a")]
    public void EarlierProfilePerAccountLayoutBecomesTheOneProfileTheUserHad(string target)
    {
        var accounts = new ClaudeDesktopAccounts(Paths);
        // An earlier version left the user's own profile parked and one it made in use.
        SignIn(Parked("account-a"), "account-a");
        File.WriteAllText(Path.Combine(Parked("account-a"), "window-state.json"), "the user's own");
        SignIn(Active, "account-b");
        File.WriteAllText(Path.Combine(Active, ".claude-syncbar-account"), "account-b\n");
        File.WriteAllText(Path.Combine(Active, "window-state.json"), "made for the second account");

        Assert.True(accounts.HasLogin("account-a"));
        // Even for the account already signed in, the profile itself has to change back.
        Assert.True(accounts.RequiresSwap(target));
        accounts.Activate(new(target, "org"));
        Assert.Equal("the user's own", File.ReadAllText(Path.Combine(Active, "window-state.json")));
        Assert.Equal("session of " + target, Session(Active));
        Assert.Equal(target, accounts.Presumed());
        Assert.True(accounts.HasLogin("account-a"));
        Assert.True(accounts.HasLogin("account-b"));
        // The profile it made is set aside, not deleted.
        var superseded = Assert.Single(Directory.GetDirectories(Paths.DesktopProfilesDirectory, "account-b.superseded-*"));
        Assert.Equal("made for the second account", File.ReadAllText(Path.Combine(superseded, "window-state.json")));
        Assert.False(accounts.RequiresSwap(target));
    }

    [Fact]
    public void SignInKeptInAnEarlierParkedProfileIsUsedWithoutAnotherManualSignIn()
    {
        var accounts = new ClaudeDesktopAccounts(Paths);
        // The user's own profile is in use; the second account signed in to a profile made for it.
        SignIn(Active, "account-a");
        File.WriteAllText(Path.Combine(Active, "window-state.json"), "the user's own");
        SignIn(Parked("account-b"), "account-b");
        File.WriteAllText(Path.Combine(Parked("account-b"), ".claude-syncbar-account"), "account-b\n");

        Assert.True(accounts.HasLogin("account-b"));
        accounts.Activate(new("account-b", "org-b"));
        Assert.Equal("session of account-b", Session(Active));
        Assert.Equal("the user's own", File.ReadAllText(Path.Combine(Active, "window-state.json")));
        Assert.Equal("session of account-a", Session(Kept("account-a")));
        Assert.Equal(DesktopSignIn.Settled, accounts.SignInState());
        Assert.True(Directory.Exists(Parked("account-b")));
    }

    [Fact]
    public void UnsafeLayoutsAndNamesNeverLeaveTheProfileOrTheStore()
    {
        var accounts = new ClaudeDesktopAccounts(Paths);
        SignIn(Active, "account-a");
        Directory.CreateDirectory(Records("claude-code-sessions", "account-a", "org-a"));
        accounts.Activate(new("../outside", "../../elsewhere"));
        // Records are not renamed to a name that is not a plain directory name.
        Assert.True(Directory.Exists(Records("claude-code-sessions", "account-a", "org-a")));
        SignIn(Active, "../outside");
        accounts.Activate(new("account-a", "org-a"));
        // The odd account's sign-in is kept under a digest, inside the store.
        var hashed = Assert.Single(Directory.GetDirectories(Paths.DesktopLoginsDirectory), directory => Path.GetFileName(directory) != "account-a");
        Assert.Matches("^[a-f0-9]{64}$", Path.GetFileName(hashed));
        Assert.Equal("session of ../outside", Session(hashed));
        Assert.False(Directory.Exists(Path.Combine(Path.GetDirectoryName(Paths.DesktopLoginsDirectory)!, "outside")));

        // A record of where the session records are is not followed out of the profile.
        var outside = Path.Combine(_root, "elsewhere");
        Directory.CreateDirectory(Path.Combine(outside, "org"));
        File.WriteAllText(Path.Combine(Active, ".claude-syncbar-login.json"),
            "{\"account\":\"account-a\",\"seen\":\"../outside\",\"sessions\":{\"claude-code-sessions\":\"../../../elsewhere/org\"}}");
        accounts.Activate(new("account-b", "org-b"));
        Assert.True(Directory.Exists(Path.Combine(outside, "org")));
        // What the new account gets is the records found inside the profile.
        Assert.True(Directory.Exists(Records("claude-code-sessions", "account-b", "org-b")));
        Assert.False(Directory.Exists(Records("claude-code-sessions", "account-a", "org-a")));

        // A symbolic link could lead anywhere.
        var moved = Path.Combine(_root, "moved");
        Directory.Move(Active, moved);
        Directory.CreateSymbolicLink(Active, moved);
        var marker = File.ReadAllText(Path.Combine(moved, ".claude-syncbar-login.json"));
        Assert.Throws<ClaudeSyncBarException>(() => accounts.Activate(new("account-c", "org-c")));
        Assert.Equal(marker, File.ReadAllText(Path.Combine(moved, ".claude-syncbar-login.json")));
    }

    [Fact]
    public void LockNamesTheProcessThatHasTheProfileOpen()
    {
        Directory.CreateDirectory(Active);
        Assert.Null(ClaudeDesktopLifecycle.LockHolder(Active));
        var link = Path.Combine(Active, "SingletonLock");
        // Chromium's lock is a dangling link whose name carries host and process.
        File.CreateSymbolicLink(link, System.Net.Dns.GetHostName() + "-4242");
        Assert.Equal(4242, ClaudeDesktopLifecycle.LockHolder(Active));
        File.Delete(link);
        File.CreateSymbolicLink(link, "not-a-lock");
        Assert.Null(ClaudeDesktopLifecycle.LockHolder(Active));
        File.Delete(link);
        File.CreateSymbolicLink(link, "another-host.example-4242");
        Assert.Throws<ClaudeSyncBarException>(() => ClaudeDesktopLifecycle.LockHolder(Active));
    }

    [Fact]
    public async Task OnlyTheInstanceHoldingThisProfileIsStoppedAndThenStartedAgain()
    {
        Directory.CreateDirectory(Active);
        var other = Path.Combine(_root, "other-profile");
        Directory.CreateDirectory(other);
        var main = StartDesktop(Active);
        var unrelated = StartDesktop(other);
        // A renderer that takes a moment to go away after the main process.
        var renderer = StartDesktopChild(Active, seconds: 2);
        var launched = new List<string>();
        using var session = ClaudeDesktopLifecycle.Capture(Paths, (executable, _) =>
        {
            launched.Add(executable);
            StartDesktop(Active);
        });

        var watch = Stopwatch.StartNew();
        await session.StopAsync(default);
        await main.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(renderer.HasExited, "the profile was reported free while a renderer still had it open");
        Assert.True(watch.Elapsed >= TimeSpan.FromSeconds(1), "the stop did not wait for the renderer");
        Assert.False(unrelated.HasExited);
        Assert.Empty(launched);

        File.Delete(Path.Combine(Active, "SingletonLock"));
        await session.StartAsync(default);
        Assert.Equal(DesktopExecutable, Assert.Single(launched));
        // Recovery stops whatever is running now and starts it once more.
        await session.StopAsync(default);
        File.Delete(Path.Combine(Active, "SingletonLock"));
        await session.StartAsync(default);
        Assert.Equal(2, launched.Count);
        Assert.False(unrelated.HasExited);
    }

    [Fact]
    public async Task AppThatWasNotRunningIsNotStartedAndStaleLocksAreNotInstances()
    {
        Directory.CreateDirectory(Active);
        // Left behind by a crash; the process is gone.
        File.CreateSymbolicLink(Path.Combine(Active, "SingletonLock"), System.Net.Dns.GetHostName() + "-99999999");
        using (var session = ClaudeDesktopLifecycle.Capture(Paths, (_, _) => throw new InvalidOperationException("must not launch")))
        {
            await session.StopAsync(default);
            await session.StartAsync(default);
        }

        // The lock's process number now belongs to something that is not Claude Desktop.
        File.Delete(Path.Combine(Active, "SingletonLock"));
        using var bystander = Process.Start(new ProcessStartInfo("/usr/bin/sleep", "30") { UseShellExecute = false })!;
        Thread.Sleep(100);
        _processes.Add(bystander);
        File.CreateSymbolicLink(Path.Combine(Active, "SingletonLock"), System.Net.Dns.GetHostName() + "-" + bystander.Id);
        using (var session = ClaudeDesktopLifecycle.Capture(Paths, (_, _) => throw new InvalidOperationException("must not launch")))
        {
            await session.StopAsync(default);
            await session.StartAsync(default);
        }
        Assert.False(bystander.HasExited);
    }

    [Fact]
    public async Task AccountSwitchChangesTheDesktopSignInBetweenStopAndStart()
    {
        using var fixture = new SwitchFixture(Paths);
        SignIn(Active, "fixture-1");
        File.WriteAllText(Path.Combine(Active, "window-state.json"), "the user's own");
        fixture.Auth.SwitchActive(1);
        Assert.Null(fixture.Service.DescribeDesktopChange(1));
        Assert.Contains("한 번 로그인", fixture.Service.DescribeDesktopChange(2));

        await fixture.Service.SwitchAsync(2);
        Assert.Equal(new[] { "stop:fixture-1:fixture-1", "start:fixture-2:fixture-2" }, fixture.Events);
        Assert.Equal("fixture-2", fixture.Auth.ReadActiveAccountId());
        Assert.Equal("session of fixture-1", Session(Kept("fixture-1")));
        Assert.Equal("the user's own", File.ReadAllText(Path.Combine(Active, "window-state.json")));
        // The app is waiting for this account's one manual sign-in.
        Assert.Contains("로그인해 주세요", fixture.Service.ReconnectionDetail);
        Assert.Contains("그대로 이어집니다", fixture.Service.DescribeDesktopChange(1));
        Assert.DoesNotContain("한 번 로그인", fixture.Service.DescribeDesktopChange(1));
        SignIn(Active, "fixture-2");
        Assert.Null(fixture.Service.ReconnectionDetail);

        // Re-applying the account the app is already signed in to leaves it running.
        fixture.Events.Clear();
        await fixture.Service.SwitchAsync(2);
        Assert.Empty(fixture.Events);

        await fixture.Service.SwitchAsync(1);
        Assert.Equal(new[] { "stop:fixture-2:fixture-2", "start:fixture-1:fixture-1" }, fixture.Events);
        Assert.Equal("session of fixture-1", Session(Active));
        Assert.Equal("session of fixture-2", Session(Kept("fixture-2")));
        Assert.Equal("the user's own", File.ReadAllText(Path.Combine(Active, "window-state.json")));
        Assert.Null(fixture.Service.ReconnectionDetail);

        // A sign-in kept without the app's tokens can still be asked for once more.
        Assert.DoesNotContain("보안을 위해", fixture.Service.DescribeDesktopChange(2));
        File.Delete(Path.Combine(Kept("fixture-2"), "tokens.json"));
        Assert.Contains("보안을 위해", fixture.Service.DescribeDesktopChange(2));
    }

    [Fact]
    public async Task SwitchStartedFromInsideTheDesktopAppIsRefusedBeforeAnythingChanges()
    {
        using var fixture = new SwitchFixture(Paths) { HostedByDesktop = true };
        SignIn(Active, "fixture-1");
        fixture.Auth.SwitchActive(1);
        // The account whose profile is already open needs no restart and stays applicable.
        Assert.Null(fixture.Service.DescribeDesktopBlocker(1));
        await fixture.Service.SwitchAsync(1);

        Assert.Contains("함께 종료", fixture.Service.DescribeDesktopBlocker(2));
        var error = await Assert.ThrowsAsync<ClaudeSyncBarException>(() => fixture.Service.SwitchAsync(2));
        Assert.Contains("함께 종료", error.Message);
        // As one target of a switch, the same wording is carried on as the reason to show.
        var reason = await Assert.ThrowsAsync<LocalSwitchException>(
            () => new WindowsAccountTarget(fixture.Auth, fixture.Service, Paths).ApplyAsync(2, default));
        Assert.Contains("함께 종료", reason.Message);
        Assert.Empty(fixture.Events);
        Assert.Equal("fixture-1", fixture.Auth.ReadActiveAccountId());
        Assert.Equal("session of fixture-1", Session(Active));
        Assert.False(Directory.Exists(Paths.DesktopLoginsDirectory));

        fixture.HostedByDesktop = false;
        Assert.Null(fixture.Service.DescribeDesktopBlocker(2));
        await fixture.Service.SwitchAsync(2);
        Assert.Equal("fixture-2", fixture.Auth.ReadActiveAccountId());
        Assert.Equal("session of fixture-1", Session(Kept("fixture-1")));
    }

    [Fact]
    public async Task ClaudeFolderOpenToTheUsersGroupIsClosedToItInsteadOfRefused()
    {
        using var fixture = new SwitchFixture(Paths);
        // What Claude Code leaves under Ubuntu's default umask of 002, here with more on top.
        const UnixFileMode readable = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute
            | UnixFileMode.GroupRead | UnixFileMode.GroupExecute | UnixFileMode.OtherRead | UnixFileMode.OtherExecute | UnixFileMode.SetGroup;
        File.SetUnixFileMode(Paths.ClaudeHome, readable | UnixFileMode.GroupWrite | UnixFileMode.OtherWrite);

        await fixture.Service.SwitchAsync(2);
        Assert.Equal("fixture-2", fixture.Auth.ReadActiveAccountId());
        // Only writing by others is taken away; who may read it is not this app's to decide.
        Assert.Equal(readable, File.GetUnixFileMode(Paths.ClaudeHome));

        File.SetUnixFileMode(Paths.ClaudeHome, readable | UnixFileMode.GroupWrite);
        await fixture.Service.RestoreAsync(null);
        Assert.Null(fixture.Auth.ReadActiveAccountId());
        Assert.Equal(readable, File.GetUnixFileMode(Paths.ClaudeHome));
    }

    [Fact]
    public async Task UnusableLoginLocationIsReportedBeforeTheDesktopAppIsStopped()
    {
        using var fixture = new SwitchFixture(Paths);
        SignIn(Active, "fixture-1");
        // The login is never written through a link, wherever it leads.
        Directory.Delete(Paths.ClaudeHome, true);
        Directory.CreateSymbolicLink(Paths.ClaudeHome, Path.Combine(_root, "elsewhere"));

        await Assert.ThrowsAsync<ClaudeSyncBarException>(() => fixture.Service.SwitchAsync(2));
        Assert.Empty(fixture.Events);
        Assert.Equal("session of fixture-1", Session(Active));
        Assert.False(Directory.Exists(Paths.DesktopLoginsDirectory));
    }

    [Fact]
    public void RestartedAppRunsDetachedFromThisProcessInTheGivenHome()
    {
        Directory.CreateDirectory(Paths.Home, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        var report = Path.Combine(_root, "launched");
        var app = Path.Combine(_root, "stand-in");
        File.WriteAllText(app, "#!/bin/sh\nout=$(readlink /proc/$$/fd/1)\n"
            + "{ echo \"home=$HOME\"; echo \"session=$(cut -d' ' -f6 /proc/$$/stat)\"; echo \"out=$out\"; } > '" + report + ".tmp'\n"
            + "mv '" + report + ".tmp' '" + report + "'\n");
        File.SetUnixFileMode(app, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);

        ClaudeDesktopLifecycle.Launch(app, Paths);
        var deadline = DateTime.UtcNow.AddSeconds(15);
        while (!File.Exists(report) && DateTime.UtcNow < deadline) Thread.Sleep(50);
        var lines = File.ReadAllLines(report).Select(line => line.Split('=', 2)).ToDictionary(pair => pair[0], pair => pair[1]);

        Assert.Equal(Paths.Home, lines["home"]);
        // Its own session: nothing that happens to this process's terminal or service reaches it.
        Assert.NotEqual(Stat(Environment.ProcessId).Session.ToString(System.Globalization.CultureInfo.InvariantCulture), lines["session"]);
        // Its output goes to the journal or nowhere, never into this process's own output.
        Assert.True(lines["out"] == "/dev/null" || lines["out"].StartsWith("socket:", StringComparison.Ordinal), lines["out"]);
    }

    [Fact]
    public void ProcessStartedInsideTheAppIsRecognisedEvenAfterItsShellIsGone()
    {
        Directory.CreateDirectory(Active);
        // The app's terminal: a shell leading its own session, one command it is
        // still the parent of, and one left running by a shell that has exited.
        var start = new ProcessStartInfo(DesktopExecutable) { UseShellExecute = false, RedirectStandardOutput = true };
        start.ArgumentList.Add("-c");
        start.ArgumentList.Add("/usr/bin/setsid /bin/bash -c '/usr/bin/sleep 60 & echo $!; (/usr/bin/sleep 60 & echo $!); wait 2>/dev/null' & wait");
        using var app = Process.Start(start)!;
        _processes.Add(app);
        var commands = new[] { ReadProcessId(app), ReadProcessId(app) };
        try
        {
            var (attached, orphaned) = (commands[0], commands[1]);
            File.CreateSymbolicLink(Path.Combine(Active, "SingletonLock"), System.Net.Dns.GetHostName() + "-" + app.Id);
            // Once its shell is gone the command's parent is outside the session.
            var deadline = DateTime.UtcNow.AddSeconds(5);
            while (Stat(Stat(orphaned).Parent).Session == Stat(orphaned).Session && DateTime.UtcNow < deadline) Thread.Sleep(50);
            Assert.NotEqual(Stat(orphaned).Session, Stat(Stat(orphaned).Parent).Session);

            Assert.True(LinuxProcess.DescendsFrom(attached, app.Id));
            Assert.True(LinuxProcess.DescendsFrom(orphaned, app.Id));
            Assert.False(LinuxProcess.DescendsFrom(99999999, app.Id));
            // The test started the app, not the other way around.
            Assert.False(LinuxProcess.DescendsFrom(Environment.ProcessId, app.Id));
            Assert.False(ClaudeDesktopLifecycle.HostsThisProcess(Active));
        }
        finally
        {
            foreach (var command in commands)
                try { using var process = Process.GetProcessById(command); process.Kill(); }
                catch (ArgumentException) { }
        }
    }

    [Fact]
    public void RestartedAppGetsTheLoginSessionAndNothingOfTheUnitThatReportedIt()
    {
        var environment = ClaudeDesktopLifecycle.ParseEnvironmentBlock(string.Join('\0',
            "WAYLAND_DISPLAY=wayland-0", "XMODIFIERS=@im=ibus", "SSH_AUTH_SOCK=/run/user/1000/keyring/ssh",
            "QT_IM_MODULES=wayland;ibus", "EMPTY=", "MULTI=first\nsecond=third", "=nameless", "novalue",
            "INVOCATION_ID=0123", "SYSTEMD_EXEC_PID=42", "MANAGERPID=7", "MANAGERPIDFDID=8", "MEMORY_PRESSURE_WATCH=/x",
            "JOURNAL_STREAM=8:1", "PWD=/home/someone", "SHLVL=1") + "\0");
        Assert.Equal(new[] { "EMPTY", "MULTI", "QT_IM_MODULES", "SSH_AUTH_SOCK", "WAYLAND_DISPLAY", "XMODIFIERS" },
            environment.Keys.Order(StringComparer.Ordinal));
        Assert.Equal("@im=ibus", environment["XMODIFIERS"]);
        Assert.Equal("first\nsecond=third", environment["MULTI"]);
        Assert.Equal("", environment["EMPTY"]);

        // Whatever the source, the app is started as this user in this home.
        var session = ClaudeDesktopLifecycle.SessionEnvironment(Paths);
        Assert.Equal(Paths.Home, session["HOME"]);
        Assert.Equal(Environment.UserName, session["USER"]);
        Assert.DoesNotContain(session.Keys, key => key is "INVOCATION_ID" or "SYSTEMD_EXEC_PID");
    }

    private static int ReadProcessId(Process process) =>
        int.Parse(process.StandardOutput.ReadLine()!, System.Globalization.CultureInfo.InvariantCulture);

    private static (int Parent, int Session) Stat(int id)
    {
        var stat = File.ReadAllText("/proc/" + id + "/stat");
        var fields = stat[(stat.LastIndexOf(')') + 2)..].Split(' ');
        return (int.Parse(fields[1], System.Globalization.CultureInfo.InvariantCulture),
            int.Parse(fields[3], System.Globalization.CultureInfo.InvariantCulture));
    }

    [Fact]
    public async Task FailedRestartPutsBackBothTheLoginAndTheDesktopSignIn()
    {
        using var fixture = new SwitchFixture(Paths) { FailNextStart = true };
        SignIn(Active, "fixture-1");
        fixture.Auth.SwitchActive(1);

        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Service.SwitchAsync(2));
        Assert.Equal(new[]
        {
            "stop:fixture-1:fixture-1", "start:fixture-2:fixture-2", "stop:fixture-2:fixture-2", "start:fixture-1:fixture-1",
        }, fixture.Events);
        Assert.Equal("fixture-1", fixture.Auth.ReadActiveAccountId());
        Assert.Equal("session of fixture-1", Session(Active));
    }

    [Fact]
    public async Task RollingBackASwitchRestoresTheVerySignInItReplaced()
    {
        using var fixture = new SwitchFixture(Paths);
        // The desktop app is signed in to an account this app does not manage.
        SignIn(Active, "unmanaged-account");
        fixture.Auth.SwitchActive(1);
        var previous = fixture.Auth.ReadActiveAuth();

        await fixture.Service.SwitchAsync(2);
        Assert.Equal("fixture-2", new ClaudeDesktopAccounts(Paths).Presumed());
        // Another device failed, so the coordinator restores this PC.
        fixture.Events.Clear();
        await fixture.Service.RestoreAsync(previous);
        Assert.Equal(new[] { "stop:fixture-2:fixture-2", "start:fixture-1:unmanaged-account" }, fixture.Events);
        Assert.Equal("fixture-1", fixture.Auth.ReadActiveAccountId());
        Assert.Equal("session of unmanaged-account", Session(Active));
    }

    // Waits without a child process, so the stand-in is one process like the real main process.
    private const string Wait = "f=$(mktemp -u); mkfifo \"$f\"; exec 3<>\"$f\"; rm -f \"$f\"; read -t \"$1\" -u 3";

    // Process inspection goes by the executable's file name, so the stand-in is a shell under that name.
    private string DesktopExecutable
    {
        get
        {
            var executable = Path.Combine(_root, "bin", "claude-desktop");
            if (!File.Exists(executable))
            {
                Directory.CreateDirectory(Path.GetDirectoryName(executable)!);
                File.Copy("/bin/bash", executable);
                File.SetUnixFileMode(executable, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            }

            return executable;
        }
    }

    // A main process: it holds the profile's lock the way Chromium does.
    private Process StartDesktop(string profile)
    {
        var start = new ProcessStartInfo(DesktopExecutable) { UseShellExecute = false };
        foreach (var argument in new[] { "-c", Wait, "fixture", "60" }) start.ArgumentList.Add(argument);
        var process = Process.Start(start)!;
        _processes.Add(process);
        var link = Path.Combine(profile, "SingletonLock");
        File.Delete(link);
        File.CreateSymbolicLink(link, System.Net.Dns.GetHostName() + "-" + process.Id);
        Thread.Sleep(150);
        return process;
    }

    // Electron flattens a child's arguments into its process title.
    private Process StartDesktopChild(string profile, int seconds)
    {
        var start = new ProcessStartInfo("/bin/bash") { UseShellExecute = false };
        foreach (var argument in new[]
        {
            "-c", "exec -a \"$1 --type=renderer --user-data-dir=$2 --lang=ko\" \"$1\" -c \"$4\" fixture \"$3\"",
            "fixture", DesktopExecutable, profile, seconds.ToString(System.Globalization.CultureInfo.InvariantCulture), Wait,
        }) start.ArgumentList.Add(argument);
        var process = Process.Start(start)!;
        _processes.Add(process);
        Thread.Sleep(150);
        return process;
    }

    public void Dispose()
    {
        foreach (var process in _processes)
        {
            try { if (!process.HasExited) { process.Kill(); process.WaitForExit(5000); } }
            catch (InvalidOperationException) { }
            process.Dispose();
        }

        if (Directory.Exists(_root)) Directory.Delete(_root, true);
    }

    private sealed class SwitchFixture : IDisposable
    {
        private readonly IDisposable _key = LinuxSecretKeyProvider.OverrideForTests(() => Enumerable.Repeat((byte)23, 32).ToArray());
        public AuthStore Auth { get; }
        public LocalSwitchService Service { get; }
        public List<string> Events { get; } = [];
        public bool FailNextStart { get; set; }
        public bool HostedByDesktop { get; set; }

        public SwitchFixture(WindowsPaths paths)
        {
            paths.EnsureDirectories();
            Auth = new(paths);
            for (var id = 1; id <= 2; id++)
            {
                var file = Path.Combine(paths.LoginSessionsDirectory, id + ".json");
                WindowsPathSafety.WritePrivateBytes(file, Contracts.Bytes(Contracts.Create("fixture-" + id, "access-" + id, "refresh-" + id)));
                Auth.ImportAuth(file, id);
            }

            Service = new LocalSwitchService(Auth, paths, () => new RecordingDesktop(this, paths), () => HostedByDesktop);
        }

        public void Dispose() => _key.Dispose();

        // Records, at each stop and start, which login and which desktop sign-in are in place.
        private sealed class RecordingDesktop(SwitchFixture owner, WindowsPaths paths) : IClaudeDesktopSession
        {
            private string State(string step) => step + ":" + owner.Auth.ReadActiveAccountId() + ":"
                + new ClaudeDesktopAccounts(paths).Presumed();
            public Task StopAsync(CancellationToken cancellationToken) { owner.Events.Add(State("stop")); return Task.CompletedTask; }
            public Task StartAsync(CancellationToken cancellationToken)
            {
                owner.Events.Add(State("start"));
                if (owner.FailNextStart) { owner.FailNextStart = false; throw new InvalidOperationException("Fixture restart failure"); }
                return Task.CompletedTask;
            }
            public void Dispose() { }
        }
    }
}
