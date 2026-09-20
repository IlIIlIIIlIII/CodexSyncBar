using CodexSyncBar.Windows.Core;
using System.Globalization;

namespace CodexSyncBar.Windows.Core.Tests;

public sealed class QuotaResetFormattingTests
{
    [Theory]
    [InlineData("en-US")]
    [InlineData("ko-KR")]
    [InlineData("ar-SA")]
    public void QuotaShowsCountdownAndLocalDateWithShortKoreanWeekday(string culture)
    {
        var previous = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo(culture);
            var reset = new DateTimeOffset(2026, 9, 26, 17, 54, 0, TimeSpan.FromHours(9));
            var text = UsageFormatting.QuotaResetDescription(reset, reset.AddHours(-3).AddMinutes(-5));
            var local = reset.ToLocalTime();
            var weekdays = new[] { "일", "월", "화", "수", "목", "금", "토" };
            Assert.Contains("3시간 5분 후 초기화", text);
            Assert.Contains($"{local.ToString("MM.dd", CultureInfo.InvariantCulture)} ({weekdays[(int)local.DayOfWeek]}) {local.ToString("HH:mm", CultureInfo.InvariantCulture)}", text);
        }
        finally { CultureInfo.CurrentCulture = previous; }
    }

    [Fact]
    public void MissingDateDoesNotInventADateAndExpiredDateIsStillVisible()
    {
        Assert.Equal("초기화 시각 미확인", UsageFormatting.QuotaResetDescription(null));
        var date = DateTimeOffset.UtcNow.AddMinutes(-5);
        Assert.StartsWith("곧 초기화\n", UsageFormatting.QuotaResetDescription(date));
        Assert.EndsWith(UsageFormatting.QuotaResetDate(date), UsageFormatting.QuotaResetDescription(date));
    }
}
