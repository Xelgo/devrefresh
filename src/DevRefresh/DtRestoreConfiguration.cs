using Microsoft.AspNetCore.DataProtection;

namespace DevRefresh;

public sealed record DtRestoreSettings(bool Enabled,string BackupDirectory,string IbcmdDirectory,
    string WorkingDirectory,string InfobaseUser,string ProtectedInfobasePassword,string SqlUsername,
    string ProtectedSqlPassword,int TimeoutMinutes=360);
public sealed record DtRestoreInput(bool Enabled,string BackupDirectory,string IbcmdDirectory,
    string WorkingDirectory,string InfobaseUser,string? InfobasePassword=null,string SqlUsername="",
    string? SqlPassword=null,int TimeoutMinutes=360,bool ClearInfobasePassword=false);

public static class DtRestoreConfiguration
{
    internal const string PasswordPurpose="DevRefresh.IbcmdPassword.v1";
    public static DtRestoreSettings Empty => new(false,"","","","","","","",360);

    public static DtRestoreSettings? Prepare(DtRestoreInput? input,DtRestoreSettings? previous,IDataProtectionProvider protection)
    {
        if(input is null)return previous;
        var protector=protection.CreateProtector(PasswordPurpose);
        var user=input.InfobaseUser.Trim();var sqlUser=input.SqlUsername.Trim();
        if(new[]{user,sqlUser}.Any(x=>x.Length>256 || x.Any(char.IsControl)) ||
            new[]{input.InfobasePassword,input.SqlPassword}.Any(x=>x is not null && (x.Length>1024 || x.Any(char.IsControl))))
            throw new InputException("Проверьте имена и пароли IBCMD: управляющие символы не допускаются.");
        string Password(string? value,string old,bool same,bool clear=false) => clear?"":
            !string.IsNullOrEmpty(value)?protector.Protect(value):same?old:"";
        var result=new DtRestoreSettings(input.Enabled,input.BackupDirectory.Trim(),input.IbcmdDirectory.Trim(),
            input.WorkingDirectory.Trim(),user,
            Password(input.InfobasePassword,previous?.ProtectedInfobasePassword??"",user==previous?.InfobaseUser,
                input.ClearInfobasePassword || user.Length==0),
            sqlUser,Password(input.SqlPassword,previous?.ProtectedSqlPassword??"",sqlUser==previous?.SqlUsername,sqlUser.Length==0),
            input.TimeoutMinutes);
        foreach(var path in new[]{result.BackupDirectory,result.IbcmdDirectory,result.WorkingDirectory})
            if(path.Length>2048 || path.Any(char.IsControl) || path.Length>0 && !Path.IsPathFullyQualified(path))
                throw new InputException("Укажите абсолютные пути в настройках .dt / IBCMD.");
        if(result.TimeoutMinutes is <1 or >1440)throw new InputException("Таймаут IBCMD: от 1 до 1440 минут.");
        if(result.Enabled && (result.BackupDirectory.Length==0 || result.IbcmdDirectory.Length==0))
            throw new InputException("Для .dt укажите каталог архивов и папку bin, содержащую ibcmd.exe.");
        if(result.Enabled && sqlUser.Length>0 && result.ProtectedSqlPassword.Length==0)
            throw new InputException("Введите пароль отдельного SQL-пользователя IBCMD.");
        return result;
    }

    public static object Public(DtRestoreSettings? value)
    {
        var settings=value??Empty;
        return new {settings.Enabled,settings.BackupDirectory,settings.IbcmdDirectory,settings.WorkingDirectory,
            settings.InfobaseUser,InfobasePasswordConfigured=settings.ProtectedInfobasePassword.Length>0,
            settings.SqlUsername,SqlPasswordConfigured=settings.ProtectedSqlPassword.Length>0,settings.TimeoutMinutes};
    }

    public static string? UnavailableReason(SqlSettings settings)
    {
        if(settings.DtRestore is not {Enabled:true} dt)return "Загрузка .dt через IBCMD выключена в настройках.";
        if(!OperatingSystem.IsWindows())return "Загрузка .dt в MSSQL через IBCMD требует Windows.";
        if(!File.Exists(Path.Combine(dt.IbcmdDirectory,"ibcmd.exe")))return "В настроенной папке IBCMD не найден ibcmd.exe.";
        if(dt.SqlUsername.Length==0 && settings.Authentication!="sql")
            return "Для IBCMD укажите отдельный SQL-логин и пароль либо используйте SQL-аутентификацию в общих настройках.";
        return null;
    }

    public static (BackupFile[] Files,string? Error) Scan(SqlSettings settings)
    {
        if(UnavailableReason(settings) is {} reason)return ([],reason);
        var (files,error)=BackupCatalog.Scan(settings.DtRestore!.BackupDirectory,".dt");
        return (files.Where(x=>x.Size>0).ToArray(),error);
    }

    public static BackupFile RequireLatest(SqlSettings settings,string type)
    {
        var (files,error)=Scan(settings);
        return files.FirstOrDefault(x=>x.SourceKey==type)
            ??throw new InputException(error??$"В каталоге .dt нет архива для типа базы «{type}».");
    }
}
