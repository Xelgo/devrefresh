using System.Net;
using System.Text.Json;
using System.Threading.RateLimiting;
using DevRefresh;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.Negotiate;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.RateLimiting;
using Serilog;
using Serilog.Events;
using Serilog.Formatting.Json;

if(!Microsoft.Extensions.Hosting.WindowsServices.WindowsServiceHelpers.IsWindowsService())
    Console.OutputEncoding = new System.Text.UTF8Encoding(false);
var publishedRoot = Directory.Exists(Path.Combine(AppContext.BaseDirectory, "wwwroot")) ? AppContext.BaseDirectory : null;
var bootstrapArguments=new ConfigurationBuilder().AddCommandLine(args).Build();
var builder = WebApplication.CreateBuilder(new WebApplicationOptions { Args = args,
    ContentRootPath = bootstrapArguments["contentRoot"]??publishedRoot });
var serverConfiguration = builder.Configuration["ServerConfiguration"];
if (serverConfiguration is null && publishedRoot is not null)
{
    var portableConfiguration = Path.GetFullPath(Path.Combine(publishedRoot, "../config/server.json"));
    if (File.Exists(portableConfiguration)) serverConfiguration = portableConfiguration;
}
if (serverConfiguration is not null)
    builder.Configuration.AddJsonFile(Path.GetFullPath(serverConfiguration), optional: false, reloadOnChange: false)
        .AddEnvironmentVariables(prefix: "DEVREFRESH_").AddCommandLine(args);
var dataPath = Path.GetFullPath(builder.Configuration["DataDirectory"] ?? "App_Data", builder.Environment.ContentRootPath);
if (builder.Configuration["Maintenance"] is { } maintenance)
{
    try
    {
        object result;
        if (maintenance == "migrate")
        {
            using var maintenanceLock = RuntimeDataLock.Acquire(dataPath);
            result = DatabaseMigrator.Ensure(Path.Combine(dataPath, "devrefresh.sqlite"));
        }
        else if(maintenance=="prepare-service")
        {
            using var maintenanceLock=RuntimeDataLock.Acquire(dataPath);
            result=ServiceKeyPreparation.Prepare(dataPath);
        }
        else if (maintenance is "update" or "rollback")
        {
            var packageRoot = Path.GetFullPath(Path.Combine(publishedRoot ?? AppContext.BaseDirectory, ".."));
            var target = builder.Configuration["UpdateTarget"] ?? throw new InvalidOperationException("Не задана папка установленного сервиса.");
            if (!Path.GetFullPath(builder.Environment.ContentRootPath).TrimEnd(Path.DirectorySeparatorChar)
                .Equals(Path.Combine(Path.GetFullPath(target), "app").TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("contentRoot должен указывать на папку app обновляемой установки.");
            result = maintenance == "update" ? UpdateEngine.Apply(packageRoot, target, dataPath)
                : UpdateEngine.Rollback(packageRoot, target, dataPath,
                    builder.Configuration["UpdateBackup"] ?? throw new InvalidOperationException("Не задана папка резервной копии."));
        }
        else throw new InvalidOperationException("Неизвестный режим обслуживания.");
        Console.WriteLine(JsonSerializer.Serialize(result));
    }
    catch (Exception ex)
    {
        Console.Error.WriteLine("DevRefresh maintenance: " + ex.Message);
        Environment.ExitCode = 1;
    }
    return;
}
builder.Services.AddWindowsService(options=>options.ServiceName=builder.Configuration["WindowsService:Name"]??"DevRefresh");
builder.Services.Configure<HostOptions>(options=>options.ShutdownTimeout=TimeSpan.FromSeconds(120));
using var runtimeDataLock = RuntimeDataLock.Acquire(dataPath);
if (builder.Configuration["HttpsCertificate:Thumbprint"] is { Length: > 0 })
{
    var certificate = DeploymentCertificate.Load(builder.Configuration);
    builder.WebHost.ConfigureKestrel(options => options.ConfigureHttpsDefaults(https => https.ServerCertificate = certificate));
}
builder.WebHost.ConfigureKestrel(options => options.Limits.MaxRequestBodySize = 1024 * 1024);
var logPath = Path.GetFullPath(builder.Configuration["LogDirectory"] ?? Path.Combine(dataPath, "logs"), builder.Environment.ContentRootPath);
Directory.CreateDirectory(dataPath);
Directory.CreateDirectory(logPath);
var logConfiguration = new LoggerConfiguration().MinimumLevel.Information()
    .MinimumLevel.Override("Microsoft.AspNetCore", LogEventLevel.Warning).Enrich.FromLogContext()
    .WriteTo.File(new JsonFormatter(renderMessage: true), Path.Combine(logPath, "devrefresh-.jsonl"),
        rollingInterval: RollingInterval.Day, fileSizeLimitBytes: 10 * 1024 * 1024,
        rollOnFileSizeLimit: true, retainedFileCountLimit: null, shared: true);
if (builder.Configuration.GetValue("FileLogging:ConsoleEnabled", true)) logConfiguration.WriteTo.Console();
builder.Logging.ClearProviders();
var applicationLogger = logConfiguration.CreateLogger();
builder.Services.AddSerilog(applicationLogger, dispose: true);
var protection = builder.Services.AddDataProtection().SetApplicationName("DevRefresh")
    .PersistKeysToFileSystem(new DirectoryInfo(Path.Combine(dataPath, "keys")));
if (OperatingSystem.IsWindows()) protection.ProtectKeysWithDpapi(protectToLocalMachine:
    Microsoft.Extensions.Hosting.WindowsServices.WindowsServiceHelpers.IsWindowsService()
    || File.Exists(Path.Combine(dataPath,ServiceKeyPreparation.Marker)));
builder.Services.AddSingleton(new Store(Path.Combine(dataPath, "devrefresh.sqlite")));
builder.Services.AddHostedService(sp => new LogRetentionService(logPath, sp.GetRequiredService<Store>(),
    sp.GetRequiredService<ILogger<LogRetentionService>>()));
builder.Services.AddSingleton<SessionService>();
builder.Services.AddSingleton<SettingsService>();
builder.Services.AddSingleton<ISqlGateway, SqlGateway>();
builder.Services.AddSingleton<RestoreSql>();
builder.Services.AddSingleton<StructureAnalyzer>();
builder.Services.AddSingleton<DevDatabaseAnalysis>();
builder.Services.AddSingleton<ReductionExecutor>();
builder.Services.AddSingleton<WorkflowService>();
builder.Services.AddHostedService(sp=>sp.GetRequiredService<WorkflowService>());
builder.Services.AddSingleton<WebUpdates>();
builder.Services.AddSingleton<IPasswordHasher<LocalAdmin>, PasswordHasher<LocalAdmin>>();
builder.Services.Configure<PasswordHasherOptions>(options => options.IterationCount = 210_000);
builder.Services.ConfigureHttpJsonOptions(options => options.SerializerOptions.RespectNullableAnnotations = true);
builder.Services.AddAntiforgery(options =>
{
    options.HeaderName = "X-CSRF-TOKEN";
    options.Cookie.Name = "DevRefresh.Csrf";
    options.Cookie.SameSite = SameSiteMode.Strict;
    options.Cookie.HttpOnly = true;
    options.Cookie.SecurePolicy = CookieSecurePolicy.SameAsRequest;
});
var auth = builder.Services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
    .AddCookie(options =>
    {
        options.Cookie.Name = "DevRefresh.Session";
        options.Cookie.HttpOnly = true;
        options.Cookie.SameSite = SameSiteMode.Strict;
        options.Cookie.SecurePolicy = CookieSecurePolicy.SameAsRequest;
        options.ExpireTimeSpan = TimeSpan.FromHours(8);
        options.SlidingExpiration = false;
        options.Events.OnRedirectToLogin = context => { context.Response.StatusCode = 401; return Task.CompletedTask; };
        options.Events.OnRedirectToAccessDenied = context => { context.Response.StatusCode = 403; return Task.CompletedTask; };
    });
auth.AddNegotiate();
builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = 429;
    options.AddPolicy("login", context => RateLimitPartition.GetFixedWindowLimiter(
        context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
        _ => new FixedWindowRateLimiterOptions { PermitLimit = 5, Window = TimeSpan.FromMinutes(1), QueueLimit = 0 }));
});

var app = builder.Build();
TestContourBootstrap.Initialize(app);
app.Lifetime.ApplicationStopped.Register(runtimeDataLock.Dispose);
app.UseSerilogRequestLogging(options =>
{
    options.Logger = applicationLogger;
    options.MessageTemplate = "HTTP {RequestMethod} {RequestPath} -> {StatusCode} ({Elapsed:0.0} ms)";
    options.GetLevel = (context, _, exception) => exception is not null || context.Response.StatusCode >= 500
        ? LogEventLevel.Error : context.Response.StatusCode >= 400 ? LogEventLevel.Warning : LogEventLevel.Information;
    options.EnrichDiagnosticContext = (diagnostics, context) => diagnostics.Set("RequestId", context.TraceIdentifier);
});
app.Use(async (context, next) =>
{
    context.Response.Headers["X-Request-ID"] = context.TraceIdentifier;
    context.Response.Headers.CacheControl = "no-cache, no-store";
    context.Response.Headers.Pragma = "no-cache";
    context.Response.Headers.XContentTypeOptions = "nosniff";
    context.Response.Headers.XFrameOptions = "DENY";
    context.Response.Headers.ContentSecurityPolicy = "default-src 'self'; script-src 'self'; style-src 'self'; img-src 'self' data:; base-uri 'self'; frame-ancestors 'none'; form-action 'self'";
    context.Response.Headers["Referrer-Policy"] = "same-origin";
    if (!context.Request.IsHttps && !IsLocal(context))
    {
        context.Response.StatusCode = 400;
        await context.Response.WriteAsJsonAsync(new { error = "Для удалённого доступа требуется HTTPS." });
        return;
    }
    try { await next(context); }
    catch (InputException ex)
    {
        app.Logger.LogInformation("Запрос отклонён: {Reason}. RequestId {RequestId}", ex.Message, context.TraceIdentifier);
        context.Response.StatusCode = 400;
        await context.Response.WriteAsJsonAsync(new { error = ex.Message });
    }
    catch (SqlServiceException ex)
    {
        app.Logger.LogWarning("Ошибка SQL Server. SQL-код {SqlErrorNumber}. RequestId {RequestId}", ex.Number, context.TraceIdentifier);
        context.Response.StatusCode = 502;
        await context.Response.WriteAsJsonAsync(new { error = ex.Message, code = ex.Number, requestId = context.TraceIdentifier });
    }
    catch (AntiforgeryValidationException)
    {
        context.Response.StatusCode = 400;
        await context.Response.WriteAsJsonAsync(new { error = "Сессия формы устарела. Обновите страницу и повторите действие." });
    }
    catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested) { context.Response.StatusCode = 499; }
    catch (Exception ex)
    {
        app.Logger.LogError(ex, "Ошибка обработки запроса {Path}. RequestId {RequestId}", context.Request.Path, context.TraceIdentifier);
        context.Response.StatusCode = 500;
        await context.Response.WriteAsJsonAsync(new { error = "Не удалось выполнить действие. Подробности доступны администратору в журнале сервера.", requestId = context.TraceIdentifier });
    }
});
app.UseAuthentication();
app.Use(async (context, next) =>
{
    if (context.Request.Path.StartsWithSegments("/api/admin") || context.Request.Path == "/api/databases"
        || context.Request.Path.StartsWithSegments("/api/refresh") || context.Request.Path.StartsWithSegments("/api/jobs"))
    {
        var user = context.RequestServices.GetRequiredService<SessionService>().Current(context);
        if (user is null)
        {
            context.Response.StatusCode = 401;
            await context.Response.WriteAsJsonAsync(new { error = "Требуется вход в систему." });
            return;
        }
        if (context.Request.Path.StartsWithSegments("/api/admin") && !user.IsAdmin)
        {
            context.Response.StatusCode = 403;
            await context.Response.WriteAsJsonAsync(new { error = "Доступ разрешён только администратору." });
            return;
        }
    }
    if (context.Request.Path.StartsWithSegments("/api") && !HttpMethods.IsGet(context.Request.Method)
        && !HttpMethods.IsHead(context.Request.Method) && !HttpMethods.IsOptions(context.Request.Method))
        await context.RequestServices.GetRequiredService<IAntiforgery>().ValidateRequestAsync(context);
    await next(context);
});
app.UseRateLimiter();
app.UseStaticFiles();

app.MapGet("/health", () => Results.Ok(new { status = "ok", version = typeof(Program).Assembly.GetName().Version?.ToString() }));
app.MapGet("/api/session", (HttpContext context, Store store, SessionService sessions, IAntiforgery csrf) =>
    Results.Ok(new
    {
        setupRequired = store.GetAdmin() is null,
        setupAllowed = IsLocal(context),
        windowsEnabled = store.GetWindowsAccess(app.Configuration).Enabled,
        windowsAutoSignIn = store.GetWindowsAccess(app.Configuration) is {Enabled:true,AutoSignIn:true},
        user = sessions.Current(context),
        csrfToken = csrf.GetAndStoreTokens(context).RequestToken
    }));

app.MapPost("/api/setup", async (SetupInput input, HttpContext context, Store store, IPasswordHasher<LocalAdmin> hasher) =>
{
    if (!IsLocal(context)) return Results.Json(new { error = "Первичная настройка доступна только на сервере через loopback." }, statusCode: 403);
    if (store.GetAdmin() is not null) return Results.Conflict(new { error = "Первый администратор уже создан." });
    Rules.ValidateAdmin(input.Username, input.Password);
    var accounts = Rules.ValidateWindowsAdmins(input.WindowsAdmins);
    var admin = new LocalAdmin(input.Username, "", Guid.NewGuid().ToString("N"));
    admin = admin with { PasswordHash = hasher.HashPassword(admin, input.Password) };
    if (!store.Initialize(admin, accounts)) return Results.Conflict(new { error = "Первый администратор уже создан." });
    await SessionService.SignIn(context, admin.Username, "local", admin.Version);
    return Results.Ok(new { ok = true });
}).RequireRateLimiting("login");

app.MapPost("/api/login", async (LoginInput input, HttpContext context, Store store, IPasswordHasher<LocalAdmin> hasher) =>
{
    var admin = store.GetAdmin();
    if (input.Username.Length > 256 || input.Password.Length > 256)
        return Results.Json(new { error = "Неверное имя пользователя или пароль." }, statusCode: 401);
    // Perform password hashing even for an unknown username to reduce login-name timing differences.
    var valid = admin is not null && hasher.VerifyHashedPassword(admin, admin.PasswordHash, input.Password) != PasswordVerificationResult.Failed;
    if (!valid || !string.Equals(admin!.Username, input.Username, StringComparison.Ordinal))
    {
        store.Audit("anonymous", "login.failed", "Неудачная попытка локального входа.");
        return Results.Json(new { error = "Неверное имя пользователя или пароль." }, statusCode: 401);
    }
    await SessionService.SignIn(context, admin.Username, "local", admin.Version);
    store.Audit(admin.Username, "login.local", "Вход локального администратора.");
    return Results.Ok(new { ok = true });
}).RequireRateLimiting("login");

app.MapGet("/auth/windows", async (HttpContext context, Store store) =>
{
    if (!store.GetWindowsAccess(app.Configuration).Enabled) return Results.Redirect("/login?domain=disabled");
    if (store.GetAdmin() is null) return Results.Redirect("/setup");
    var result = await context.AuthenticateAsync(NegotiateDefaults.AuthenticationScheme);
    if (!result.Succeeded || result.Principal?.Identity?.IsAuthenticated != true)
        return Results.Challenge(new AuthenticationProperties(), [NegotiateDefaults.AuthenticationScheme]);
    var name = result.Principal.Identity.Name;
    if (name is null || !name.Contains('\\'))
        return Results.Json(new { error = "Не удалось определить доменное имя DOMAIN\\user." }, statusCode: 403);
    await SessionService.SignIn(context, name, "domain");
    store.Audit(name, "login.domain", "Вход с подтверждённой Windows-идентичностью.");
    return Results.Redirect("/");
});

app.MapPost("/api/logout", async (HttpContext context, SessionService sessions, Store store) =>
{
    var user = sessions.Current(context);
    if (user is not null) store.Audit(user.Name, "logout", "Выход из приложения.");
    await context.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
    return Results.Ok(new { ok = true });
});

app.MapGet("/api/admin/settings", (Store store) => Results.Ok(new
{
    connection = Rules.PublicSettings(store.GetSettings()),
    windowsAdmins = store.GetWindowsAdmins(),
    windowsAuthentication = store.GetWindowsAccess(app.Configuration),
    testContourOperators = store.GetWindowsAccess(app.Configuration).TestContourOperators ?? [],
    logRetentionDays = store.GetLogRetentionDays(),
    localUsername = store.GetAdmin()!.Username
}));
app.MapGet("/api/admin/update", (WebUpdates updates) => Results.Ok(updates.Status()));
app.MapGet("/api/admin/update/latest", async (WebUpdates updates,CancellationToken token) =>
{
    var release=await updates.Latest(token);
    return Results.Ok(new {Release=release,Available=WebUpdates.IsNewer(release)});
});
app.MapPost("/api/admin/update", async (UpdateInput input,WebUpdates updates,HttpContext context,SessionService sessions) =>
{
    if (updates.InstallUnavailable() is { } reason) throw new InputException(reason);
    var release=await updates.Latest(context.RequestAborted);
    if (input.Version!=release.Version || !WebUpdates.IsNewer(release))
        throw new InputException("Версия релиза изменилась. Проверьте обновления ещё раз.");
    updates.Start(release,sessions.Current(context)!.Name);
    return Results.Accepted();
});
app.MapPut("/api/admin/log-retention", (LogRetentionInput input, HttpContext context, Store store, SessionService sessions) =>
{
    store.SaveLogRetentionDays(input.Days, sessions.Current(context)!.Name);
    return Results.Ok(new { days = input.Days });
});
app.MapPut("/api/admin/settings", (SettingsInput input, HttpContext context, Store store, SettingsService settings, SessionService sessions) =>
{
    var value = settings.Prepare(input);
    store.SaveSettings(value, sessions.Current(context)!.Name);
    return Results.Ok(Rules.PublicSettings(value));
});
app.MapPost("/api/admin/connection/test", async (SettingsInput input, HttpContext context, SettingsService settings,
    ISqlGateway sql, Store store, SessionService sessions, CancellationToken token) =>
{
    var actor = sessions.Current(context)!.Name;
    var prepared = settings.Prepare(input);
    try
    {
        var result = await sql.Test(prepared, token);
        store.Audit(actor, "connection.test", "Подключение успешно. Проверяемые параметры не сохранялись.");
        return Results.Ok(result);
    }
    catch (SqlServiceException ex)
    {
        store.Audit(actor, "connection.failed", $"Проверка подключения неуспешна. SQL-код: {ex.Number}.");
        throw;
    }
});
app.MapPut("/api/admin/security", async (SecurityInput input, HttpContext context, Store store,
    SessionService sessions, IPasswordHasher<LocalAdmin> hasher) =>
{
    var user = sessions.Current(context)!;
    var accounts = Rules.ValidateWindowsAdmins(input.WindowsAdmins);
    LocalAdmin? replacement = null;
    if (!string.IsNullOrEmpty(input.NewPassword))
    {
        Rules.ValidatePassword(input.NewPassword);
        var previous = store.GetAdmin()!;
        replacement = previous with { PasswordHash = hasher.HashPassword(previous, input.NewPassword), Version = Guid.NewGuid().ToString("N") };
    }
    var currentAccess=store.GetWindowsAccess(app.Configuration);
    var testContourOperators=Rules.ValidateWindowsAdmins(input.TestContourOperators??currentAccess.TestContourOperators);
    var access=new WindowsAccess(input.WindowsEnabled??currentAccess.Enabled,input.WindowsAutoSignIn??currentAccess.AutoSignIn,
        testContourOperators);
    if(!access.Enabled)access=access with{AutoSignIn=false};
    store.SaveSecurity(accounts, replacement, user.Name, access);
    if (replacement is not null && user.Kind == "local")
        await SessionService.SignIn(context, replacement.Username, "local", replacement.Version);
    return Results.Ok(new { ok = true });
});
app.MapGet("/api/admin/audit", (Store store) => Results.Ok(store.GetAudit()));

app.MapGet("/api/databases", async (HttpContext context, Store store, SessionService sessions, ISqlGateway sql, CancellationToken token) =>
{
    var user = sessions.Current(context)!;
    var settings = store.GetSettings();
    if (settings is null) return Results.Ok(new { configured = false, databases = Array.Empty<DatabaseEntry>(), backupError = (string?)null });
    var databases = await sql.List(settings, token);
    var visible = databases.Select(x => (Database: x, Parsed: Rules.ParseDatabase(x.Name)))
        .Where(x => Rules.CanAccessDatabase(x.Parsed, user)).ToArray();
    var (backups, backupError) = BackupCatalog.Scan(settings.BackupDirectory);
    var backupsBySource = backups.ToLookup(backup => backup.SourceKey);
    var aliases = store.GetBackupAliases();
    var entries = visible.Select(x =>
    {
        var parsed = x.Parsed!;
        var backupSource = aliases.GetValueOrDefault(parsed.SourceKey, parsed.SourceKey);
        return new DatabaseEntry(x.Database.Name, parsed.SourceKey, parsed.OwnerKey, x.Database.State,
            backupsBySource[backupSource].FirstOrDefault(), x.Database.SizeBytes, backupSource, parsed.IsTestContour);
    }).ToArray();
    return Results.Ok(new { configured = true, databases = entries, backupError });
});

foreach (var route in new[] { "/", "/login", "/setup" })
    app.MapGet(route, () => Results.File(Path.Combine(app.Environment.WebRootPath, "index.html"), "text/html; charset=utf-8"));

app.MapWorkflow();

app.Lifetime.ApplicationStarted.Register(() => app.Logger.LogInformation(
    "DevRefresh {Version} запущен. Платформа {Platform}. Каталог данных {DataDirectory}. Каталог журналов {LogDirectory}",
    typeof(Program).Assembly.GetName().Version, System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription, dataPath, logPath));
try { app.Run(); }
catch (IOException ex) when (ex.GetBaseException() is System.Net.Sockets.SocketException
    { SocketErrorCode: System.Net.Sockets.SocketError.AddressAlreadyInUse })
{
    applicationLogger.Error("Не удалось запустить сервис: адрес уже занят. URLs {Urls}", builder.Configuration["Urls"]);
    Console.Error.WriteLine("Порт уже занят другой программой или копией DevRefresh. Откройте существующий сайт либо измените порт в config/server.json (например, 5188 на 5190). Другие процессы не останавливались.");
    Environment.ExitCode = 98;
}

static bool IsLocal(HttpContext context) => context.Connection.RemoteIpAddress is { } address && IPAddress.IsLoopback(address);

public partial class Program;
