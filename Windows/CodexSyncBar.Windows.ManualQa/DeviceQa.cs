using System.Text.Json;
using CodexSyncBar.Windows.Core;

internal static class DeviceQa
{
    public static async Task<int> RunAsync(string[] args)
    {
        // Real-device operations require an explicit device and operation. Never print credentials.
        var paths = new WindowsPaths();
        var store = new ConfigurationStore(paths);
        var configuration = store.LoadOrCreate();
        var device = configuration.Devices.Single(item => item.Id == args[1]);
        var auth = new AuthStore(paths);
        var local = new LocalSwitchService(auth, paths);
        var service = new SshDeviceService(auth, paths, local);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMinutes(10));
        var token = cancellation.Token;
        try
        {
            switch (args[2])
            {
                case "recover":
                    using (await ControllerMutationLock.AcquireAsync(paths, cancellationToken: token))
                    {
                        var pending = await service.RecoverPendingBootstrapTransactionsAsync(configuration, token);
                        Console.WriteLine(JsonSerializer.Serialize(new { Pending = pending }));
                        if (pending.Count != 0) return 1;
                    }
                    break;
                case "inspect-host":
                    var key = await new SshHostTrust().InspectAsync(device, token);
                    Console.WriteLine(JsonSerializer.Serialize(new { Trusted = key is null, key?.Host, key?.Port, key?.Fingerprint }));
                    break;
                case "trust-host":
                    if (args.Length != 4) throw new InvalidOperationException("Expected approved SHA256 fingerprint.");
                    var trust = new SshHostTrust();
                    var approved = await trust.InspectAsync(device, token);
                    if (approved is not null)
                    {
                        if (approved.Fingerprint != args[3]) throw new InvalidOperationException("Host fingerprint differs.");
                        await trust.TrustAsync(device, approved, token);
                    }
                    Console.WriteLine("Host key registered.");
                    break;
                case "test":
                    var result = await service.TestConnectionAsync(device, token);
                    Console.WriteLine(JsonSerializer.Serialize(result));
                    if (!result.IsReachable) return 1;
                    break;
                case "inspect-cli":
                    Console.WriteLine(JsonSerializer.Serialize(CliInstallation.Parse(await service.RunCodexHelperAsync(device, false, token))));
                    break;
                case "activate":
                    using (await ControllerMutationLock.AcquireAsync(paths, cancellationToken: token))
                    {
                        var connection = await service.TestConnectionAsync(device, token);
                        if (!connection.IsReachable) throw new CodexSyncBarException(connection.Message);
                        var bootstrap = await service.BootstrapAsync(configuration, device, local.GetActiveProfileId(configuration.Accounts), token);
                        var transactions = new DeviceActivationTransactionStore(paths);
                        var intent = transactions.Save(device);
                        try
                        {
                            store.BeginDeviceActivation(configuration, device);
                            var statuses = await service.FetchStatusesAsync(configuration, local.GetActiveProfileId(configuration.Accounts), token);
                            var status = statuses.Single(item => item.Id == device.Id);
                            if (!status.IsReachable || status.ProfileId != bootstrap.ActiveProfileId) throw new CodexSyncBarException("Remote active profile verification failed.");
                            transactions.Delete(intent);
                            Console.WriteLine(JsonSerializer.Serialize(new { Activated = true, Device = device.Id, ActiveProfileVerified = true }));
                        }
                        catch
                        {
                            store.RollbackDeviceActivation(configuration, device);
                            transactions.Delete(intent);
                            throw;
                        }
                    }
                    break;
                default: throw new ArgumentException("Unknown device QA operation.");
            }
            return 0;
        }
        catch (Exception error)
        {
            Console.Error.WriteLine(error.Message);
            return 1;
        }
    }
}
