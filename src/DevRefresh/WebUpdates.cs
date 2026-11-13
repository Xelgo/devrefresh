using System.Diagnostics;
using System.Security.Principal;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Hosting.WindowsServices;

namespace DevRefresh;

internal sealed record UpdateInput(string Version);
internal sealed record ReleaseInfo(string Version,string Notes)
{
    public string Url => $"https://github.com/Xelgo/devrefresh/releases/tag/v{Version}";
}
internal sealed record UpdateProgress(string Version,string State,int ProcessId);

internal sealed partial class WebUpdates(IConfiguration config,IWebHostEnvironment environment,WorkflowService workflows,
    Store store,ILogger<WebUpdates> logger)
{
    private const string LatestUrl="https://api.github.com/repos/Xelgo/devrefresh/releases/latest";
    private const string AssetName="DevRefresh-update-win-x64.zip";
    private static readonly HttpClient Client=new() { Timeout=TimeSpan.FromSeconds(20) };
    private readonly object gate=new();
    private Process? worker;
    private string? startingVersion;
    private string DataPath => Path.GetFullPath(config["DataDirectory"]??"App_Data",environment.ContentRootPath);
    private string StatusPath => Path.Combine(DataPath,"web-update.status.json");
    private static Version Installed => typeof(WebUpdates).Assembly.GetName().Version!;
    internal static string CurrentVersion => Installed.ToString(3);

    internal string? InstallUnavailable()
    {
        if (!config.GetValue("Updates:Enabled",true)) return "Установка из интерфейса отключена настройкой сервера.";
        if (!OperatingSystem.IsWindows() || !WindowsServiceHelpers.IsWindowsService())
            return "Установка из интерфейса доступна при запуске DevRefresh как службы Windows.";
        using var identity=WindowsIdentity.GetCurrent();
        if (!identity.IsSystem && !new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator))
            return "У службы нет прав на установку. Используйте UPDATE-ONLINE.cmd с правами администратора Windows.";
        return null;
    }

    internal object Status()
    {
        lock (gate)
        {
            return new {CurrentVersion,Unavailable=InstallUnavailable(),Operation=ReadProgress()};
        }
    }

    internal async Task<ReleaseInfo> Latest(CancellationToken token)
    {
        using var request=new HttpRequestMessage(HttpMethod.Get,LatestUrl);
        request.Headers.UserAgent.ParseAdd($"DevRefresh/{CurrentVersion}");
        request.Headers.Accept.ParseAdd("application/vnd.github+json");
        request.Headers.Add("X-GitHub-Api-Version","2022-11-28");
        using var response=await Client.SendAsync(request,token);
        response.EnsureSuccessStatusCode();
        using var json=JsonDocument.Parse(await response.Content.ReadAsStringAsync(token));
        return ParseRelease(json.RootElement);
    }

    internal static ReleaseInfo ParseRelease(JsonElement root)
    {
        var tag=root.GetProperty("tag_name").GetString();
        if (tag is null || !StableTag().IsMatch(tag) || !Version.TryParse(tag[1..],out _) ||
            root.GetProperty("draft").GetBoolean() || root.GetProperty("prerelease").GetBoolean())
            throw new InvalidDataException("GitHub did not return a stable DevRefresh release.");
        var assets=root.GetProperty("assets").EnumerateArray()
            .Where(asset=>asset.GetProperty("name").GetString()==AssetName && asset.GetProperty("state").GetString()=="uploaded")
            .ToArray();
        if (assets.Length!=1 || !assets[0].TryGetProperty("digest",out var digest) || !Sha256().IsMatch(digest.GetString()??""))
            throw new InvalidDataException("GitHub did not return the checked DevRefresh update package.");
        return new(tag[1..],root.GetProperty("body").GetString()??"");
    }

    internal static bool IsNewer(ReleaseInfo release) => Version.Parse(release.Version)>Version.Parse(CurrentVersion);

    internal void Start(ReleaseInfo release,string actor)
    {
        if (InstallUnavailable() is { } reason) throw new InputException(reason);
        var root=Path.GetFullPath(Path.Combine(environment.ContentRootPath,".."));
        var script=Path.Combine(root,"scripts","Update-Online.ps1");
        var configPath=Path.Combine(root,"config","server.json");
        if (!File.Exists(script) || !File.Exists(configPath))
            throw new InputException("Обновление из интерфейса доступно для стандартной установки DevRefresh.");
        var serviceName=config["WindowsService:Name"]??"DevRefresh";
        var logs=Path.GetFullPath(config["LogDirectory"]??Path.Combine(DataPath,"logs"),environment.ContentRootPath);
        var log=Path.Combine(logs,$"web-update-{DateTimeOffset.UtcNow:yyyyMMdd-HHmmss}-{Guid.NewGuid():N}.log");
        var start=new ProcessStartInfo(Path.Combine(Environment.SystemDirectory,"WindowsPowerShell","v1.0","powershell.exe"))
        {UseShellExecute=false,CreateNoWindow=true,WorkingDirectory=root};
        foreach (var arg in new[]{"-NoProfile","-NonInteractive","-ExecutionPolicy","Bypass","-File",script,
            "-TargetDirectory",root,"-ServiceName",serviceName,"-ExpectedTag","v"+release.Version,
            "-StatusPath",StatusPath,"-LogPath",log,"-Yes"}) start.ArgumentList.Add(arg);

        lock (gate)
        {
            if (ReadProgress() is {State:"Running"}) throw new InputException("Обновление уже выполняется.");
            if (!workflows.PauseForUpdate()) throw new InputException("Дождитесь завершения выполняющихся заданий и повторите обновление.");
            try
            {
                store.Audit(actor,"update.requested",$"Запрошена установка DevRefresh {release.Version}.");
                File.Delete(StatusPath);
                failedToStart=null;
                worker=Process.Start(start)??throw new InvalidOperationException("Could not start the DevRefresh updater.");
                startingVersion=release.Version;
                _=ObserveExit(worker);
            }
            catch
            {
                workflows.ResumeAfterUpdate();
                throw;
            }
        }
    }

    private async Task ObserveExit(Process process)
    {
        try
        {
            await process.WaitForExitAsync();
            if (process.ExitCode!=0) logger.LogWarning("DevRefresh updater exited with code {ExitCode}",process.ExitCode);
        }
        finally
        {
            lock (gate)
            {
                var last=ReadProgress();
                worker=null;
                if (last is null || last.State=="Running")
                    failedToStart=new(startingVersion!,"Failed",0);
                startingVersion=null;
                workflows.ResumeAfterUpdate();
            }
            process.Dispose();
        }
    }

    private UpdateProgress? failedToStart;

    private UpdateProgress? ReadProgress()
    {
        if (worker is {HasExited:false}) return new(startingVersion!,"Running",worker.Id);
        if (!File.Exists(StatusPath)) return failedToStart;
        try
        {
            using var file=new FileStream(StatusPath,FileMode.Open,FileAccess.Read,FileShare.ReadWrite|FileShare.Delete);
            var progress=JsonSerializer.Deserialize<UpdateProgress>(file);
            if (progress is not {State:"Running"}) return progress;
            using var process=Process.GetProcessById(progress.ProcessId);
            return process.HasExited ? progress with {State="Failed"} : progress;
        }
        catch (ArgumentException) { return new(startingVersion??CurrentVersion,"Failed",0); }
        catch (IOException) { return failedToStart; }
        catch (JsonException) { return failedToStart; }
    }

    [GeneratedRegex(@"\Av\d+\.\d+\.\d+\z")]
    private static partial Regex StableTag();
    [GeneratedRegex(@"\Asha256:[0-9a-fA-F]{64}\z")]
    private static partial Regex Sha256();
}
