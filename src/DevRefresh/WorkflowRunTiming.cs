using System.Diagnostics;

namespace DevRefresh;

internal sealed class WorkflowRunTiming
{
    private sealed class Stage(string name,long started)
    {
        public string Name { get; } = name;
        public long Started { get; } = started;
        public Dictionary<(string Operation,string Object,string Table),TimeSpan> Tables { get; } = new();
    }

    private sealed class TableMeasurement(Stage stage,(string Operation,string Object,string Table) key) : IDisposable
    {
        private readonly long started = Stopwatch.GetTimestamp();

        public void Dispose()
        {
            stage.Tables[key] = stage.Tables.GetValueOrDefault(key) + Stopwatch.GetElapsedTime(started);
        }
    }

    private readonly long started = Stopwatch.GetTimestamp();
    private readonly long elapsedBeforeStart;
    private readonly List<WorkflowStageTiming> stages = [];
    private Stage current;
    private WorkflowExecutionReport? report;
    public List<WorkflowFileSize> FileSizes { get; } = [];
    public WorkflowFileOptimization? FileOptimization { get; set; }
    public BinaryDataCleanupReport? BinaryDataCleanup { get; set; }

    public WorkflowRunTiming(bool copy=false)
    {
        current = new(copy?"Проверка базы назначения и соединений":"Открытие резервного файла",started);
    }

    public WorkflowRunTiming(WorkflowExecutionReport previous,string stage)
    {
        var queueWait=Math.Max(0,Milliseconds(DateTimeOffset.UtcNow-previous.FinishedAt));
        elapsedBeforeStart=previous.DurationMs+queueWait;
        stages.AddRange(previous.Stages);
        stages.Add(new("Ожидание фонового освобождения места",queueWait,[]));
        FileSizes.AddRange(previous.FileSizes);
        FileOptimization=previous.FileOptimization;
        BinaryDataCleanup=previous.BinaryDataCleanup;
        current=new(stage,started);
    }

    public void ChangeStage(string name)
    {
        if (current.Name == name) return;
        var now = Stopwatch.GetTimestamp();
        RecordStage(now);
        current = new(name,now);
    }

    public IDisposable MeasureTable(string operation,AnalyzedTable table) =>
        new TableMeasurement(current,(operation,table.LogicalName,$"{table.Schema}.{table.Table}"));

    public WorkflowExecutionReport Finish()
    {
        if (report is not null) return report;
        var now = Stopwatch.GetTimestamp();
        RecordStage(now);
        return report = new(DateTimeOffset.UtcNow,elapsedBeforeStart+Milliseconds(Stopwatch.GetElapsedTime(started,now)),
            [..stages],[..FileSizes],FileOptimization,BinaryDataCleanup);
    }

    private void RecordStage(long end)
    {
        stages.Add(new(current.Name,Milliseconds(Stopwatch.GetElapsedTime(current.Started,end)),
            current.Tables.Select(item=>new WorkflowTableTiming(item.Key.Operation,item.Key.Object,item.Key.Table,
                Milliseconds(item.Value))).ToArray()));
    }

    private static long Milliseconds(TimeSpan time) => (long)time.TotalMilliseconds;
}
