namespace DevRefresh;

// Keep the original file open against replacement while SQL Server reads it.
public sealed class BackupSource(string sqlPath, FileStream stream) : IDisposable
{
    public string SqlPath { get; } = sqlPath;
    public void Dispose() => stream.Dispose();
}
