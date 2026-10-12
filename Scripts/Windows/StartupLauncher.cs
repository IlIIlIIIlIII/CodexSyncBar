using System;
using System.Diagnostics;
using System.IO;

// Compiled as a Windows application so Task Scheduler never allocates a console.
internal static class StartupLauncher
{
    [STAThread]
    private static int Main()
    {
        string directory = AppDomain.CurrentDomain.BaseDirectory;
        try
        {
            var start = new ProcessStartInfo
            {
                FileName = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System),
                    @"WindowsPowerShell\v1.0\powershell.exe"),
                Arguments = "-NoProfile -NonInteractive -WindowStyle Hidden -ExecutionPolicy Bypass -File \""
                    + Path.Combine(directory, "launch.ps1") + "\"",
                WorkingDirectory = directory,
                UseShellExecute = false,
                CreateNoWindow = true,
                WindowStyle = ProcessWindowStyle.Hidden
            };
            using (var process = Process.Start(start))
            {
                process.WaitForExit();
                return process.ExitCode;
            }
        }
        catch (Exception exception)
        {
            try
            {
                File.AppendAllText(Path.Combine(directory, "startup.log"),
                    DateTimeOffset.Now.ToString("o") + " Launcher failed: " + exception.Message + Environment.NewLine);
            }
            catch { }
            return 1;
        }
    }
}
