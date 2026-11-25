namespace DevRefresh;

internal static class IbcmdWorkspaceCleanup
{
    public static bool TryDelete(string root,string jobId,ILogger logger)
    {
        try
        {
            if(!Guid.TryParseExact(jobId,"N",out _) || !Path.IsPathFullyQualified(root))
                throw new InputException("Некорректный рабочий каталог задания IBCMD.");
            var parent=Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
            var work=Path.GetFullPath(Path.Combine(parent,"dt-"+jobId));
            if(!string.Equals(Path.GetDirectoryName(work),parent,StringComparison.OrdinalIgnoreCase))
                throw new InputException("Рабочий каталог IBCMD выходит за пределы временной папки.");
            if(!Directory.Exists(work))return true;
            if((File.GetAttributes(work)&FileAttributes.ReparsePoint)!=0)
                throw new InputException("Рабочий каталог IBCMD является ссылкой.");
            Directory.Delete(work,recursive:true);
            return true;
        }
        catch(Exception error)
        {
            logger.LogWarning(error,"Не удалось удалить рабочую папку IBCMD задания {JobId}; база восстановлена",jobId);
            return false;
        }
    }
}
