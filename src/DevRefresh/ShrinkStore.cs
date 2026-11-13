using System.Text.Json;

namespace DevRefresh;

public sealed partial class Store
{
    public ShrinkSchedule[] GetShrinkSchedules()
    {
        using var db = Open();
        using var command = db.CreateCommand();
        command.CommandText = "SELECT value FROM shrink_schedules ORDER BY source_key";
        using var reader = command.ExecuteReader();
        var schedules = new List<ShrinkSchedule>();
        while (reader.Read()) schedules.Add(JsonSerializer.Deserialize<ShrinkSchedule>(reader.GetString(0))!);
        return [.. schedules];
    }

    public void SaveShrinkSchedule(ShrinkSchedule schedule)
    {
        using var db = Open();
        using var transaction = db.BeginTransaction();
        using var command = db.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO shrink_schedules(source_key,value) VALUES($source,$value)
            ON CONFLICT(source_key) DO UPDATE SET value=excluded.value
            """;
        command.Parameters.AddWithValue("$source", schedule.SourceKey);
        command.Parameters.AddWithValue("$value", JsonSerializer.Serialize(schedule));
        command.ExecuteNonQuery();
        WriteAudit(db, transaction, schedule.SavedBy, "shrink.schedule.saved",
            $"Тип {schedule.SourceKey}. Расписание {(schedule.Enabled ? "включено" : "выключено")}.");
        transaction.Commit();
    }

    internal StoredShrinkBackup? GetShrinkBackup(string source)
    {
        using var db = Open();
        using var command = db.CreateCommand();
        command.CommandText = "SELECT value FROM shrink_backups WHERE source_key=$source AND state='current'";
        command.Parameters.AddWithValue("$source", source);
        return command.ExecuteScalar() is string json ? JsonSerializer.Deserialize<StoredShrinkBackup>(json) : null;
    }

    internal void RegisterShrinkBackup(StoredShrinkBackup item)
    {
        using var db = Open();
        using var command = db.CreateCommand();
        command.CommandText = "INSERT INTO shrink_backups(job_id,source_key,value,state) VALUES($id,$source,$value,'pending')";
        command.Parameters.AddWithValue("$id", item.Backup.Id);
        command.Parameters.AddWithValue("$source", item.Backup.SourceKey);
        command.Parameters.AddWithValue("$value", JsonSerializer.Serialize(item));
        command.ExecuteNonQuery();
    }

    internal void PublishShrinkBackup(StoredShrinkBackup item)
    {
        using var db = Open();
        using var transaction = db.BeginTransaction(deferred:false);
        using var command = db.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            UPDATE shrink_backups SET state='retired' WHERE source_key=$source AND state='current';
            """;
        command.Parameters.AddWithValue("$source", item.Backup.SourceKey);
        command.Parameters.AddWithValue("$id", item.Backup.Id);
        command.Parameters.AddWithValue("$value", JsonSerializer.Serialize(item));
        command.ExecuteNonQuery();
        command.CommandText = "UPDATE shrink_backups SET state='current',value=$value WHERE job_id=$id AND state='pending'";
        if (command.ExecuteNonQuery() != 1) throw new InvalidOperationException("Копия для публикации не зарегистрирована.");
        transaction.Commit();
    }

    internal StoredShrinkBackup[] GetShrinkBackupsToClean()
    {
        using var db = Open();
        using var command = db.CreateCommand();
        command.CommandText = """
            SELECT b.value FROM shrink_backups b JOIN workflow_jobs build ON build.id=b.job_id
            WHERE b.state<>'current' AND build.status NOT IN ('Queued','Running')
            """;
        using var reader = command.ExecuteReader();
        var backups = new List<StoredShrinkBackup>();
        while (reader.Read()) backups.Add(JsonSerializer.Deserialize<StoredShrinkBackup>(reader.GetString(0))!);
        return [.. backups];
    }

    internal void ForgetShrinkBackup(string id)
    {
        using var db = Open();
        using var command = db.CreateCommand();
        command.CommandText = "DELETE FROM shrink_backups WHERE job_id=$id AND state<>'current'";
        command.Parameters.AddWithValue("$id", id);
        command.ExecuteNonQuery();
    }

    internal WorkflowJob[] GetLatestShrinkBuilds() => GetJobsInternal("""
        WHERE kind='shrink-build' AND rowid=(
            SELECT b.rowid FROM workflow_jobs b WHERE b.kind='shrink-build' AND b.source_key=workflow_jobs.source_key
            ORDER BY b.created_at DESC LIMIT 1
        )
        """, null);
}
