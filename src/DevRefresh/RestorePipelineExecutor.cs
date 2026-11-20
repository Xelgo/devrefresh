namespace DevRefresh;

public sealed class RestorePipelineExecutor(OneCDomainService oneC)
{
    public void Validate(SqlSettings settings,RestorePipeline? pipeline)
    {
        if(pipeline is null || pipeline.Operations.Length==0)return;
        foreach(var operation in pipeline.Operations)
        {
            RestorePipelineRules.Validate(pipeline.SourceType,pipeline.ArchiveFormat,operation.TargetDomain);
            if(operation.Kind!=RestorePipelineRules.WindowsDomain)throw new InputException("Неизвестная операция после восстановления.");
        }
        _=OneCConfiguration.Require(settings);
    }
    public Task<RestoreOperationResult[]> Apply(SqlSettings settings,RestorePipeline? pipeline,string target,string id,Action<string> progress,CancellationToken token)
    {
        Validate(settings,pipeline);
        return pipeline is null || pipeline.Operations.Length==0 ? Task.FromResult(Array.Empty<RestoreOperationResult>())
            : oneC.Apply(settings,pipeline,target,id,progress,token);
    }
    public Task RecoverPending()=>oneC.RecoverPending();
}