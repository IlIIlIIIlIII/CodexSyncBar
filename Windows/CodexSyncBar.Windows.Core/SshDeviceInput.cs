using System.Text.RegularExpressions;

namespace CodexSyncBar.Windows.Core;

public static class SshDeviceInput
{
    public static void Validate(string host, string username, double port, string authentication,
        string identityFile, string password, bool hasStoredPassword)
    {
        if (!Regex.IsMatch(host, "^[A-Za-z0-9][A-Za-z0-9._:-]{0,252}$"))
            throw new CodexSyncBarException("호스트에는 hostname 또는 IP만 입력해 주세요. 사용자 이름은 아래 칸에 입력합니다.");
        if (!Regex.IsMatch(username, "^[A-Za-z0-9_][A-Za-z0-9._-]{0,63}$"))
            throw new CodexSyncBarException("SSH 사용자 이름을 입력해 주세요.");
        if (!double.IsFinite(port) || port < 1 || port > 65535 || port != Math.Truncate(port))
            throw new CodexSyncBarException("포트는 1~65535 사이의 정수여야 합니다.");
        if (authentication == "password" && password.Length == 0 && !hasStoredPassword)
            throw new CodexSyncBarException("SSH 비밀번호를 입력해 주세요.");
        if (authentication == "privateKey" && (!Path.IsPathFullyQualified(identityFile) || !File.Exists(identityFile)))
            throw new CodexSyncBarException("존재하는 개인 키 파일을 선택해 주세요.");
    }
}
