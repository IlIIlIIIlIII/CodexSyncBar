// Windows port of Ubuntu/ClaudeSyncBar.Ubuntu.Platform/ClaudeDesktopAccounts.cs. Keep the two in step;
// they differ only in where Chromium keeps cookies and in directory creation.
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Encodings.Web;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace ClaudeSyncBar.Windows.Core;

/// <summary>An account as Claude Desktop names it on disk.</summary>
internal sealed record DesktopAccount(string Account, string? Organization);

/// <summary>A directory one switch renamed, and what was already in its place.</summary>
internal sealed record DesktopSessionMove(string From, string To, string? Aside);

/// <summary>One record one switch moved into the shared records, and the directory it came from.</summary>
internal sealed record DesktopSessionMerge(string From, string To, string Name);

/// <summary>
/// A setting one switch overwrote, by its path in the file, and its earlier
/// value. File names the file when it is not the app's settings file.
/// </summary>
internal sealed record DesktopPreferenceChange(IReadOnlyList<string> Path, string? Previous, string? File = null);

/// <summary>An entry of the app's Local Storage one switch wrote, what it held before, and what was written (null: none).</summary>
internal sealed record DesktopStorageChange(string Name, byte[]? Previous, byte[]? Written);

/// <summary>What one switch changed in the profile, so that exactly that can be put back.</summary>
/// <param name="Tokens">The entries of the app's own config.json the switch overwrote, by name.</param>
/// <param name="Stored">The entries of the app's Local Storage the switch wrote.</param>
/// <param name="PresetsMerged">Whether this switch was the one that brought the presets of every account together.</param>
internal sealed record DesktopLoginSwap(
    string TargetAccount, string? PreviousMarker, string? PreviousLogin, IReadOnlyList<DesktopSessionMove> Moved,
    IReadOnlyList<DesktopSessionMerge> Merged, IReadOnlyList<DesktopPreferenceChange> Preferences, IReadOnlyList<DesktopPreferenceChange> Tokens,
    IReadOnlyList<DesktopStorageChange>? Stored = null, bool PresetsMerged = false);

internal enum DesktopSignIn { Settled, Needed, OtherAccount }

/// <summary>
/// Claude Desktop keeps one profile directory, and everything in it is the
/// user's own - sessions, settings, extensions - except the sign-in, which is
/// one cookie file and the tokens the app obtained with it. Switching accounts
/// therefore exchanges only those and leaves the profile where it is; the
/// sign-in of each account that is not in use is kept aside. The app files
/// its session records under the signed-in account, so those directories are
/// renamed to the new account, and records the app filed under any other
/// account the user works with join them: the same sessions are there
/// whichever account is active. Choices the app files under the account, such
/// as permission modes and session presets, are carried over to the new
/// account the same way. The app must not be running while any of this happens.
/// </summary>
/// <param name="knownAccounts">The accounts registered in SyncBar, whose records are the user's own.</param>
internal sealed class ClaudeDesktopAccounts(WindowsPaths paths, Func<string, string?>? organizationOf = null,
    Func<IEnumerable<string>>? knownAccounts = null)
{
    private const string MarkerFile = ".claude-syncbar-login.json";
    private const string EarlierMarkerFile = ".claude-syncbar-account";
    private const string Unattributed = "unattributed";
    // Chromium on Windows keeps the cookie database in the Network folder of the profile.
    private static readonly string CookieFile = Path.Combine("Network", "Cookies");
    private const string OrganizationFile = "organization";
    private const string SettingsFile = "claude_desktop_config.json";
    private const string ConfigFile = "config.json";
    private const string TokenFile = "tokens.json";
    // The app's record of the account it last saw signed in.
    private const string OwnerEntry = "lastKnownAccountUuid";
    // Encrypted by the app with a key from the user's keyring; copied as they are, never read.
    private static readonly string[] TokenEntries = ["oauth:tokenCache", "oauth:tokenCacheV2"];
    private static readonly JsonSerializerOptions SettingsFormat = new()
    {
        WriteIndented = true,
        // Folder paths in it stay readable as the app wrote them.
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };
    // How the app itself writes config.json.
    private static readonly JsonSerializerOptions ConfigFormat = new(SettingsFormat) { IndentCharacter = '\t', IndentSize = 1 };
    // Other files in which the app keeps choices by account: the presets for
    // new sessions and which tools of each connector are turned off.
    private const string PresetsFile = "ccd-session-presets.json";
    private static readonly (string Name, JsonSerializerOptions Format)[] ChoiceFiles =
        [(PresetsFile, ConfigFormat), ("mcp-user-tool-toggles.json", SettingsFormat)];
    // The app's page keeps the presets for new sessions in its Local Storage too,
    // one entry per account. The presets file is read only the first time an
    // account is used; from then on the page writes its entry over the file.
    private const string StorageOrigin = "https://claude.ai";
    private const string PresetStorage = "persisted.ccd-session-presets.";
    private static readonly string[] StoredChoices = [PresetStorage];
    // How the app's page writes the values it keeps there.
    private static readonly JsonSerializerOptions StoredFormat = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
    private const long MaximumCookieBytes = 64 * 1024 * 1024;
    private const long MaximumSettingsBytes = 4 * 1024 * 1024;
    private static readonly string[] LoginFiles = [CookieFile, Path.Combine("Network", "Cookies-journal")];
    // Filed by the app under account/organization. A link from one account's
    // directory to another's is no use: the app refuses to write through it.
    private static readonly string[] SessionRoots = ["claude-code-sessions", "local-agent-mode-sessions"];
    private static readonly Regex Name = new("^[A-Za-z0-9][A-Za-z0-9-]{0,63}$", RegexOptions.CultureInvariant);

    // Seen: the account the app had on record at the moment of the switch. A
    // different one on record later means the app has seen a sign-in since.
    private sealed record Marker(string Account, string? Seen, IReadOnlyDictionary<string, string> Sessions);

    public string ActiveDirectory => paths.DesktopUserDataDirectory;
    private string MarkerPath => Path.Combine(ActiveDirectory, MarkerFile);
    private ChromiumLocalStorage LocalStorage => new(Path.Combine(ActiveDirectory, "Local Storage", "leveldb"));
    // Present once the presets of every registered account have been brought together.
    private string PresetsMergedPath => Path.Combine(paths.DesktopLoginsDirectory, ".presets-merged");

    /// <summary>The account the app last saw signed in to this profile. It records that unencrypted.</summary>
    public static string? SignedInOwner(string directory)
    {
        try
        {
            var config = new FileInfo(Path.Combine(directory, ConfigFile));
            if (!config.Exists || config.Length > MaximumSettingsBytes) return null;
            using var document = JsonDocument.Parse(File.ReadAllBytes(config.FullName));
            return document.RootElement.ValueKind == JsonValueKind.Object
                && document.RootElement.TryGetProperty(OwnerEntry, out var owner)
                && owner.ValueKind == JsonValueKind.String
                && !string.IsNullOrWhiteSpace(owner.GetString())
                ? owner.GetString()
                : null;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException)
        {
            return null;
        }
    }

    /// <summary>The account this profile is set up for, signed in yet or not.</summary>
    public string? Presumed()
    {
        if (!Directory.Exists(ActiveDirectory)) return null;
        var known = SignedInOwner(ActiveDirectory);
        if (ReadMarker() is not { } marker) return known;
        // Another account on record since the switch: the user signed in to that one by hand.
        return known is not null && known != marker.Account && known != marker.Seen ? known : marker.Account;
    }

    // The account the cookie file in place can be kept under. What the app had
    // on record before the last switch says nothing about the file put there since.
    private string? Attributable()
    {
        var known = SignedInOwner(ActiveDirectory);
        if (ReadMarker() is not { } marker) return known;
        return known is not null && (known == marker.Account || known != marker.Seen) ? known : null;
    }

    /// <summary>Whether a sign-in of the account is kept, so that switching to it needs no manual sign-in.</summary>
    public bool HasLogin(string account) => HasKeptLogin(account) || EarlierProfiles().Any(profile => SignedInOwner(profile) == account
        && File.Exists(Path.Combine(profile, CookieFile)));

    /// <summary>
    /// Whether the app's own tokens are kept with the account's sign-in. Without
    /// them the app has to obtain new ones, and the server can ask for a fresh sign-in for that.
    /// </summary>
    public bool HasTokens(string account) => HasKeptLogin(account)
        ? KeptTokens(Slot(account)) is not null
        : EarlierProfiles().Any(profile => SignedInOwner(profile) == account && File.Exists(Path.Combine(profile, CookieFile))
            && TokensFiledUnder(profile, account) is not null);

    /// <summary>True when switching to the account changes files the running app has open.</summary>
    public bool RequiresSwap(string account)
    {
        if (!Directory.Exists(ActiveDirectory)) return false;
        if (OriginalToBringBack() is not null) return true;
        var presumed = Presumed();
        if (presumed == account) return false;
        // A profile nobody has signed in to is simply given to this account.
        return presumed is not null || HasLogin(account);
    }

    /// <summary>What the user still has to do in the app after the last switch.</summary>
    public DesktopSignIn SignInState()
    {
        if (!Directory.Exists(ActiveDirectory) || ReadMarker() is not { } marker) return DesktopSignIn.Settled;
        var known = SignedInOwner(ActiveDirectory);
        if (known == marker.Account) return DesktopSignIn.Settled;
        if (known is not null && known != marker.Seen) return DesktopSignIn.OtherAccount;
        // A kept sign-in was put in place; the app takes it up when it next starts.
        return HasKeptLogin(marker.Account) ? DesktopSignIn.Settled : DesktopSignIn.Needed;
    }

    /// <summary>
    /// Makes the account the one Claude Desktop is signed in to. Returns null
    /// when nothing had to change, including when the app has never been used.
    /// </summary>
    public DesktopLoginSwap? Activate(DesktopAccount target)
    {
        if (new DirectoryInfo(ActiveDirectory).LinkTarget is not null)
            throw new ClaudeSyncBarException("Claude 데스크톱 데이터 폴더가 심볼릭 링크여서 계정을 바꿀 수 없습니다.");
        if (!Directory.Exists(ActiveDirectory)) return null;
        if (!RequiresSwap(target.Account))
        {
            if (Presumed() is null) WriteMarker(new(target.Account, SignedInOwner(ActiveDirectory), new Dictionary<string, string>()));
            return null;
        }

        WindowsPathSafety.EnsurePrivateDirectory(paths.DesktopLoginsDirectory, "Claude 데스크톱 로그인 보관 디렉터리");
        AdoptEarlierLayout();
        var previous = Presumed();
        // The profile brought back from the earlier layout can already be this account's.
        if (previous == target.Account) return null;

        var previousMarker = File.Exists(MarkerPath) ? File.ReadAllText(MarkerPath) : null;
        var previousOrganization = SessionOrganization(previous);
        var previousLogin = KeepCurrentLogin();
        var moved = new List<DesktopSessionMove>();
        var merged = new List<DesktopSessionMerge>();
        var preferences = new List<DesktopPreferenceChange>();
        var tokens = new List<DesktopPreferenceChange>();
        var stored = new List<DesktopStorageChange>();
        var presetsMerged = false;
        try
        {
            if (HasKeptLogin(target.Account))
            {
                PlaceLogin(Slot(target.Account));
                PlaceTokens(target.Account, tokens);
            }
            else
            {
                PlaceLogin(null);
            }

            WriteMarker(new(target.Account, SignedInOwner(ActiveDirectory), MoveSessions(previous, target, moved, merged)));
            if (previous is not null)
            {
                // What the presets file held for each account before the mirror below rewrote it.
                var presetsBefore = ReadJsonObject(Path.Combine(ActiveDirectory, PresetsFile))?["byAccount"]?.DeepClone() as JsonObject;
                MirrorPreferences(previous, previousOrganization, target.Account, KeptOrganization(target.Account) ?? target.Organization, preferences);
                presetsMerged = CarryStoredChoices(previous, target.Account, presetsBefore, stored);
            }

            return new(target.Account, previousMarker, previousLogin, moved, merged, preferences, tokens, stored, presetsMerged);
        }
        catch
        {
            Restore(previousMarker, previousLogin, moved, merged, preferences, tokens, stored, presetsMerged);
            throw;
        }
    }

    public void Undo(DesktopLoginSwap swap)
    {
        if (!Directory.Exists(ActiveDirectory)) return;
        // The app may have run with the account since and replaced its tokens;
        // the ones kept from before would no longer be accepted.
        if (File.Exists(Path.Combine(ActiveDirectory, CookieFile)) && Attributable() == swap.TargetAccount)
            KeepLogin(ActiveDirectory, swap.TargetAccount);
        Restore(swap.PreviousMarker, swap.PreviousLogin, swap.Moved, swap.Merged, swap.Preferences, swap.Tokens, swap.Stored ?? [], swap.PresetsMerged);
    }

    private void Restore(string? marker, string? login, IReadOnlyList<DesktopSessionMove> moved, IReadOnlyList<DesktopSessionMerge> merged,
        IReadOnlyList<DesktopPreferenceChange> preferences, IReadOnlyList<DesktopPreferenceChange> tokens, IReadOnlyList<DesktopStorageChange> stored, bool presetsMerged)
    {
        RestoreStoredChoices(stored);
        // The presets are brought together again with a later switch.
        if (presetsMerged) File.Delete(PresetsMergedPath);
        RestoreTokens(tokens);
        RestorePreferences(preferences);
        foreach (var merge in merged.Reverse())
        {
            var record = Path.Combine(merge.To, merge.Name);
            var back = Path.Combine(merge.From, merge.Name);
            if (File.Exists(back) || Directory.Exists(back)) continue;
            Directory.CreateDirectory(merge.From);
            if (Directory.Exists(record)) Directory.Move(record, back);
            else if (File.Exists(record)) File.Move(record, back);
        }

        foreach (var move in moved.Reverse())
        {
            if (Directory.Exists(move.To) && !Directory.Exists(move.From)) Directory.Move(move.To, move.From);
            if (move.Aside is null || Directory.Exists(move.To)) continue;
            if (Directory.Exists(move.Aside)) Directory.Move(move.Aside, move.To);
            // Emptied into the shared set, it was removed.
            else Directory.CreateDirectory(move.To);
        }

        PlaceLogin(login);
        if (marker is null) File.Delete(MarkerPath);
        else WritePrivate(MarkerPath, Encoding.UTF8.GetBytes(marker));
    }

    // Returns where the cookie file now in place is kept, or null when there is none.
    private string? KeepCurrentLogin()
    {
        if (!File.Exists(Path.Combine(ActiveDirectory, CookieFile))) return null;
        var owner = Attributable();
        if (owner is null)
        {
            // Untouched since it was put there: the kept copy already is this file.
            if (ReadMarker() is { } marker && SameLogin(ActiveDirectory, Slot(marker.Account))) return Slot(marker.Account);
            // Whose sign-in this is cannot be told, so it replaces nobody's.
            owner = Unattributed;
        }

        KeepLogin(ActiveDirectory, owner);
        return Slot(owner);
    }

    private void KeepLogin(string from, string owner)
    {
        WindowsPathSafety.EnsurePrivateDirectory(Slot(owner), "Claude 데스크톱 로그인 보관 디렉터리");
        CopyLogin(from, Slot(owner));
        // Where the app files this account's records is what it used, which
        // need not be the organization a Claude Code login of the account names.
        if (OrganizationFiledUnder(from, owner) is { } organization)
            WritePrivate(Path.Combine(Slot(owner), OrganizationFile), Encoding.UTF8.GetBytes(organization + "\n"));
        // Tokens filed under another account, or kept from an earlier sign-in, do not go with this cookie file.
        var tokens = Path.Combine(Slot(owner), TokenFile);
        if (TokensFiledUnder(from, owner) is { } kept) WritePrivate(tokens, Encoding.UTF8.GetBytes(kept.ToJsonString()));
        else File.Delete(tokens);
    }

    // The app's tokens in a profile, when what it has on record there is that account.
    private static JsonObject? TokensFiledUnder(string profile, string account)
    {
        if (SignedInOwner(profile) != account || ReadJsonObject(Path.Combine(profile, ConfigFile)) is not { } config) return null;
        var tokens = new JsonObject();
        foreach (var entry in TokenEntries)
            if (config.TryGetPropertyValue(entry, out var value)) tokens[entry] = value?.DeepClone();
        return tokens.Count == 0 ? null : tokens;
    }

    private static JsonObject? KeptTokens(string slot) =>
        ReadJsonObject(Path.Combine(slot, TokenFile)) is { Count: > 0 } tokens && tokens.All(entry => TokenEntries.Contains(entry.Key))
            ? tokens
            : null;

    // The app keeps the tokens it obtained in its config, for the account it
    // last saw. Put back together with that record, they are used as they are.
    // Otherwise the app takes the account as changed, discards them and has to
    // obtain new ones with the cookie alone, which the server refuses for a
    // sign-in it considers too old, so the user would have to sign in again.
    private void PlaceTokens(string account, List<DesktopPreferenceChange> changes)
    {
        var path = Path.Combine(ActiveDirectory, ConfigFile);
        if (KeptTokens(Slot(account)) is not { } tokens || ReadJsonObject(path) is not { } config) return;
        foreach (var entry in TokenEntries.Append(OwnerEntry))
        {
            var value = entry == OwnerEntry ? JsonValue.Create(account) : tokens[entry]?.DeepClone();
            var replaced = entry == OwnerEntry || tokens.ContainsKey(entry);
            var previous = config.TryGetPropertyValue(entry, out var current) ? current?.ToJsonString() ?? "null" : null;
            if (previous == (replaced ? value?.ToJsonString() ?? "null" : null)) continue;
            changes.Add(new([entry], previous));
            if (replaced) config[entry] = value;
            else config.Remove(entry);
        }

        if (changes.Count > 0) WritePrivate(path, Encoding.UTF8.GetBytes(config.ToJsonString(ConfigFormat)));
    }

    private static string? OrganizationFiledUnder(string profile, string account)
    {
        if (!Name.IsMatch(account)) return null;
        foreach (var root in SessionRoots)
        {
            var organizations = Organizations(Path.Combine(profile, root, account));
            if (organizations.Length == 1) return Path.GetFileName(organizations[0]);
        }

        return null;
    }

    private string? KeptOrganization(string account)
    {
        try
        {
            var file = new FileInfo(Path.Combine(Slot(account), OrganizationFile));
            if (!file.Exists || file.LinkTarget is not null || file.Length > 256) return null;
            var organization = File.ReadAllText(file.FullName).Trim();
            return Name.IsMatch(organization) ? organization : null;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static string[] Organizations(string accountDirectory) => IsPlainDirectory(accountDirectory)
        ? Directory.GetDirectories(accountDirectory).Where(directory => IsPlainDirectory(directory) && Name.IsMatch(Path.GetFileName(directory))).ToArray()
        : [];

    // Without a kept sign-in the app opens signed out and the user signs in once.
    private void PlaceLogin(string? from)
    {
        if (from is not null && File.Exists(Path.Combine(from, CookieFile)))
        {
            CopyLogin(from, ActiveDirectory);
            return;
        }

        foreach (var name in LoginFiles) File.Delete(Path.Combine(ActiveDirectory, name));
    }

    private static void CopyLogin(string from, string to)
    {
        foreach (var name in LoginFiles)
        {
            var source = new FileInfo(Path.Combine(from, name));
            var destination = Path.Combine(to, name);
            if (!source.Exists)
            {
                File.Delete(destination);
                continue;
            }

            if (source.LinkTarget is not null || source.Length > MaximumCookieBytes)
                throw new ClaudeSyncBarException("Claude 데스크톱 로그인 파일이 안전하지 않습니다.");
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            WritePrivate(destination, File.ReadAllBytes(source.FullName));
        }
    }

    private static bool SameLogin(string first, string second)
    {
        var one = new FileInfo(Path.Combine(first, CookieFile));
        var other = new FileInfo(Path.Combine(second, CookieFile));
        return one.Exists && other.Exists && one.Length == other.Length && one.Length <= MaximumCookieBytes
            && File.ReadAllBytes(one.FullName).AsSpan().SequenceEqual(File.ReadAllBytes(other.FullName));
    }

    // Renames each set of session records from where it is to the directory
    // the app reads for the target account, gathers there the records filed
    // under the user's other accounts, and returns where each set now is.
    private Dictionary<string, string> MoveSessions(string? previous, DesktopAccount target, List<DesktopSessionMove> moved,
        List<DesktopSessionMerge> merged)
    {
        var marker = ReadMarker();
        var locations = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var root in SessionRoots)
        {
            var from = marker is not null && marker.Sessions.TryGetValue(root, out var kept) ? SessionDirectory(root, kept) : FindSessions(root, previous);
            var rootDirectory = Path.Combine(ActiveDirectory, root);
            var organization = KeptOrganization(target.Account) ?? target.Organization;
            if (organization is null || !Name.IsMatch(target.Account) || !Name.IsMatch(organization))
            {
                if (from is not null) locations[root] = Path.GetRelativePath(rootDirectory, from).Replace('\\', '/');
                continue;
            }

            var to = Path.Combine(rootDirectory, target.Account, organization);
            var others = OtherRecords(root, previous, target.Account).Where(directory => directory != to && directory != from).ToList();
            // With no shared set yet, the first set found becomes it.
            if (from is null && !Directory.Exists(to) && others.Count > 0)
            {
                from = others[0];
                others.RemoveAt(0);
            }

            if (from is not null && from != to)
            {
                // Records the account already had there join the shared ones.
                string? aside = null;
                if (Directory.Exists(to))
                {
                    aside = to + ".before-syncbar-" + DateTime.UtcNow.Ticks.ToString(CultureInfo.InvariantCulture);
                    Directory.Move(to, aside);
                }

                Directory.CreateDirectory(Path.GetDirectoryName(to)!);
                Directory.Move(from, to);
                moved.Add(new(from, to, aside));
                if (aside is not null)
                {
                    Gather(aside, to, merged);
                    // Nothing is left there unless a record by the same name was already shared.
                    if (!Directory.EnumerateFileSystemEntries(aside).Any()) Directory.Delete(aside);
                }
            }

            foreach (var other in others) Gather(other, to, merged);
            if (Directory.Exists(to)) locations[root] = Path.GetRelativePath(rootDirectory, to).Replace('\\', '/');
        }

        return locations;
    }

    // Where else the app filed session records of the accounts the user works
    // with: the one signed in before the switch, by hand or not, every account
    // registered here, and the target's own under another organization.
    private IEnumerable<string> OtherRecords(string root, string? previous, string target)
    {
        var accounts = new SortedSet<string>(StringComparer.Ordinal) { target };
        if (previous is not null) accounts.Add(previous);
        foreach (var account in knownAccounts?.Invoke() ?? []) accounts.Add(account);
        return accounts.Where(account => Name.IsMatch(account)).SelectMany(account => Organizations(Path.Combine(ActiveDirectory, root, account)));
    }

    // Moves each record into the shared set, unless the set has one by that name.
    // Sessions have names of their own, so only per-account files such as the
    // schedule stay where they are.
    private static void Gather(string source, string to, List<DesktopSessionMerge> merged)
    {
        if (!IsPlainDirectory(source)) return;
        foreach (var entry in new DirectoryInfo(source).EnumerateFileSystemInfos())
        {
            var destination = Path.Combine(to, entry.Name);
            if (entry.LinkTarget is not null || File.Exists(destination) || Directory.Exists(destination)) continue;
            if (entry is DirectoryInfo directory) directory.MoveTo(destination);
            else ((FileInfo)entry).MoveTo(destination);
            merged.Add(new(source, to, entry.Name));
        }
    }

    // The organization under which the app files the account's session records, as it was before the switch.
    private string? SessionOrganization(string? account)
    {
        var root = SessionRoots[0];
        var directory = ReadMarker() is { } marker && marker.Sessions.TryGetValue(root, out var kept)
            ? SessionDirectory(root, kept)
            : FindSessions(root, account);
        return directory is not null && Path.GetFileName(Path.GetDirectoryName(directory)) == account ? Path.GetFileName(directory) : null;
    }

    // The app files some of the user's choices under the account they were
    // made in: whether the mode that skips permission prompts is enabled, the
    // permission mode of each folder, the sidebar's sections, the presets for
    // new sessions, the connector tools turned off. The new account gets those
    // of the account that was in use, replacing its own. The presets need their
    // Local Storage entry as well; see CarryStoredChoices.
    private void MirrorPreferences(string source, string? sourceOrganization, string target, string? targetOrganization,
        List<DesktopPreferenceChange> changes)
    {
        var renames = new Dictionary<string, string>(StringComparer.Ordinal) { [source] = target };
        if (sourceOrganization is not null && targetOrganization is not null)
            renames[source + "/" + sourceOrganization] = target + "/" + targetOrganization;
        if (ReadSettings() is { } settings && settings["preferences"] is JsonObject preferences)
        {
            var before = changes.Count;
            Mirror(preferences, ["preferences"], renames, changes, null);
            if (changes.Count > before) WriteSettings(settings);
        }

        foreach (var (name, format) in ChoiceFiles)
        {
            var path = Path.Combine(ActiveDirectory, name);
            if (ReadJsonObject(path) is not { } choices) continue;
            var before = changes.Count;
            Mirror(choices, [], renames, changes, name);
            if (changes.Count > before) WritePrivate(path, Encoding.UTF8.GetBytes(choices.ToJsonString(format)));
        }
    }

    private static void Mirror(JsonObject node, IReadOnlyList<string> path, Dictionary<string, string> renames,
        List<DesktopPreferenceChange> changes, string? file)
    {
        foreach (var (key, value) in node.ToArray())
        {
            // A gate is the app's own decision, which can follow an organization's
            // policy; it is left for the app to make for the new account.
            if (key.Contains("gate", StringComparison.OrdinalIgnoreCase) || renames.Values.Any(to => FiledUnder(key, to))) continue;
            if (renames.FirstOrDefault(rename => FiledUnder(key, rename.Key)) is { Key: { } from, Value: { } to })
            {
                var renamed = key[..^from.Length] + to;
                var previous = node[renamed]?.ToJsonString();
                if (previous == value?.ToJsonString()) continue;
                changes.Add(new([.. path, renamed], previous, file));
                node[renamed] = value?.DeepClone();
            }
            else if (value is JsonObject child)
            {
                Mirror(child, [.. path, key], renames, changes, file);
            }
        }
    }

    // The app files a choice under the account either as an entry named after
    // it or, as with the permission mode of each folder, as "<choice>.<account>".
    private static bool FiledUnder(string key, string account) =>
        key == account || key.Length > account.Length + 1 && key.EndsWith("." + account, StringComparison.Ordinal);

    // The new account's Local Storage entries become those of the account in
    // use, as the presets file does. One the account in use does not have is
    // removed, so that the app reads the file again. A database that is in use
    // or not as expected is left as it is, and the presets stay where they were.
    // Until that has been done once, the presets each registered account
    // gathered on its own join those of the account in use on the way; returns
    // whether it happened now.
    private bool CarryStoredChoices(string source, string target, JsonObject? presetsFile, List<DesktopStorageChange> changes)
    {
        var storage = LocalStorage;
        if (source == target || !Name.IsMatch(source) || !Name.IsMatch(target) || !storage.Exists) return false;
        List<string> gathered = File.Exists(PresetsMergedPath) ? [] :
        [
            target,
            .. (knownAccounts?.Invoke() ?? []).Where(account => Name.IsMatch(account) && account != source && account != target)
                .Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal),
        ];
        var names = StoredChoices.SelectMany(prefix => new[] { source, target }.Concat(gathered).Select(account => prefix + account));
        var written = new List<DesktopStorageChange>();
        var merged = false;
        try
        {
            storage.Update(StorageOrigin, names, current =>
            {
                foreach (var prefix in StoredChoices)
                {
                    var value = current.GetValueOrDefault(prefix + source);
                    // An account without an entry there has its presets in the file only.
                    if (prefix == PresetStorage && gathered.Count > 0 && value is not null
                        && MergedPresets(value, gathered.Select(account => current.TryGetValue(prefix + account, out var kept)
                            ? StoredPresets(kept) : presetsFile?[account]?["presets"] as JsonArray)) is { } all)
                    {
                        value = all;
                        merged = true;
                    }

                    var previous = current.GetValueOrDefault(prefix + target);
                    if (!SameBytes(value, previous)) written.Add(new(prefix + target, previous, value));
                }

                return written.Select(change => (change.Name, change.Written));
            });
        }
        catch (Exception error) when (error is ChromiumLocalStorageException or IOException or UnauthorizedAccessException)
        {
            return false;
        }

        changes.AddRange(written);
        if (!merged) return false;
        try
        {
            WritePrivate(PresetsMergedPath, Encoding.UTF8.GetBytes(DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture) + "\n"));
            return true;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            // Not on record: they are brought together again with a later switch, which changes nothing more.
            return false;
        }
    }

    // The presets of the account in use first, then each one only another
    // account has, every place once. Of presets with one name but different
    // places the app makes names of their own when it reads them.
    private static byte[]? MergedPresets(byte[] value, IEnumerable<JsonArray?> others)
    {
        if (ParseStored(value) is not { } stored || stored["state"]?["presets"] is not JsonArray presets) return null;
        var ids = new HashSet<string>(StringComparer.Ordinal);
        var places = new HashSet<string>(StringComparer.Ordinal);
        foreach (var preset in presets.OfType<JsonObject>())
        {
            if (PresetId(preset) is { } id) ids.Add(id);
            if (PlaceOf(preset) is { } place) places.Add(place);
        }

        foreach (var preset in others.SelectMany(list => list?.OfType<JsonObject>() ?? []))
            if (PresetId(preset) is { } id && PlaceOf(preset) is { } place && !ids.Contains(id) && places.Add(place))
            {
                ids.Add(id);
                presets.Add(preset.DeepClone());
            }

        return StoredValue(stored.ToJsonString(StoredFormat));
    }

    private static JsonArray? StoredPresets(byte[] value) => ParseStored(value)?["state"]?["presets"] as JsonArray;

    // Chromium keeps a value as Latin-1 behind a 1 when it can, and as UTF-16 behind a 0 otherwise.
    private static JsonObject? ParseStored(byte[] value)
    {
        string text;
        if (value.Length > 0 && value[0] == 1) text = Encoding.Latin1.GetString(value, 1, value.Length - 1);
        else if (value.Length % 2 == 1 && value[0] == 0) text = Encoding.Unicode.GetString(value, 1, value.Length - 1);
        else return null;
        try
        {
            return JsonNode.Parse(text) as JsonObject;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static byte[] StoredValue(string text) => text.All(character => character <= '\u00FF')
        ? [1, .. Encoding.Latin1.GetBytes(text)]
        : [0, .. Encoding.Unicode.GetBytes(text)];

    private static string? PresetId(JsonObject preset) =>
        preset["id"] is JsonValue id && id.TryGetValue<string>(out var text) && text.Length > 0 ? text : null;

    // Where a preset starts its sessions, as the app tells two apart: the
    // folder and the WSL distribution or SSH host it is on.
    private static string? PlaceOf(JsonObject preset)
    {
        if (preset["target"] is not JsonObject target) return null;
        string Part(string name) => target[name] is JsonValue value && value.TryGetValue<string>(out var text) ? text : "";
        return Part("kind") switch
        {
            "local" => string.Join('\n', "local", Part("cwd"), Part("wslDistro"), Part("variant")),
            "ssh" => string.Join('\n', "ssh", Part("sshConfigId"), Part("cwd")),
            _ => null,
        };
    }

    // What the app has written to an entry since is the account's own and stays.
    private void RestoreStoredChoices(IReadOnlyList<DesktopStorageChange> changes)
    {
        var storage = LocalStorage;
        if (changes.Count == 0 || !storage.Exists) return;
        try
        {
            storage.Update(StorageOrigin, changes.Select(change => change.Name), current => changes.Reverse()
                .Where(change => SameBytes(current.GetValueOrDefault(change.Name), change.Written))
                .Select(change => (change.Name, change.Previous)).ToList());
        }
        catch (Exception error) when (error is ChromiumLocalStorageException or IOException or UnauthorizedAccessException)
        {
        }
    }

    private static bool SameBytes(byte[]? first, byte[]? second) =>
        first is null ? second is null : second is not null && first.AsSpan().SequenceEqual(second);

    private void RestorePreferences(IReadOnlyList<DesktopPreferenceChange> changes)
    {
        foreach (var file in changes.GroupBy(change => change.File))
            Revert(file.Key ?? SettingsFile, ChoiceFiles.FirstOrDefault(choice => choice.Name == file.Key).Format ?? SettingsFormat, file.ToArray());
    }

    private void RestoreTokens(IReadOnlyList<DesktopPreferenceChange> changes) => Revert(ConfigFile, ConfigFormat, changes);

    private void Revert(string name, JsonSerializerOptions format, IReadOnlyList<DesktopPreferenceChange> changes)
    {
        var path = Path.Combine(ActiveDirectory, name);
        if (changes.Count == 0 || ReadJsonObject(path) is not { } contents) return;
        foreach (var change in changes.Reverse())
        {
            JsonObject? parent = contents;
            foreach (var segment in change.Path.Take(change.Path.Count - 1)) parent = parent?[segment] as JsonObject;
            if (parent is null) continue;
            if (change.Previous is null) parent.Remove(change.Path[^1]);
            else parent[change.Path[^1]] = JsonNode.Parse(change.Previous);
        }

        WritePrivate(path, Encoding.UTF8.GetBytes(contents.ToJsonString(format)));
    }

    private JsonObject? ReadSettings() => ReadJsonObject(Path.Combine(ActiveDirectory, SettingsFile));

    // A file the app could not read either is left alone.
    private static JsonObject? ReadJsonObject(string path)
    {
        try
        {
            var file = new FileInfo(path);
            if (!file.Exists || file.LinkTarget is not null || file.Length > MaximumSettingsBytes) return null;
            return JsonNode.Parse(File.ReadAllBytes(file.FullName)) as JsonObject;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException)
        {
            return null;
        }
    }

    private void WriteSettings(JsonObject settings) =>
        WritePrivate(Path.Combine(ActiveDirectory, SettingsFile), Encoding.UTF8.GetBytes(settings.ToJsonString(SettingsFormat)));

    private string? SessionDirectory(string root, string location)
    {
        var parts = location.Split('/');
        if (parts.Length != 2 || !parts.All(Name.IsMatch)) return null;
        var directory = Path.Combine(ActiveDirectory, root, parts[0], parts[1]);
        return IsPlainDirectory(directory) ? directory : null;
    }

    // Before the first switch the records are where the app put them: under the signed-in account.
    private string? FindSessions(string root, string? account)
    {
        if (account is null || !Name.IsMatch(account)) return null;
        var accountDirectory = Path.Combine(ActiveDirectory, root, account);
        var organizations = Organizations(accountDirectory);
        if (organizations.Length == 1) return organizations[0];
        // Several organizations: the one a registered login of the account names, if it is among them.
        return organizationOf?.Invoke(account) is { } organization && Name.IsMatch(organization)
            && IsPlainDirectory(Path.Combine(accountDirectory, organization))
            ? Path.Combine(accountDirectory, organization)
            : null;
    }

    private static bool IsPlainDirectory(string path) => Directory.Exists(path) && new DirectoryInfo(path).LinkTarget is null;

    private IEnumerable<string> EarlierProfiles() => Directory.Exists(paths.DesktopProfilesDirectory)
        ? Directory.GetDirectories(paths.DesktopProfilesDirectory).Where(IsPlainDirectory)
        : [];

    // The profile the user had before an earlier version made one per account.
    private string? OriginalToBringBack() => File.Exists(Path.Combine(ActiveDirectory, EarlierMarkerFile))
        ? EarlierProfiles().Where(profile => !File.Exists(Path.Combine(profile, EarlierMarkerFile)) && File.Exists(Path.Combine(profile, "config.json")))
            .OrderByDescending(profile => File.GetLastWriteTimeUtc(Path.Combine(profile, "config.json"))).FirstOrDefault()
        : null;

    // An earlier version kept a whole profile per account. The sign-ins in
    // those become sign-ins kept here, and the profile the user had before any
    // of it is the one that stays. Nothing is deleted.
    private void AdoptEarlierLayout()
    {
        foreach (var profile in EarlierProfiles())
            if (SignedInOwner(profile) is { } owner && !HasKeptLogin(owner) && File.Exists(Path.Combine(profile, CookieFile)))
                KeepLogin(profile, owner);
        if (OriginalToBringBack() is not { } original) return;
        var signedIn = SignedInOwner(ActiveDirectory);
        if (signedIn is not null && File.Exists(Path.Combine(ActiveDirectory, CookieFile))) KeepLogin(ActiveDirectory, signedIn);
        Directory.Move(ActiveDirectory, Path.Combine(paths.DesktopProfilesDirectory,
            (signedIn is not null && Name.IsMatch(signedIn) ? signedIn : "profile") + ".superseded-" + DateTime.UtcNow.Ticks.ToString(CultureInfo.InvariantCulture)));
        Directory.Move(original, ActiveDirectory);
    }

    private bool HasKeptLogin(string account) => File.Exists(Path.Combine(Slot(account), CookieFile));

    private string Slot(string account) => Path.Combine(
        paths.DesktopLoginsDirectory,
        Name.IsMatch(account) ? account : Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(account))));

    private Marker? ReadMarker()
    {
        try
        {
            var file = new FileInfo(MarkerPath);
            if (!file.Exists || file.LinkTarget is not null || file.Length > 64 * 1024) return null;
            if (JsonNode.Parse(File.ReadAllBytes(file.FullName)) is not JsonObject marker
                || marker["account"]?.GetValue<string>() is not { Length: > 0 } account) return null;
            var sessions = new Dictionary<string, string>(StringComparer.Ordinal);
            if (marker["sessions"] is JsonObject kept)
                foreach (var (root, location) in kept)
                    if (location?.GetValue<string>() is { } value) sessions[root] = value;
            return new(account, marker["seen"]?.GetValue<string>(), sessions);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException or InvalidOperationException or FormatException)
        {
            return null;
        }
    }

    private void WriteMarker(Marker marker)
    {
        var sessions = new JsonObject();
        foreach (var (root, location) in marker.Sessions) sessions[root] = location;
        WritePrivate(MarkerPath, Encoding.UTF8.GetBytes(
            new JsonObject { ["account"] = marker.Account, ["seen"] = marker.Seen, ["sessions"] = sessions }.ToJsonString()));
    }

    private static void WritePrivate(string destination, byte[] contents)
    {
        var temporary = Path.Combine(Path.GetDirectoryName(destination)!, "." + Path.GetFileName(destination) + "." + Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            WindowsPathSafety.WritePrivateBytes(temporary, contents);
            File.Move(temporary, destination, overwrite: true);
        }
        finally
        {
            File.Delete(temporary);
        }
    }
}
