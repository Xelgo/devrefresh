using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Data.SqlClient;

namespace DevRefresh;

internal sealed record IbcmdLaunch(ProcessStartInfo Start,string SqlUsername,string SqlPassword,TimeSpan Timeout);

public sealed class IbcmdRestore(SettingsService settingsService,IDataProtectionProvider protection,IbcmdProcessRunner runner)
{
    public async Task<Version> Probe(SqlSettings settings,CancellationToken token)
    {
        var dt=settings.DtRestore??throw new InputException("Заполните настройки .dt / IBCMD.");
        var executable=Path.Combine(dt.IbcmdDirectory,"ibcmd.exe");
        if(!OperatingSystem.IsWindows() || !File.Exists(executable))
            throw new InputException("В указанной папке не найден Windows-файл ibcmd.exe.");
        var start=new ProcessStartInfo(executable);start.ArgumentList.Add("--version");
        var result=await runner.Run(start,null,TimeSpan.FromSeconds(15),[],token);
        var match=Regex.Match(result.Output,@"\b\d+\.\d+\.\d+\.\d+\b",RegexOptions.CultureInvariant);
        if(result.ExitCode!=0 || !match.Success || !Version.TryParse(match.Value,out var version))
            throw new InputException("Не удалось определить версию IBCMD. Проверьте комплектность установленной платформы 1С.");
        if(version<new Version(8,3,20,0))throw new InputException("Для загрузки .dt требуется IBCMD версии 8.3.20 или новее.");
        return version;
    }

    internal IbcmdLaunch Prepare(SqlSettings settings,string database,string archive,string workingDirectory,Version version)
    {
        if(Rules.ParseDatabase(database) is null)throw new InputException("Некорректная база назначения IBCMD.");
        if(DtRestoreConfiguration.UnavailableReason(settings) is {} reason)throw new InputException(reason);
        var dt=settings.DtRestore!;
        var protector=protection.CreateProtector(DtRestoreConfiguration.PasswordPurpose);
        string Unprotect(string value)
        {
            try{return value.Length==0?"":protector.Unprotect(value);}
            catch(CryptographicException){throw new InputException("Не удалось расшифровать пароль IBCMD. Введите его заново в настройках.");}
        }
        var sqlUser=dt.SqlUsername;
        string sqlPassword;
        if(sqlUser.Length>0)sqlPassword=Unprotect(dt.ProtectedSqlPassword);
        else
        {
            var connection=new SqlConnectionStringBuilder(settingsService.ConnectionString(settings));
            sqlUser=connection.UserID;sqlPassword=connection.Password;
        }
        if(sqlPassword.Any(char.IsControl))throw new InputException("Пароль SQL Server для IBCMD не должен содержать управляющие символы.");
        var start=new ProcessStartInfo(Path.Combine(dt.IbcmdDirectory,"ibcmd.exe"))
            {WorkingDirectory=dt.IbcmdDirectory};
        foreach(var argument in new[]{"infobase","create","--dbms=MSSQLServer","--db-server="+settings.Server,
            "--db-name="+database,"--db-user="+sqlUser,"--request-db-pwd","--data="+workingDirectory,
            "--create-database","--force","--restore="+archive})
            start.ArgumentList.Add(argument);
        return new(start,sqlUser,sqlPassword,TimeSpan.FromMinutes(dt.TimeoutMinutes));
    }

    internal async Task CheckCreatePermission(SqlSettings settings,IbcmdLaunch launch,CancellationToken token)
    {
        var builder=new SqlConnectionStringBuilder(settingsService.ConnectionString(settings))
        {InitialCatalog="master",IntegratedSecurity=false,UserID=launch.SqlUsername,Password=launch.SqlPassword,
            Pooling=false,ConnectTimeout=15,ApplicationName="DevRefresh IBCMD preflight"};
        await using var connection=new SqlConnection(builder.ConnectionString);
        try
        {
            await connection.OpenAsync(token);
            if(await RestoreSql.Scalar<int>(connection,"SELECT CASE WHEN HAS_PERMS_BY_NAME(NULL,NULL,'CREATE ANY DATABASE')=1 OR IS_SRVROLEMEMBER('dbcreator')=1 OR IS_SRVROLEMEMBER('sysadmin')=1 THEN 1 ELSE 0 END",token)!=1)
                throw new InputException("SQL-пользователь IBCMD не имеет права создавать базы. Целевая база не удалена.");
        }
        catch(SqlException){throw new InputException("Не удалось проверить SQL-вход IBCMD и права создания базы. Целевая база не удалена.");}
    }

    internal async Task Execute(IbcmdLaunch launch,CancellationToken token)
    {
        var result=await runner.Run(launch.Start,launch.SqlPassword,launch.Timeout,
            [launch.SqlPassword],token);
        if(result.ExitCode!=0)
            throw new InputException($"IBCMD завершил загрузку .dt с кодом {result.ExitCode}. Проверьте учётные данные, версию платформы и монопольный доступ 1С. "+
                result.Output);
    }
}
