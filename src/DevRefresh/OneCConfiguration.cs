using System.Text.RegularExpressions;

namespace DevRefresh;

public sealed record OneCSettings(string ClusterAddress,string PlatformDirectory,int TimeoutSeconds=180);

public static class OneCConfiguration
{
    public static OneCSettings? Prepare(OneCSettings? input,OneCSettings? previous)
    {
        if(input is null)return previous;
        var value=input with{ClusterAddress=input.ClusterAddress.Trim(),PlatformDirectory=input.PlatformDirectory.Trim()};
        if(value.ClusterAddress.Length==0 && value.PlatformDirectory.Length==0)return null;
        Validate(value);
        return value;
    }

    public static void Validate(OneCSettings value)
    {
        if(!Regex.IsMatch(value.ClusterAddress,@"\A[a-zA-Z0-9_.-]+(?::[0-9]{1,5})?\z",RegexOptions.CultureInvariant)
            || value.ClusterAddress.Split(':') is [_,var port] && (!int.TryParse(port,out var number)||number is <1 or >65535))
            throw new InputException("Укажите адрес кластера 1С: SERVER или SERVER:1541.");
        if(value.PlatformDirectory.Length>2048 || !Path.IsPathFullyQualified(value.PlatformDirectory) || value.PlatformDirectory.Any(char.IsControl))
            throw new InputException("Укажите абсолютный путь к папке bin установленной платформы 1С.");
        if(value.TimeoutSeconds is <15 or >1800)throw new InputException("Таймаут операции 1С должен быть от 15 до 1800 секунд.");
    }

    public static OneCSettings Require(SqlSettings settings)
    {
        var value=settings.OneC??throw new InputException("Для подмены домена настройте кластер и папку платформы 1С.");
        Validate(value);
        if(!OperatingSystem.IsWindows() || !File.Exists(Path.Combine(value.PlatformDirectory,"comcntr.dll")))
            throw new InputException("Для операции 1С требуется установленная Windows-платформа с comcntr.dll.");
        return value;
    }
}
