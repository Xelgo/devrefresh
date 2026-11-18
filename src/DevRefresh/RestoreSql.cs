using System.Data;
using System.Diagnostics;
using System.Globalization;
using Microsoft.Data.SqlClient;

namespace DevRefresh;

public sealed class RestoreSql(SettingsService settings)
{
    internal const int LongCommandTimeoutSeconds = 6 * 60 * 60;

    public static string Q(string value)=>"["+value.Replace("]","]]")+ "]";
    public static string L(string value)=>"N'"+value.Replace("'","''")+"'";
    public string ConnectionString(SqlSettings value,string database="master")
    {
        var builder=new SqlConnectionStringBuilder(settings.ConnectionString(value))
            {InitialCatalog=database,Pooling=false,ConnectTimeout=15,CommandTimeout=LongCommandTimeoutSeconds};
        return builder.ConnectionString;
    }
    public async Task<SqlConnection> Open(SqlSettings value,string database="master",CancellationToken token=default)
    {var cn=new SqlConnection(ConnectionString(value,database));try{await cn.OpenAsync(token);return cn;}catch{await cn.DisposeAsync();throw;}}
    public static async Task Execute(SqlConnection cn,string sql,CancellationToken token=default,SqlTransaction? tx=null)
    {
        await using var cmd = new SqlCommand(sql, cn, tx) { CommandTimeout = LongCommandTimeoutSeconds };
        await cmd.ExecuteNonQueryAsync(token);
    }
    public static async Task<T> Scalar<T>(SqlConnection cn, string sql, CancellationToken token = default,
        SqlTransaction? tx = null, int timeoutSeconds = LongCommandTimeoutSeconds)
    {
        await using var cmd = new SqlCommand(sql, cn, tx) { CommandTimeout = timeoutSeconds };
        return (T)(await cmd.ExecuteScalarAsync(token))!;
    }
    public static async Task<List<Dictionary<string,object?>>> Query(SqlConnection cn, string sql, CancellationToken token = default,
        SqlTransaction? tx = null, int timeoutSeconds = LongCommandTimeoutSeconds)
    {
        await using var cmd=new SqlCommand(sql,cn,tx){CommandTimeout=timeoutSeconds};await using var r=await cmd.ExecuteReaderAsync(token);var rows=new List<Dictionary<string,object?>>();
        while(await r.ReadAsync(token)){var row=new Dictionary<string,object?>(StringComparer.OrdinalIgnoreCase);for(var i=0;i<r.FieldCount;i++)row[r.GetName(i)]=r.IsDBNull(i)?null:r.GetValue(i);rows.Add(row);}return rows;
    }
    public static string WorkPath(string directory,string file)=>Path.Combine(directory,"DevRefreshWork",file);
    public BackupSource OpenBackupSource(SqlSettings value,string filename,CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if(Path.GetFileName(filename)!=filename || filename.Any(char.IsControl) || !filename.EndsWith(".bak",StringComparison.OrdinalIgnoreCase))
            throw new InputException("Выберите имя .bak из каталога резервных копий.");
        var root=Path.GetFullPath(value.BackupDirectory);var path=Path.GetFullPath(Path.Combine(root,filename));
        if(!path.StartsWith(root.TrimEnd(Path.DirectorySeparatorChar)+Path.DirectorySeparatorChar,StringComparison.OrdinalIgnoreCase))throw new InputException("Недопустимый путь бэкапа.");
        var info=new FileInfo(path);
        if(!info.Exists || (info.Attributes&FileAttributes.ReparsePoint)!=0)throw new InputException("Резервный файл не найден либо является ссылкой.");
        var sqlPath=Path.Combine(value.SqlBackupDirectory,filename);
        return new(sqlPath,File.Open(path,FileMode.Open,FileAccess.Read,FileShare.Read));
    }

    internal static async Task<Dictionary<string, object?>> ReadFullBackupHeader(SqlConnection connection,
        string path, CancellationToken token)
    {
        var header = await Query(connection, $"RESTORE HEADERONLY FROM DISK={L(path)}", token);
        if (header.Count != 1 || Convert.ToInt32(header[0]["BackupType"]) != 1 || Convert.ToBoolean(header[0]["IsDamaged"]))
            throw new InputException("Нужен один полный неповреждённый резервный набор базы SQL Server.");
        return header[0];
    }

    public async Task<WorkflowFileSize> Restore(SqlSettings value,string database,string backup,string id,bool replace,CancellationToken token,
        SqlConnection? connection=null,bool disconnect=false,Action<string>? progress=null,
        Action<DateTime>? backupFinished=null,Action? replacing=null)
    {
        if(replace ? Rules.ParseDatabase(database) is null : database!="DevRefreshStage_"+id)
            throw new InputException("Имя базы не допускает восстановление.");
        await using var ownedConnection=connection is null?await Open(value,token:token):null;
        var cn=connection??ownedConnection!;
        progress?.Invoke("Чтение заголовка резервной копии");
        var header=await ReadFullBackupHeader(cn,backup,token);
        if (header.GetValueOrDefault("BackupFinishDate") is DateTime finished) backupFinished?.Invoke(finished);
        var checksum=Convert.ToBoolean(header["HasBackupChecksums"])?",CHECKSUM":"";
        progress?.Invoke("Проверка резервной копии SQL Server (RESTORE VERIFYONLY)");
        await Execute(cn,$"RESTORE VERIFYONLY FROM DISK={L(backup)}"+(checksum.Length>0?" WITH CHECKSUM":""),token);
        progress?.Invoke("Подготовка файлов восстановления");
        var files=await Query(cn,$"RESTORE FILELISTONLY FROM DISK={L(backup)}",token);
        if(files.Count==0 || files.Any(x=>x["Type"]?.ToString() is not("D" or "L")))throw new InputException("FILESTREAM и нестандартные типы файлов пока не поддерживаются.");
        if(!replace && await Scalar<int>(cn,$"SELECT COUNT(*) FROM sys.databases WHERE name={L(database)}",token)>0)throw new InputException("Служебная база уже существует.");
        var paths=(await Query(cn,"SELECT CONVERT(nvarchar(4000),SERVERPROPERTY('InstanceDefaultDataPath')) AS d,CONVERT(nvarchar(4000),SERVERPROPERTY('InstanceDefaultLogPath')) AS l",token)).Single();
        var existing=replace?await Query(cn,$"SELECT name,physical_name,type FROM sys.master_files WHERE database_id=DB_ID({L(database)}) ORDER BY file_id",token):[];
        var usedPaths=new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var moves=new List<string>();var n=0;
        foreach(var file in files)
        {
            var log=file["Type"]?.ToString()=="L";var folder=paths[log?"l":"d"]?.ToString();
            if(string.IsNullOrWhiteSpace(folder))throw new InputException("SQL Server не сообщил каталог файлов данных/журнала.");
            var matching=existing.FirstOrDefault(x=>Convert.ToInt32(x["type"])==(log?1:0)
                && !usedPaths.Contains((string)x["physical_name"]!) && (string)x["name"]! == (string)file["LogicalName"]!)
                ??existing.FirstOrDefault(x=>Convert.ToInt32(x["type"])==(log?1:0) && !usedPaths.Contains((string)x["physical_name"]!));
            var full=matching?["physical_name"]?.ToString()??Path.Combine(folder,$"{database}_{id}_{n++}{(log?".ldf":".mdf")}");
            usedPaths.Add(full);
            moves.Add($"MOVE {L((string)file["LogicalName"]!)} TO {L(full)}");
        }
        // MULTI_USER is an ALTER DATABASE option, never a RESTORE option.
        var restore=$"RESTORE DATABASE {Q(database)} FROM DISK={L(backup)} WITH {string.Join(",",moves)},RECOVERY,RESTRICTED_USER{(replace?",REPLACE":"")}{checksum}";
        if(replace)restore=$"ALTER DATABASE {Q(database)} SET SINGLE_USER"+(disconnect?" WITH ROLLBACK IMMEDIATE":" WITH NO_WAIT")+"; "+restore;
        progress?.Invoke(replace ? "Восстановление бэкапа непосредственно в целевую базу" : "Восстановление временной базы для анализа");
        if (replace) replacing?.Invoke();
        await Execute(cn,restore,token);
        if (replace)
        {
            progress?.Invoke("Установка модели восстановления SIMPLE");
            await Execute(cn,$"ALTER DATABASE {Q(database)} SET RECOVERY SIMPLE",token);
        }
        else
        {
            await using var stage=await Open(value,database,token);
            await Execute(stage,$"EXEC sys.sp_addextendedproperty @name=N'DevRefresh.Resource',@value={L(id)}",token);
        }
        return new("По метаданным бэкапа",
            files.Where(f=>f["Type"]?.ToString()=="D").Sum(f=>Convert.ToInt64(f["Size"])),null,
            files.Where(f=>f["Type"]?.ToString()=="L").Sum(f=>Convert.ToInt64(f["Size"])),null,null);
    }
    public async Task DropOwned(SqlSettings value,string database,string id,CancellationToken token,bool registered=false)
    {
        if(!Guid.TryParseExact(id,"N",out _)||database!="DevRefreshStage_"+id)throw new InputException("Отказ удаления незарегистрированной служебной базы.");
        await using var cn=await Open(value,token:token);
        if(await Scalar<int>(cn,$"SELECT COUNT(*) FROM sys.databases WHERE name={L(database)}",token)==0)return;
        var state=await Scalar<string>(cn,$"SELECT state_desc FROM sys.databases WHERE name={L(database)}",token);
        if(state=="ONLINE")
        {
            var marker=await Scalar<object>(cn,$"SELECT COALESCE((SELECT CONVERT(nvarchar(64),value) FROM {Q(database)}.sys.extended_properties WHERE class=0 AND name=N'DevRefresh.Resource'),N'')",token);
            if(marker.ToString()!=id && (!registered || marker.ToString()!=""))throw new InputException("Маркер служебной базы не совпал; удаление отменено.");
            await Execute(cn,$"ALTER DATABASE {Q(database)} SET SINGLE_USER WITH ROLLBACK IMMEDIATE",token);
        }
        else if(!registered)throw new InputException("Не подтверждён владелец незавершённой служебной базы.");
        await Execute(cn,$"DROP DATABASE {Q(database)}",token);
    }
    public async Task<int> Sessions(SqlSettings value,string database,CancellationToken token)
    {await using var cn=await Open(value,token:token);return await Scalar<int>(cn,$"SELECT COUNT(*) FROM sys.dm_exec_sessions WHERE database_id=DB_ID({L(database)}) AND is_user_process=1",token);}

    public static async Task<WorkflowFileSize> ReadFileSize(SqlConnection connection,string stage,CancellationToken token)
    {
        var row = (await Query(connection,"""
            DECLARE @logUsed bigint;
            BEGIN TRY
                SELECT @logUsed=used_log_space_in_bytes FROM sys.dm_db_log_space_usage;
            END TRY
            BEGIN CATCH
                IF ERROR_NUMBER() NOT IN (229,297,300) THROW;
            END CATCH;

            SELECT COALESCE(SUM(CASE WHEN type=0 THEN CONVERT(bigint,size)*8192 END),0) AS DataBytes,
                   CASE WHEN COUNT(CASE WHEN type=0 THEN 1 END)=COUNT(CASE WHEN type=0 THEN FILEPROPERTY(name,'SpaceUsed') END)
                        THEN SUM(CASE WHEN type=0 THEN CONVERT(bigint,FILEPROPERTY(name,'SpaceUsed'))*8192 END) END AS DataUsedBytes,
                   COALESCE(SUM(CASE WHEN type=1 THEN CONVERT(bigint,size)*8192 END),0) AS LogBytes,
                   @logUsed AS LogUsedBytes,
                   (SELECT log_reuse_wait_desc FROM sys.databases WHERE database_id=DB_ID()) AS LogReuseWait,
                   (SELECT recovery_model_desc FROM sys.databases WHERE database_id=DB_ID()) AS RecoveryModel
            FROM sys.database_files WHERE type IN (0,1)
            """,token,timeoutSeconds:10)).Single();
        return new(stage,(long)row["DataBytes"]!,row["DataUsedBytes"] as long?,(long)row["LogBytes"]!,
            row["LogUsedBytes"] as long?,row["LogReuseWait"] as string,row["RecoveryModel"] as string);
    }

    private const long BytesPerMiB = 1024L * 1024;
    private static readonly CultureInfo RussianCulture = CultureInfo.GetCultureInfo("ru-RU");

    private sealed record DataFile(int Id, long AllocatedBytes, int? UsedPages, bool CanShrink)
    {
        public int? TargetMiB => UsedPages is int used ? Math.Max(1,(int)Math.Ceiling(used*1.05m/128)) : null;
        public long? TargetBytes => TargetMiB * BytesPerMiB;
        public bool NeedsShrink => CanShrink && (TargetBytes is not long target || AllocatedBytes > target);
    }

    private static async Task<DataFile[]> ReadDataFiles(SqlConnection connection, CancellationToken token)
    {
        var rows = await Query(connection,"""
            SELECT f.file_id AS Id, f.size AS SizePages, FILEPROPERTY(f.name,'SpaceUsed') AS UsedPages,
                   CASE WHEN f.state=0 AND COALESCE(fg.is_read_only,0)=0 THEN 1 ELSE 0 END AS CanShrink
            FROM sys.database_files f
            LEFT JOIN sys.filegroups fg ON fg.data_space_id=f.data_space_id
            WHERE f.type=0
            ORDER BY f.file_id
            """,token,timeoutSeconds:10);

        return rows.Select(row => new DataFile(
            Convert.ToInt32(row["Id"]),
            Convert.ToInt64(row["SizePages"]) * 8192,
            row["UsedPages"] as int?,
            Convert.ToInt32(row["CanShrink"]) != 0)).ToArray();
    }

    private static async Task<DataFile[]> WaitForPageRelease(SqlConnection connection,DataFile[] files,long? expectedUsedBytes,
        Action<DataFile[]> observe,Action<string,int,int,long> stopped,CancellationToken token)
    {
        const int maxSeconds=120;
        const int pagesPerMiB=128;
        var releaseMark=files.Sum(file=>(long)(file.UsedPages??0));
        var initialSeconds=Math.Clamp((int)Math.Ceiling(files.Sum(file=>file.AllocatedBytes)/(20d*1024*BytesPerMiB)),6,60);
        var clock=Stopwatch.StartNew();
        var lastRelease=TimeSpan.Zero;
        string reason;
        while(true)
        {
            var remaining=TimeSpan.FromSeconds(maxSeconds)-clock.Elapsed;
            if(remaining<=TimeSpan.Zero)
            {
                reason="maximum-wait";
                break;
            }
            await Task.Delay(remaining<TimeSpan.FromSeconds(2)?remaining:TimeSpan.FromSeconds(2),token);
            files=await ReadDataFiles(connection,token);
            observe(files);
            if(files.Any(file=>file.CanShrink && file.UsedPages is null))
            {
                reason="usage-unavailable";
                break;
            }
            var used=files.Sum(file=>(long)(file.UsedPages??0));
            var elapsed=clock.Elapsed;
            if(releaseMark-used>=pagesPerMiB)
            {
                releaseMark=used;
                lastRelease=elapsed;
            }
            if(elapsed.TotalSeconds<initialSeconds)continue;

            var expectedPending=expectedUsedBytes is long expected && used*8192>expected;
            // A fixed size-based delay can expire while SQL Server is still returning many pages.
            var idleSeconds=lastRelease!=TimeSpan.Zero?8:expectedPending?12:0;
            if((elapsed-lastRelease).TotalSeconds<idleSeconds)continue;
            reason=lastRelease!=TimeSpan.Zero?"release-settled":expectedPending?"no-release-observed":"initial-wait-complete";
            break;
        }
        stopped(reason,initialSeconds,maxSeconds,clock.ElapsedMilliseconds);
        return files;
    }

    public async Task<WorkflowFileOptimization> OptimizeFiles(SqlSettings settings,SqlConnection connection,
        Action<string> progress,Action<string> liveProgress,
        CancellationToken token,bool deletedRows=false,long? expectedUsedBytes=null,Action<WorkflowFileSize>? recordSize=null,
        ILogger? logger=null,string? jobId=null,bool lowPriority=false)
    {
        var shrinkOptions=lowPriority ? "WAIT_AT_LOW_PRIORITY (ABORT_AFTER_WAIT = SELF), NO_INFOMSGS" : "NO_INFOMSGS";
        const int logTargetMiB = 1024;
        const long minimumRetrySavingsBytes = 256 * BytesPerMiB;
        const string firstPass = "Освобождение места в файлах данных";
        const string secondPass = "Повторное освобождение места в файлах данных";
        var clock=Stopwatch.StartNew();
        void LogSample(string phase,DataFile[] files)
        {
            if(logger is null)return;
            var used=files.Any(file=>file.UsedPages is null)?(double?)null:files.Sum(file=>(long)file.UsedPages!.Value)/128d;
            var target=files.Any(file=>file.CanShrink && file.TargetBytes is null)?(double?)null:
                files.Sum(file=>file.CanShrink?Math.Min(file.AllocatedBytes,file.TargetBytes!.Value):file.AllocatedBytes)/(double)BytesPerMiB;
            logger.LogInformation("SQL shrink {Phase} for job {JobId} on {Database}. Elapsed {ElapsedMs} ms, allocated {AllocatedMiB} MiB, used {UsedMiB} MiB, calculated target {TargetMiB} MiB, expected occupied ceiling {ExpectedUsedMiB} MiB",
                phase,jobId,connection.Database,clock.ElapsedMilliseconds,files.Sum(file=>file.AllocatedBytes)/(double)BytesPerMiB,
                used,target,expectedUsedBytes/(double)BytesPerMiB);
        }
        var dataFiles = await ReadDataFiles(connection, token);
        LogSample("after-delete",dataFiles);
        if(deletedRows && dataFiles.Any(file=>file.CanShrink && file.UsedPages.HasValue))
        {
            progress("Ожидание освобождения страниц после удаления");
            await Execute(connection,"CHECKPOINT",token);
            dataFiles=await ReadDataFiles(connection,token);
            LogSample("after-checkpoint",dataFiles);
            dataFiles=await WaitForPageRelease(connection,dataFiles,expectedUsedBytes,
                files=>LogSample("waiting",files),
                (reason,initial,max,elapsed)=>logger?.LogInformation(
                    "SQL shrink wait completed for job {JobId} on {Database}. Reason {WaitReason}, initial {InitialWaitSeconds}s, limit {MaxWaitSeconds}s, waited {WaitMs} ms",
                    jobId,connection.Database,reason,initial,max,elapsed),token);
        }
        LogSample("before-first-pass",dataFiles);
        if(recordSize is not null)
            recordSize(await ReadFileSize(connection,"Перед первым сжатием файлов данных",token));
        var targets = new Dictionary<int, long?>();
        progress(firstPass);
        var fileCount = dataFiles.Count(file=>file.NeedsShrink);
        var fileNumber = 0;
        foreach (var file in dataFiles)
        {
            if (!file.CanShrink)
            {
                targets[file.Id] = file.AllocatedBytes;
                continue;
            }
            if (file.TargetBytes is long targetBytes)
            {
                targets[file.Id] = Math.Min(file.AllocatedBytes, targetBytes);
            }
            else
            {
                targets[file.Id] = null;
            }
            if (file.NeedsShrink)
            {
                var target = file.TargetMiB?.ToString(CultureInfo.InvariantCulture) ?? "TRUNCATEONLY";
                await ShrinkDataFile(settings,connection,$"DBCC SHRINKFILE({file.Id},{target}) WITH {shrinkOptions}",
                    firstPass,++fileNumber,fileCount,liveProgress,logger,jobId,token);
            }
        }

        var refreshedFiles = await ReadDataFiles(connection, token);
        LogSample("after-first-pass",refreshedFiles);
        if(recordSize is not null)
            recordSize(await ReadFileSize(connection,"После первого сжатия файлов данных",token));
        var retryFiles = refreshedFiles.Where(file =>
        {
            var minimumSavings=Math.Max(minimumRetrySavingsBytes,file.AllocatedBytes/100);
            return file.CanShrink && file.TargetBytes is long targetBytes &&
                targets.TryGetValue(file.Id,out var previousTarget) &&
                file.AllocatedBytes-targetBytes>=minimumSavings &&
                (previousTarget is null || previousTarget-targetBytes>=minimumSavings);
        }).ToArray();
        if (retryFiles.Length > 0)
        {
            logger?.LogInformation("SQL shrink second pass for job {JobId} on {Database}. Lower target and reclaimable space above the per-file minimum of {MinimumSavingsMiB} MiB or one percent. Files {FileIds}",
                jobId,connection.Database,minimumRetrySavingsBytes/BytesPerMiB,retryFiles.Select(file=>file.Id).ToArray());
            progress(secondPass);
            fileNumber = 0;
            foreach (var file in retryFiles)
            {
                await ShrinkDataFile(settings,connection,$"DBCC SHRINKFILE({file.Id},{file.TargetMiB}) WITH {shrinkOptions}",
                    secondPass,++fileNumber,retryFiles.Length,liveProgress,logger,jobId,token);
                targets[file.Id] = file.TargetBytes;
            }
            LogSample("after-second-pass",await ReadDataFiles(connection,token));
        }
        else logger?.LogInformation("SQL shrink second pass skipped for job {JobId} on {Database}. The target or reclaimable space did not change enough.",
            jobId,connection.Database);

        progress("Контрольная точка перед освобождением журнала");
        await Execute(connection,"CHECKPOINT",token);
        progress("Освобождение места в журнале транзакций");
        var logFiles = await Query(connection,"""
            SELECT file_id AS Id,size AS SizePages,state AS State
            FROM sys.database_files WHERE type=1 ORDER BY file_id
            """,token,timeoutSeconds:10);
        long logTargetBytes = 0;
        foreach (var file in logFiles)
        {
            var allocatedBytes = Convert.ToInt64(file["SizePages"])*8192;
            var targetBytes = logTargetMiB * BytesPerMiB;
            if (Convert.ToInt32(file["State"])!=0)
            {
                logTargetBytes += allocatedBytes;
                continue;
            }
            logTargetBytes += Math.Min(allocatedBytes,targetBytes);
            if (allocatedBytes > targetBytes)
            {
                var id = Convert.ToInt32(file["Id"]);
                await Execute(connection,$"DBCC SHRINKFILE({id},{logTargetMiB}) WITH {shrinkOptions}",token);
            }
        }
        long? dataTargetBytes = targets.Values.Contains(null) ? null : targets.Values.Sum();
        return new(dataTargetBytes,logTargetBytes);
    }

    private async Task ShrinkDataFile(SqlSettings settings,SqlConnection connection,string sql,string stage,
        int fileNumber,int fileCount,Action<string> liveProgress,ILogger? logger,string? jobId,CancellationToken token)
    {
        var label = fileCount > 1 ? $"{stage} · файл {fileNumber}/{fileCount}" : stage;
        if (fileCount > 1)
            liveProgress(label);
        var sessionId = connection.ServerProcessId;
        var database = connection.Database;
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(token);
        var observation = Observe();
        try
        {
            await Execute(connection,sql,token);
        }
        finally
        {
            await stop.CancelAsync();
            await observation;
        }

        async Task Observe()
        {
            var cancellation = stop.Token;
            try
            {
                // A separate session can query the worker while DBCC has the main connection busy.
                using var timer = new PeriodicTimer(TimeSpan.FromSeconds(2));
                if (!await timer.WaitForNextTickAsync(cancellation)) return;
                using var connectTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
                connectTimeout.CancelAfter(TimeSpan.FromSeconds(3));
                await using var observer = await Open(settings,token:connectTimeout.Token);
                await using var command = new SqlCommand("""
                    SELECT percent_complete FROM sys.dm_exec_requests
                    WHERE session_id=@session AND database_id=DB_ID(@database)
                    """,observer) { CommandTimeout=3 };
                command.Parameters.Add("@session",SqlDbType.Int).Value = sessionId;
                command.Parameters.Add("@database",SqlDbType.NVarChar,128).Value = database;
                string? last = null;
                do
                {
                    if (await command.ExecuteScalarAsync(cancellation) is float percent && percent is >= 0 and <= 100)
                    {
                        var displayed = percent.ToString("0.0",RussianCulture);
                        if (displayed != last && !cancellation.IsCancellationRequested)
                        {
                            liveProgress($"{label} · {displayed}%");
                            last = displayed;
                        }
                    }
                } while (await timer.WaitForNextTickAsync(cancellation));
            }
            catch (Exception) when (cancellation.IsCancellationRequested) { }
            catch (Exception error)
            {
                logger?.LogDebug(error,"SQL shrink progress unavailable for job {JobId} on {Database}, SPID {SqlSessionId}",
                    jobId,database,sessionId);
            }
        }
    }
}
