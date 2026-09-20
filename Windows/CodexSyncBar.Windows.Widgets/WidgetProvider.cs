using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using CodexSyncBar.Windows.Core;
using Microsoft.Windows.Widgets;
using Microsoft.Windows.Widgets.Providers;

namespace CodexSyncBar.Windows.Widgets;

[ComVisible(true)]
[Guid(WidgetTemplates.ProviderClassId)]
public sealed class WidgetProvider : IWidgetProvider
{
    public static bool DemoMode { get; set; }
    public static ManualResetEvent Shutdown { get; } = new(false);
    private readonly ConcurrentDictionary<string, WidgetState> widgets = new();
    private readonly DashboardPipeClient client;
    private readonly Timer timer;
    private readonly SemaphoreSlim refreshGate = new(1, 1);
    private DashboardSnapshot snapshot = new();
    private int actionInProgress;
    private long snapshotRequest;
    private long appliedSnapshotRequest;
    private readonly object snapshotGate = new();

    public WidgetProvider()
    {
        client = new DashboardPipeClient(DashboardPipeServer.DefaultPipeName + (DemoMode ? ".demo" : ""), LaunchAppAsync);
        // Copy values while in the WinRT callback; never retain WidgetContext objects.
        var manager = WidgetManager.GetDefault();
        if (manager is null)
        {
            ProviderLog.Status("restore-manager-unavailable");
            throw new COMException("Windows widget manager is unavailable.", unchecked((int)0x80040154));
        }
        // The host can return null rather than an empty collection before the
        // first widget is pinned. Treat it as a valid empty restoration state.
        var existing = manager.GetWidgetInfos();
        ProviderLog.Status(existing is null ? "restore-infos-null" : "restore-infos", existing?.Length ?? 0);
        foreach (var widget in existing ?? [])
        {
            if (widget is null) continue;
            var context = widget.WidgetContext;
            if (context is null) continue;
            if (context.DefinitionId != WidgetTemplates.DefinitionId) continue;
            var state = new WidgetState(context.Id, context.Size == WidgetSize.Large);
            try
            {
                if (!string.IsNullOrWhiteSpace(widget.CustomState))
                    state.OperationId = JsonSerializer.Deserialize<SavedState>(widget.CustomState)?.OperationId;
            }
            catch (JsonException) { }
            widgets[state.Id] = state;
        }
        timer = new Timer(_ => Queue(RefreshActiveAsync), null, TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(30));
        Queue(() => RefreshAsync(force: false));
    }

    public void CreateWidget(WidgetContext widgetContext)
    {
        if (widgetContext.DefinitionId != WidgetTemplates.DefinitionId) return;
        Shutdown.Reset();
        var state = new WidgetState(widgetContext.Id, widgetContext.Size == WidgetSize.Large) { Active = true };
        widgets[state.Id] = state;
        ProviderLog.Status(state.Large ? "created-large" : "created-medium", widgets.Count);
        Publish(state);
        Queue(() => RefreshAsync(force: false));
    }

    public void DeleteWidget(string widgetId, string customState)
    {
        widgets.TryRemove(widgetId, out _);
        ProviderLog.Status("deleted", widgets.Count);
        if (widgets.IsEmpty)
        {
            timer.Change(Timeout.Infinite, Timeout.Infinite);
            Shutdown.Set();
        }
    }

    public void OnWidgetContextChanged(WidgetContextChangedArgs contextChangedArgs)
    {
        var context = contextChangedArgs.WidgetContext;
        if (widgets.TryGetValue(context.Id, out var state))
        {
            state.Large = context.Size == WidgetSize.Large;
            ProviderLog.Status(state.Large ? "resized-large" : "resized-medium");
            Publish(state);
        }
    }

    public void Activate(WidgetContext widgetContext)
    {
        var id = widgetContext.Id;
        if (widgets.TryGetValue(id, out var state))
        {
            state.Active = true;
            state.Large = widgetContext.Size == WidgetSize.Large;
            Publish(state); // Cached content first, network refresh next.
            Queue(() => RefreshAsync(force: false));
        }
    }

    public void Deactivate(string widgetId)
    {
        if (widgets.TryGetValue(widgetId, out var state)) state.Active = false;
    }

    public void OnActionInvoked(WidgetActionInvokedArgs actionInvokedArgs)
    {
        // Callback arguments are only valid during this method.
        var id = actionInvokedArgs.WidgetContext.Id;
        var verb = actionInvokedArgs.Verb;
        var data = actionInvokedArgs.Data;
        if (!widgets.TryGetValue(id, out var state)) return;
        Queue(() => HandleActionAsync(state, verb, data));
    }

    private async Task HandleActionAsync(WidgetState state, string verb, string data)
    {
        if (Interlocked.CompareExchange(ref actionInProgress, 1, 0) != 0) return;
        try
        {
            var action = WidgetTemplates.ParseAction(verb, data);
            // Only validated, fixed action names; never log input, aliases or IDs.
            ProviderLog.Status(verb switch
            {
                "apply" => "action-apply",
                "refresh" => "action-refresh",
                _ => "action-settings",
            });
            state.Notice = null;
            if (verb == "settings") { await client.OpenSettingsAsync(); return; }
            if (verb == "refresh") { await RefreshAsync(force: true); return; }

            state.Notice = "모든 장치에 계정을 적용하고 있습니다…";
            PublishAll();
            var result = await client.SendAsync(new DashboardPipeRequest(
                "SwitchAccount", action.RequestId, action.ProfileId, action.ConfigurationRevision));
            var operation = result.Operation ?? throw new InvalidDataException();
            state.OperationId = operation.Id;
            // Persist the operation ID before waiting, so provider restarts can recover status.
            Publish(state);
            using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(3));
            while (!operation.IsComplete)
            {
                state.Notice = operation.Message;
                await LoadSnapshotAsync(cancellationToken: deadline.Token);
                PublishAll();
                await Task.Delay(TimeSpan.FromSeconds(1), deadline.Token);
                operation = await client.GetOperationStatusAsync(operation.Id, deadline.Token)
                    ?? throw new InvalidDataException();
            }
            state.Notice = operation.Message;
            state.OperationId = null;
            await LoadSnapshotAsync();
        }
        catch (DashboardPipeException ex)
        {
            state.Notice = ex.Message; // Server uses fixed, credential-free messages.
            try { await LoadSnapshotAsync(); } catch (Exception) { }
        }
        catch (Exception ex)
        {
            ProviderLog.Failure("action", ex);
            state.Notice = "완료 상태를 확인하지 못했습니다. 앱에서 전환·복구 결과를 확인해 주세요.";
        }
        finally
        {
            Interlocked.Exchange(ref actionInProgress, 0);
            PublishAll();
        }
    }

    private Task RefreshActiveAsync() => widgets.Values.Any(w => w.Active)
        ? RefreshAsync(force: false) : Task.CompletedTask;

    private async Task RefreshAsync(bool force)
    {
        if (!await refreshGate.WaitAsync(0)) return;
        try
        {
            await LoadSnapshotAsync();
            foreach (var state in widgets.Values.Where(s => s.OperationId is not null))
            {
                var operation = await client.GetOperationStatusAsync(state.OperationId!);
                state.Notice = operation?.Message;
                if (operation is null || operation.IsComplete) state.OperationId = null;
            }
            PublishAll();
            if (force || snapshot.Accounts.Any(a => !a.NeedsLogin &&
                (a.Usage is null || DateTimeOffset.UtcNow - a.Usage.UpdatedAt > TimeSpan.FromMinutes(5))))
            {
                await LoadSnapshotAsync(refresh: true);
                PublishAll();
            }
        }
        catch (Exception ex)
        {
            ProviderLog.Failure("refresh", ex);
            foreach (var state in widgets.Values) state.Notice = "앱에 연결할 수 없습니다. 마지막 갱신 시각을 확인해 주세요.";
            PublishAll();
        }
        finally { refreshGate.Release(); }
    }

    private void PublishAll()
    {
        foreach (var state in widgets.Values) Publish(state);
    }

    private async Task LoadSnapshotAsync(bool refresh = false, CancellationToken cancellationToken = default)
    {
        var request = Interlocked.Increment(ref snapshotRequest);
        var next = refresh ? await client.RefreshUsageAsync(cancellationToken) : await client.GetSnapshotAsync(cancellationToken);
        lock (snapshotGate)
        {
            // A late refresh response must not replace a newer switch/poll response.
            if (request <= appliedSnapshotRequest) return;
            appliedSnapshotRequest = request;
            snapshot = next;
        }
    }

    private void Publish(WidgetState state)
    {
        if (!widgets.ContainsKey(state.Id)) return;
        try
        {
            WidgetManager.GetDefault().UpdateWidget(new WidgetUpdateRequestOptions(state.Id)
            {
                Template = WidgetTemplates.Render(snapshot, state.Large, state.Notice,
                    Volatile.Read(ref actionInProgress) != 0),
                Data = "{}",
                CustomState = JsonSerializer.Serialize(new SavedState(state.OperationId)),
            });
        }
        catch (Exception ex) { ProviderLog.Failure("publish", ex); }
    }

    private static Task LaunchAppAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, ".."));
        var executable = Path.Combine(root, "CodexSyncBar.Windows.exe");
        // Also support adjacent outputs for local unpackaged smoke tests.
        if (!File.Exists(executable)) executable = Path.Combine(AppContext.BaseDirectory, "CodexSyncBar.Windows.exe");
        if (!File.Exists(executable)) throw new FileNotFoundException("앱 실행 파일이 없습니다.");
        var start = new ProcessStartInfo(executable) { UseShellExecute = false, WorkingDirectory = Path.GetDirectoryName(executable)! };
        start.ArgumentList.Add("--background");
        if (DemoMode) start.ArgumentList.Add("--demo");
        Process.Start(start)?.Dispose();
        return Task.CompletedTask;
    }

    private static void Queue(Func<Task> action)
    {
        _ = Task.Run(async () =>
        {
            try { await action(); }
            catch (Exception ex) { ProviderLog.Failure("callback", ex); }
        });
    }

    private sealed class WidgetState(string id, bool large)
    {
        public string Id { get; } = id;
        public bool Large { get; set; } = large;
        public bool Active { get; set; }
        public string? OperationId { get; set; }
        public string? Notice { get; set; }
    }

    private sealed record SavedState(string? OperationId);
}
