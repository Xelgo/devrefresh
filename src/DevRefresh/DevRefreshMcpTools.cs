using System.ComponentModel;
using ModelContextProtocol;
using ModelContextProtocol.Server;
using OpenIddict.Abstractions;

namespace DevRefresh;

[McpServerToolType]
public sealed class DevRefreshMcpTools(IHttpContextAccessor accessor, Store store, IConfiguration configuration,
    RefreshOperations refresh, McpPlans plans, ISqlGateway gateway)
{
    private System.Security.Claims.ClaimsPrincipal Principal => accessor.HttpContext?.User ?? throw new McpException("Требуется OAuth-вход.");
    private AppUser User(bool restore = false)
    {
        var principal = Principal;
        if (!principal.HasScope(restore ? McpConfiguration.RestoreScope : McpConfiguration.ReadScope)) throw new McpException("Подключению не предоставлены права восстановления.");
        return McpOAuth.DomainUser(principal, store, configuration) ?? throw new McpException("Доменный доступ отключён.");
    }
    private string Owner => Principal.GetClaim(OpenIddictConstants.Claims.Subject) + "|" + Principal.GetAuthorizationId();
    private static async Task<object> Safe(Func<Task<object>> action)
    {
        try { return await action(); } catch (InputException error) { throw new McpException(error.Message); }
    }

    [McpServerTool(Name = "get_identity", ReadOnly = true, Destructive = false, OpenWorld = false, UseStructuredContent = true)]
    [Description("Показать подтверждённую доменную учётку и текущие права devRefresh. Учётка определяется OAuth-токеном и не задаётся аргументом.")]
    public object Identity()
    {
        var user = User(); return new { user.Name, user.IsAdmin, user.CanRefreshTestContour, CanRestore = Principal.HasScope(McpConfiguration.RestoreScope) };
    }

    [McpServerTool(Name = "list_databases", ReadOnly = true, Destructive = false, OpenWorld = false, UseStructuredContent = true)]
    [Description("Список доступных пользователю баз назначения. Для восстановления пользователь должен назвать полное имя базы.")]
    public Task<object> Databases(CancellationToken cancellationToken) => Safe(() => refresh.Databases(User(), cancellationToken));

    [McpServerTool(Name = "get_restore_options", ReadOnly = true, Destructive = false, OpenWorld = false, UseStructuredContent = true)]
    [Description("Доступные архивы Full, Shrink, dt, даты, размеры, активные соединения и базы-источники для указанной базы назначения.")]
    public Task<object> Options([Description("Полное имя базы назначения.")] string database, CancellationToken cancellationToken)
        => Safe(() => refresh.Options(database, User(), cancellationToken));

    [McpServerTool(Name = "list_copy_sources", ReadOnly = true, Destructive = false, OpenWorld = false, UseStructuredContent = true)]
    [Description("Показать доступные базы других разработчиков того же типа. Если пользователь не назвал источник, обязательно спросить, какую именно базу копировать, даже если кандидат один.")]
    public Task<object> Sources(string database, CancellationToken cancellationToken) => Safe(async () =>
    {
        var parsed = refresh.Authorize(database, User());
        var settings = store.GetSettings() ?? throw new InputException("Подключение не настроено.");
        return new { Sources = WorkflowEndpoints.CopySources(await gateway.List(settings, cancellationToken), database, parsed.SourceKey).Select(x => new { x.Name, x.SizeBytes }) };
    });

    [McpServerTool(Name = "prepare_restore", ReadOnly = false, Destructive = false, OpenWorld = false, UseStructuredContent = true)]
    [Description("Подготовить план восстановления на 5 минут без изменения SQL-базы. mode: shrink, full, dt, developer_copy. При отсутствии Full пробует dt, при отсутствии dt — Full; ошибки чтения не считаются отсутствием. Для developer_copy source_database выбирает пользователь. Сообщить пользователю фактический источник, дату, замену формата и удаление базы для dt. Проверка места пока не реализована.")]
    public Task<object> Prepare(string database, string mode, CancellationToken cancellationToken, string? source_database = null) => Safe(() =>
        plans.Prepare(database, mode, source_database, User(true), Principal.GetClaim(OpenIddictConstants.Claims.Subject)!, Owner, cancellationToken));

    [McpServerTool(Name = "start_restore", ReadOnly = false, Destructive = true, Idempotent = true, OpenWorld = false, UseStructuredContent = true)]
    [Description("Запустить подготовленный план, заменив содержимое целевой базы. confirm_replace=true допустим только при явном запросе пользователя на замену. confirm_disconnect=true требует разрешения завершить соединения. request_id — UUID; повторный вызов с тем же планом возвращает то же задание. После начала ошибки не вызывают автоматическую попытку другого формата. Возвращает job id для get_job.")]
    public Task<object> Start(string plan_id, bool confirm_replace, bool confirm_disconnect, string request_id, CancellationToken cancellationToken)
        => Safe(() => plans.Start(plan_id, confirm_replace, confirm_disconnect, request_id, User(true), Owner, cancellationToken));

    [McpServerTool(Name = "get_job", ReadOnly = true, Destructive = false, OpenWorld = false, UseStructuredContent = true)]
    [Description("Получить состояние задания, текущий этап и итог. Проверяются те же права просмотра, что в интерфейсе. Запущенное задание продолжает работу при закрытии чата или истечении токена.")]
    public object Job(string job_id)
    {
        var user = User(); var job = store.GetJob(job_id);
        if (job is null || !WorkflowRules.CanRead(job, user)) throw new McpException("Задание не найдено или недоступно.");
        return job.Public(job.Status is "Succeeded" or "Failed" or "Interrupted" ? store.GetExecutionReport(job_id) : null);
    }
}
