using System.Text.Json;
using Microsoft.Data.SqlClient;

namespace DevRefresh;

internal sealed record DevAnalysisInput(string? Database);
internal sealed record DevAnalysisSummary(string Id, string Database, DateTimeOffset AnalyzedAt);
internal sealed record DevAnalysisTable(string LogicalName, string Schema, string Table, string Kind,
    long Rows, decimal DataMB, decimal IndexMB, decimal UsedMB, decimal ReservedMB);
internal sealed record DevAnalysisReport(string Id, string Database, DateTimeOffset AnalyzedAt,
    string Configuration, string ConfigurationVersion, WorkflowFileSize FileSize,
    DevAnalysisTable[] Tables, string[] Warnings);

internal sealed class DevDatabaseAnalysis(Store store, ISqlGateway gateway, RestoreSql sql, StructureAnalyzer analyzer,
    IWebHostEnvironment environment, IConfiguration configuration, ILogger<DevDatabaseAnalysis> logger)
{
    private readonly string directory = Path.Combine(
        Path.GetFullPath(configuration["DataDirectory"] ?? "App_Data", environment.ContentRootPath), "dev-analyses");
    private readonly SemaphoreSlim running = new(1);

    public async Task<IResult> Catalog(CancellationToken token)
    {
        var reports = new List<DevAnalysisSummary>();
        if (Directory.Exists(directory))
        {
            foreach (var file in Directory.EnumerateFiles(directory, "*.json"))
            {
                token.ThrowIfCancellationRequested();
                if (Guid.TryParseExact(Path.GetFileNameWithoutExtension(file), "N", out var id)
                    && await Read<DevAnalysisSummary>(id, token) is { } report)
                    reports.Add(report);
            }
        }

        string? databaseError = null;
        string[] databases = [];
        if (store.GetSettings() is { } settings)
        {
            try
            {
                databases = (await gateway.List(settings, token)).Where(IsDevDatabase)
                    .Select(database => database.Name).Order(StringComparer.OrdinalIgnoreCase).ToArray();
            }
            catch (SqlServiceException error) { databaseError = error.Message; }
        }
        else databaseError = "Подключение к SQL Server не настроено.";

        return Results.Ok(new { Databases = databases, Reports = reports.OrderByDescending(report => report.AnalyzedAt), DatabaseError = databaseError });
    }

    public async Task<DevAnalysisReport> Analyze(string? database, CancellationToken token)
    {
        if (string.IsNullOrWhiteSpace(database) || Rules.ParseDatabase(database) is not { } parsed)
            throw new InputException("Выберите Dev-базу для анализа.");
        if (!await running.WaitAsync(0, token))
            throw new InputException("Анализ Dev-базы уже выполняется. Дождитесь его завершения.");

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromMinutes(2));
        try
        {
            var settings = store.GetSettings() ?? throw new InputException("Подключение к SQL Server не настроено.");
            var name = (await gateway.List(settings, timeout.Token)).FirstOrDefault(entry => IsDevDatabase(entry) &&
                entry.Name.Equals(database, StringComparison.OrdinalIgnoreCase))?.Name
                ?? throw new InputException("Dev-база не найдена или недоступна.");
            var id = Guid.NewGuid().ToString("N");
            var analysis = await analyzer.Analyze(settings, name, id, parsed.SourceKey, "", timeout.Token, liveDatabase: true);
            WorkflowFileSize fileSize;
            await using (var connection = await sql.Open(settings, name, timeout.Token))
                fileSize = await RestoreSql.ReadFileSize(connection, "Размер файлов базы", timeout.Token);
            var report = new DevAnalysisReport(id, name, DateTimeOffset.UtcNow, analysis.Configuration,
                analysis.ConfigurationVersion, fileSize,
                analysis.Tables.Select(table => new DevAnalysisTable(table.LogicalName, table.Schema, table.Table,
                    table.Kind, table.Rows, table.DataMB, table.IndexMB, table.UsedMB, table.ReservedMB)).ToArray(),
                analysis.Warnings);

            Directory.CreateDirectory(directory);
            var temporary = Path.Combine(directory, id + ".tmp");
            try
            {
                await using (var stream = File.Create(temporary))
                    await JsonSerializer.SerializeAsync(stream, report, cancellationToken: timeout.Token);
                timeout.Token.ThrowIfCancellationRequested();
                File.Move(temporary, ReportPath(Guid.ParseExact(id, "N")));
            }
            finally { File.Delete(temporary); }
            return report;
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested && timeout.IsCancellationRequested)
        {
            throw new InputException("Анализ Dev-базы не завершился за две минуты. Попробуйте ещё раз.");
        }
        catch (SqlException error)
        {
            logger.LogWarning(error, "SQL error while analyzing Dev database {Database}", database);
            throw new SqlServiceException("Не удалось прочитать структуру Dev-базы. Проверьте её доступность и права подключения.", error.Number);
        }
        finally { running.Release(); }
    }

    public async Task<T?> Read<T>(Guid id, CancellationToken token)
    {
        try
        {
            await using var stream = File.Open(ReportPath(id), FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
            return await JsonSerializer.DeserializeAsync<T>(stream, cancellationToken: token);
        }
        catch (FileNotFoundException) { return default; }
        catch (DirectoryNotFoundException) { return default; }
        catch (JsonException error)
        {
            logger.LogWarning(error, "Could not read Dev database analysis report {ReportId}", id);
            return default;
        }
    }

    public void Delete(Guid id)
    {
        if (Directory.Exists(directory)) File.Delete(ReportPath(id));
    }

    private string ReportPath(Guid id) => Path.Combine(directory, id.ToString("N") + ".json");

    private bool IsDevDatabase(SqlDatabaseInfo database)
    {
        if (database.IsSnapshot || database.State != "ONLINE" || Rules.ParseDatabase(database.Name) is null) return false;
        var allowed = configuration.GetSection("TestContour:AllowedTargets").Get<string[]>();
        return allowed is not { Length: > 0 } || allowed.Contains(database.Name, StringComparer.OrdinalIgnoreCase);
    }
}
