using System.Globalization;
using CodexSyncBar.Windows.Core;

namespace CodexSyncBar.Windows.Core.Tests;

public sealed class ResetCreditExpiryFormattingTests
{
    private static readonly DateTimeOffset ReferenceTime = new(2030, 1, 1, 0, 0, 0, TimeSpan.Zero);

    [Fact]
    public void EmptyOrMissingExpiryDataProducesNoInventedGroups()
    {
        Assert.Empty(UsageFormatting.ResetCreditExpiryGroups([], ReferenceTime));
        Assert.Empty(UsageFormatting.ResetCreditExpiryGroups(null!, ReferenceTime));
    }

    [Fact]
    public void GroupsByInstantAndSortsFromEarliestExpiry()
    {
        var earlier = ReferenceTime.AddDays(1);
        var later = ReferenceTime.AddDays(3);
        var groups = UsageFormatting.ResetCreditExpiryGroups(
            [later, earlier.ToOffset(TimeSpan.FromHours(9)), earlier, later], ReferenceTime);

        Assert.Collection(groups,
            group =>
            {
                Assert.Equal(2, group.Count);
                Assert.Equal(earlier, group.ExpiresAt);
                Assert.Equal("1일 0시간 0분 남음", group.RemainingText);
            },
            group =>
            {
                Assert.Equal(2, group.Count);
                Assert.Equal(later, group.ExpiresAt);
            });
    }

    [Theory]
    [InlineData(-1, "만료됨")]
    [InlineData(0, "만료됨")]
    [InlineData(1, "1분 미만 남음")]
    [InlineData(59, "1분 미만 남음")]
    [InlineData(60, "1분 남음")]
    [InlineData(119, "1분 남음")]
    [InlineData(120, "2분 남음")]
    [InlineData(3599, "59분 남음")]
    [InlineData(3600, "1시간 0분 남음")]
    [InlineData(19800, "5시간 30분 남음")]
    [InlineData(86399, "23시간 59분 남음")]
    [InlineData(86400, "1일 0시간 0분 남음")]
    [InlineData(86460, "1일 0시간 1분 남음")]
    [InlineData(183840, "2일 3시간 4분 남음")]
    public void RemainingTextPreservesDaysHoursAndMinutesAtBoundaries(int seconds, string expected)
    {
        var group = Assert.Single(UsageFormatting.ResetCreditExpiryGroups(
            [ReferenceTime.AddSeconds(seconds)], ReferenceTime));

        Assert.Equal(expected, group.RemainingText);
    }

    [Fact]
    public void FractionalMinuteAndExactExpiryAreDifferentStates()
    {
        var group = Assert.Single(UsageFormatting.ResetCreditExpiryGroups(
            [ReferenceTime.AddTicks(1)], ReferenceTime));

        Assert.Equal("1분 미만 남음", group.RemainingText);
    }

    [Fact]
    public void LongIntervalsDoNotOverflowMinuteCount()
    {
        var group = Assert.Single(UsageFormatting.ResetCreditExpiryGroups(
            [DateTimeOffset.MaxValue.AddDays(-1)], DateTimeOffset.MinValue));

        Assert.Equal("3652057일 23시간 59분 남음", group.RemainingText);
    }

    [Theory]
    [InlineData("en-US")]
    [InlineData("ar-SA")]
    [InlineData("th-TH")]
    public void LocalDateUsesGregorianDigitsAndKoreanWeekdaysRegardlessOfCulture(string cultureName)
    {
        var previousCulture = CultureInfo.CurrentCulture;
        var previousUiCulture = CultureInfo.CurrentUICulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo(cultureName);
            CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo(cultureName);
            string[] weekdays = ["일요일", "월요일", "화요일", "수요일", "목요일", "금요일", "토요일"];
            // Noon avoids DST clock-change ambiguity; the input deliberately uses
            // a different offset so the asserted display must convert to local time.
            var sunday = new DateTime(2030, 1, 6, 12, 34, 0, DateTimeKind.Unspecified);
            for (var day = 0; day < weekdays.Length; day++)
            {
                var localDate = sunday.AddDays(day);
                var local = new DateTimeOffset(localDate, TimeZoneInfo.Local.GetUtcOffset(localDate));
                var group = Assert.Single(UsageFormatting.ResetCreditExpiryGroups(
                    [local.ToOffset(TimeSpan.FromHours(11))], local.AddDays(-2).AddMinutes(-3)));

                Assert.Equal(local.Offset, group.ExpiresAt.Offset);
                Assert.Equal(local, group.ExpiresAt);
                Assert.Equal(
                    localDate.ToString("yyyy.MM.dd", CultureInfo.InvariantCulture) +
                    $" ({weekdays[day]}) 12:34", group.ExpiresAtText);
                Assert.Equal("2일 0시간 3분 남음", group.RemainingText);
            }
        }
        finally
        {
            CultureInfo.CurrentCulture = previousCulture;
            CultureInfo.CurrentUICulture = previousUiCulture;
        }
    }

    [Fact]
    public void ExistingCompactExpiryDescriptionKeepsItsContract()
    {
        Assert.Equal("2일 3시간", UsageFormatting.ResetCreditExpiryDescription(
            ReferenceTime.AddDays(2).AddHours(3).AddMinutes(4), ReferenceTime));
        Assert.Equal("다음 만료 5시간 30분 · 외 1회",
            UsageFormatting.CompactResetCreditExpiryDescription(
                [ReferenceTime.AddHours(5.5), ReferenceTime.AddHours(7)], ReferenceTime));
    }
}
