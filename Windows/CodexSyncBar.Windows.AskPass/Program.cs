// OpenSSH launches this executable directly; never pass secrets through a shell.
var secret = Environment.GetEnvironmentVariable("CODEX_SYNCBAR_ASKPASS_SECRET");
if (secret is null) return 1;
Console.OutputEncoding = new System.Text.UTF8Encoding(false);
Console.WriteLine(secret);
return 0;
