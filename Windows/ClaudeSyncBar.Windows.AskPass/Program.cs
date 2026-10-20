// ssh.exe runs this program with its prompt as the only argument whenever a device
// needs its stored password or key passphrase. SshDeviceService names the secret in
// the environment; the value itself stays in this Windows user's DPAPI store and is
// written to ssh.exe alone. Nothing is ever printed on refusal or error.
using System.Security.Cryptography;
using System.Text;
using ClaudeSyncBar.Windows.Core;

// Confirmations and notices (SSH_ASKPASS_PROMPT=confirm|none) never take a secret.
if (!string.IsNullOrEmpty(Environment.GetEnvironmentVariable("SSH_ASKPASS_PROMPT"))) return 1;
if (!Guid.TryParseExact(Environment.GetEnvironmentVariable("CLAUDE_SYNCBAR_CREDENTIAL_ID"), "D", out var credential)) return 64;
var kind = Environment.GetEnvironmentVariable("CLAUDE_SYNCBAR_SECRET_KIND");
if (kind is not ("password" or "passphrase") || args.Length != 1) return 64;

// Answer only the prompt for this kind of secret, so a key passphrase never goes to a
// server's password prompt and a stored password never becomes a new one.
var prompt = args[0];
if (!prompt.Contains(kind, StringComparison.OrdinalIgnoreCase)
    || prompt.Contains("new password", StringComparison.OrdinalIgnoreCase)) return 1;

byte[] bytes;
try
{
    var secret = new WindowsSecretStore(new WindowsPaths()).Read(credential.ToString("D") + "." + kind);
    if (string.IsNullOrEmpty(secret)) return 1;
    bytes = Encoding.UTF8.GetBytes(secret);
}
catch
{
    return 1;
}

// Raw UTF-8 bytes: the console code page must not change what reaches the server.
try
{
    using var output = Console.OpenStandardOutput();
    output.Write(bytes);
    output.Flush();
    return 0;
}
catch
{
    return 1;
}
finally
{
    CryptographicOperations.ZeroMemory(bytes);
}
