namespace TelegramPanel.Web.Services;

/// <summary>
/// 轻量 Cron 表达式服务（5 位：分 时 日 月 周）。
/// </summary>
public sealed class CronExpressionService
{
    public DateTime? GetNextOccurrenceUtc(string expression, DateTime fromUtc)
    {
        var schedule = CronSchedule.Parse(expression);
        return schedule.GetNextOccurrenceUtc(fromUtc, TimeZoneInfo.Local);
    }

    public bool TryValidate(string expression, out string? error)
    {
        try
        {
            _ = CronSchedule.Parse(expression);
            error = null;
            return true;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
    }

    private sealed class CronSchedule
    {
        private readonly CronField _minutes;
        private readonly CronField _hours;
        private readonly CronField _days;
        private readonly CronField _months;
        private readonly CronField _weekdays;

        private CronSchedule(CronField minutes, CronField hours, CronField days, CronField months, CronField weekdays)
        {
            _minutes = minutes;
            _hours = hours;
            _days = days;
            _months = months;
            _weekdays = weekdays;
        }

        public static CronSchedule Parse(string expression)
        {
            var parts = (expression ?? string.Empty)
                .Split(new[] { ' ', '\t', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (parts.Length != 5)
                throw new InvalidOperationException("Cron 表达式必须是 5 段：分 时 日 月 周");

            return new CronSchedule(
                CronField.Parse(parts[0], 0, 59, FieldKind.Minute),
                CronField.Parse(parts[1], 0, 23, FieldKind.Hour),
                CronField.Parse(parts[2], 1, 31, FieldKind.Day),
                CronField.Parse(parts[3], 1, 12, FieldKind.Month),
                CronField.Parse(parts[4], 0, 6, FieldKind.Weekday));
        }

        public DateTime? GetNextOccurrenceUtc(DateTime fromUtc, TimeZoneInfo timeZone)
        {
            var local = TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(fromUtc, DateTimeKind.Utc), timeZone);
            var candidate = new DateTime(local.Year, local.Month, local.Day, local.Hour, local.Minute, 0, DateTimeKind.Unspecified)
                .AddMinutes(1);

            for (var i = 0; i < 60 * 24 * 366 * 5; i++)
            {
                if (Matches(candidate))
                {
                    try
                    {
                        return TimeZoneInfo.ConvertTimeToUtc(candidate, timeZone);
                    }
                    catch (ArgumentException)
                    {
                        // 跳过夏令时无效时间。
                    }
                }

                candidate = candidate.AddMinutes(1);
            }

            return null;
        }

        private bool Matches(DateTime candidate)
        {
            if (!_minutes.Contains(candidate.Minute))
                return false;
            if (!_hours.Contains(candidate.Hour))
                return false;
            if (!_months.Contains(candidate.Month))
                return false;

            var dayMatch = _days.Contains(candidate.Day);
            var weekMatch = _weekdays.Contains((int)candidate.DayOfWeek);
            var calendarMatch = _days.IsAny && _weekdays.IsAny
                ? true
                : _days.IsAny
                    ? weekMatch
                    : _weekdays.IsAny
                        ? dayMatch
                        : dayMatch || weekMatch;

            return calendarMatch;
        }

        private enum FieldKind
        {
            Minute,
            Hour,
            Day,
            Month,
            Weekday
        }

        private sealed class CronField
        {
            private readonly bool[] _allowed;

            private CronField(bool[] allowed, bool isAny)
            {
                _allowed = allowed;
                IsAny = isAny;
            }

            public bool IsAny { get; }

            public bool Contains(int value)
            {
                if (value < 0 || value >= _allowed.Length)
                    return false;
                return _allowed[value];
            }

            public static CronField Parse(string text, int min, int max, FieldKind kind)
            {
                text = (text ?? string.Empty).Trim();
                if (text.Length == 0)
                    throw new InvalidOperationException("Cron 字段不能为空");

                var size = max + 1;
                var allowed = new bool[size];
                var isAny = text is "*" or "?";
                if (isAny)
                {
                    for (var value = min; value <= max; value++)
                        allowed[value] = true;
                    return new CronField(allowed, true);
                }

                foreach (var part in text.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                {
                    ApplyPart(part, allowed, min, max, kind);
                }

                if (!allowed.Any(x => x))
                    throw new InvalidOperationException($"Cron 字段无可用值：{text}");

                return new CronField(allowed, false);
            }

            private static void ApplyPart(string part, bool[] allowed, int min, int max, FieldKind kind)
            {
                var step = 1;
                var rangePart = part;

                var slashIndex = part.IndexOf('/');
                if (slashIndex >= 0)
                {
                    rangePart = part[..slashIndex];
                    var stepPart = part[(slashIndex + 1)..];
                    if (!int.TryParse(stepPart, out step) || step <= 0)
                        throw new InvalidOperationException($"Cron 步长无效：{part}");
                }

                int start;
                int end;
                if (rangePart is "*" or "?")
                {
                    start = min;
                    end = max;
                }
                else if (rangePart.Contains('-'))
                {
                    var segments = rangePart.Split('-', 2, StringSplitOptions.TrimEntries);
                    start = ParseValue(segments[0], kind);
                    end = ParseValue(segments[1], kind);
                }
                else
                {
                    start = ParseValue(rangePart, kind);
                    end = slashIndex >= 0 ? max : start;
                }

                if (kind == FieldKind.Weekday)
                {
                    start = NormalizeWeekday(start);
                    end = NormalizeWeekday(end);
                }

                if (start < min || start > max || end < min || end > max)
                    throw new InvalidOperationException($"Cron 取值超出范围：{part}");
                if (end < start)
                    throw new InvalidOperationException($"Cron 范围无效：{part}");

                for (var value = start; value <= end; value += step)
                    allowed[value] = true;
            }

            private static int ParseValue(string raw, FieldKind kind)
            {
                raw = (raw ?? string.Empty).Trim().ToUpperInvariant();
                if (raw.Length == 0)
                    throw new InvalidOperationException("Cron 字段取值不能为空");

                if (kind == FieldKind.Month)
                {
                    var month = raw switch
                    {
                        "JAN" => 1,
                        "FEB" => 2,
                        "MAR" => 3,
                        "APR" => 4,
                        "MAY" => 5,
                        "JUN" => 6,
                        "JUL" => 7,
                        "AUG" => 8,
                        "SEP" => 9,
                        "OCT" => 10,
                        "NOV" => 11,
                        "DEC" => 12,
                        _ => (int?)null
                    };
                    if (month.HasValue)
                        return month.Value;
                }

                if (kind == FieldKind.Weekday)
                {
                    var weekday = raw switch
                    {
                        "SUN" => 0,
                        "MON" => 1,
                        "TUE" => 2,
                        "WED" => 3,
                        "THU" => 4,
                        "FRI" => 5,
                        "SAT" => 6,
                        _ => (int?)null
                    };
                    if (weekday.HasValue)
                        return weekday.Value;
                }

                if (!int.TryParse(raw, out var value))
                    throw new InvalidOperationException($"Cron 字段取值无效：{raw}");

                return value;
            }

            private static int NormalizeWeekday(int value)
            {
                return value == 7 ? 0 : value;
            }
        }
    }
}
