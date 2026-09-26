using CodexSyncBar.Windows.Core;

namespace CodexSyncBar.Windows.Core.Tests;

public sealed class ProcessResponsivenessTests
{
    [WindowsFact]
    public async Task LargeOutputBeforeReadingLargeInputDoesNotDeadlock()
    {
        const int size = 1024 * 1024;
        var result = await ProcessRunner.RunAsync("node", ["-e",
            "process.stdout.write('o'.repeat(1048576), () => { let n = 0; process.stdin.on('data', b => n += b.length); process.stdin.on('end', () => process.stderr.write(String(n))); });"],
            new string('i', size), timeout: TimeSpan.FromSeconds(15));
        Assert.Equal(0, result.ExitCode);
        Assert.Equal(size, result.StandardOutput.Length);
        Assert.Equal(size.ToString(), result.StandardError);
    }

    [WindowsFact]
    public async Task TimeoutInterruptsChildThatNeverReadsInput()
    {
        var operation = ProcessRunner.RunAsync("node", ["-e", "setInterval(() => {}, 1000)"],
            new string('i', 1024 * 1024), timeout: TimeSpan.FromSeconds(1));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => operation.WaitAsync(TimeSpan.FromSeconds(10)));
    }
}
