using System.Globalization;

namespace DevRefresh;

public sealed record ShrinkScheduleInput(bool Enabled, int[] Days, string Time);
public sealed record ShrinkSchedule(string SourceKey, bool Enabled, int[] Days, string Time,
    DateTimeOffset SavedAt, string SavedBy)
{
    public DateTimeOffset? DueSlot(DateTimeOffset now, TimeZoneInfo zone)
    {
        if (!Enabled) return null;
        var localToday = TimeZoneInfo.ConvertTime(now, zone).Date;
        var time = TimeOnly.ParseExact(Time, "HH:mm", CultureInfo.InvariantCulture);
        for (var daysAgo = 0; daysAgo <= 7; daysAgo++)
        {
            var day = localToday.AddDays(-daysAgo);
            if (!Days.Contains((int)day.DayOfWeek)) continue;
            var local = DateTime.SpecifyKind(day.Add(time.ToTimeSpan()), DateTimeKind.Unspecified);
            if (zone.IsInvalidTime(local)) continue;
            var offset = zone.IsAmbiguousTime(local) ? zone.GetAmbiguousTimeOffsets(local).Max() : zone.GetUtcOffset(local);
            var slot = new DateTimeOffset(local, offset).ToUniversalTime();
            if (slot <= now) return slot >= SavedAt ? slot : null;
        }
        return null;
    }
}

public sealed record ShrinkBackup(string Id, string SourceKey, string Name, string FullBackupName,
    DateOnly DataDate, DateTimeOffset PreparedAt, long Size);

internal sealed record StoredShrinkBackup(ShrinkBackup Backup, string Directory, string SqlDirectory, string Server)
{
    internal string PathInDirectory => Path.Combine(Directory, Backup.Name);
    internal string SqlPath => Path.Combine(SqlDirectory, Backup.Name);

    internal bool Matches(SqlSettings settings) =>
        string.Equals(Server, settings.Server, StringComparison.OrdinalIgnoreCase) &&
        ShrinkBackups.SamePath(Directory, settings.ShrinkBackupDirectory) &&
        ShrinkBackups.SamePath(SqlDirectory, settings.SqlShrinkBackupDirectory);
}

internal static class ShrinkBackups
{
    internal static bool Configured(SqlSettings settings) =>
        !string.IsNullOrWhiteSpace(settings.ShrinkBackupDirectory) && !string.IsNullOrWhiteSpace(settings.SqlShrinkBackupDirectory);

    internal static bool SamePath(string left, string right) =>
        string.Equals(left.TrimEnd('\\', '/'), right.TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase);

    internal static void ValidatePaths(string path, string sqlPath, string fullPath, string fullSqlPath)
    {
        if (path.Length == 0 && sqlPath.Length == 0) return;
        if (new[] { path, sqlPath }.Any(p => p.Length > 2048 || !Path.IsPathFullyQualified(p) || p.Any(char.IsControl)))
            throw new InputException("Укажите оба абсолютных пути к папке Shrink-копий.");
        try
        {
            if (SamePath(Path.GetFullPath(path), Path.GetFullPath(fullPath)) ||
                SamePath(Path.GetFullPath(sqlPath), Path.GetFullPath(fullSqlPath)))
                throw new InputException("Для Shrink-копий нужна отдельная папка.");
        }
        catch (ArgumentException)
        {
            throw new InputException("Укажите корректные пути к каталогам бэкапов.");
        }
    }

    internal static ShrinkSchedule CreateSchedule(string source, ShrinkScheduleInput input, string actor, DateTimeOffset now)
    {
        if (!TimeOnly.TryParseExact(input.Time, "HH:mm", CultureInfo.InvariantCulture, DateTimeStyles.None, out var time) ||
            input.Days is null || input.Days.Any(day => day is < 0 or > 6) ||
            (input.Enabled && input.Days.Length == 0))
            throw new InputException("Укажите время и дни недели для расписания.");
        return new(source, input.Enabled, input.Days.Distinct().Order().ToArray(),
            time.ToString("HH:mm", CultureInfo.InvariantCulture), now, actor);
    }

    internal static ShrinkBackup? Available(Store store, SqlSettings settings, string source)
    {
        if (!Configured(settings)) return null;
        var item = store.GetShrinkBackup(source);
        return item is not null && item.Matches(settings) && File.Exists(item.PathInDirectory) ? item.Backup : null;
    }

    internal static string FileName(string source, string jobId) => $"shrink-{source}-{jobId}.bak";
}
