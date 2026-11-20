using System.ComponentModel;
using System.Diagnostics;
using System.Text;

namespace DevRefresh;

internal sealed record IbcmdProcessResult(int ExitCode,string Output);

public sealed class IbcmdProcessRunner
{
    internal async Task<IbcmdProcessResult> Run(ProcessStartInfo start,string? password,TimeSpan timeout,
        string[] secrets,CancellationToken token,string operationName="IBCMD")
    {
        token.ThrowIfCancellationRequested();
        start.UseShellExecute=false;start.CreateNoWindow=true;start.WindowStyle=ProcessWindowStyle.Hidden;
        start.RedirectStandardInput=true;start.RedirectStandardOutput=true;start.RedirectStandardError=true;
        start.StandardOutputEncoding=Encoding.UTF8;start.StandardErrorEncoding=Encoding.UTF8;
        start.StandardInputEncoding=new UTF8Encoding(false);
        using var process=new Process{StartInfo=start};
        try { if(!process.Start())throw new InputException($"Не удалось запустить {operationName}."); }
        catch(Win32Exception){throw new InputException($"Не удалось запустить {operationName}. Проверьте установку платформы и права службы.");}
        using var deadline=CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(timeout);
        async Task<string> Drain(StreamReader reader)
        {
            var tail=new StringBuilder();
            while(await reader.ReadLineAsync(deadline.Token) is {} line)
            {
                foreach(var secret in secrets.Where(x=>x.Length>0).OrderByDescending(x=>x.Length))
                    line=line.Replace(secret,"[скрыто]",StringComparison.Ordinal);
                line=new string(line.Where(x=>!char.IsControl(x) || x=='\t').ToArray());
                tail.AppendLine(line.Length>2048?line[..2048]:line);
                if(tail.Length>8192)tail.Remove(0,tail.Length-8192);
            }
            return tail.ToString();
        }
        var stdout=Drain(process.StandardOutput);var stderr=Drain(process.StandardError);
        try
        {
            try
            {
                if(password is not null)await process.StandardInput.WriteLineAsync(password.AsMemory(),deadline.Token);
                process.StandardInput.Close();
            }
            catch(IOException) { } // Preserve diagnostics if IBCMD exited before reading stdin.
            await process.WaitForExitAsync(deadline.Token);
            var output=await Task.WhenAll(stdout,stderr);
            return new(process.ExitCode,string.Join("\n",output).Trim());
        }
        catch(OperationCanceledException)
        {
            if(!process.HasExited)process.Kill(entireProcessTree:true);
            await process.WaitForExitAsync(CancellationToken.None);
            try{await Task.WhenAll(stdout,stderr);}catch(OperationCanceledException){}
            token.ThrowIfCancellationRequested();
            throw new InputException($"Превышен таймаут: {operationName}. Проверьте состояние целевой базы перед повтором.");
        }
        finally
        {
            if(!process.HasExited)
            {
                process.Kill(entireProcessTree:true);
                await process.WaitForExitAsync(CancellationToken.None);
            }
            deadline.Cancel();
            try{await Task.WhenAll(stdout,stderr);}catch(OperationCanceledException){}
        }
    }
}
