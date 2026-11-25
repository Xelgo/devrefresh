using DevRefresh;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace DevRefresh.Tests;

public sealed class IbcmdWorkspaceCleanupTests
{
    [Fact]
    public void RemovesOnlyTheCompletedJobDirectoryAndKeepsTheArchiveAndOtherJobs()
    {
        using var app=new Factory();
        var root=Path.Combine(app.DirectoryPath,"ibcmd");var id=Guid.NewGuid().ToString("N");
        var work=Path.Combine(root,"dt-"+id);var nested=Path.Combine(work,"cache");
        var other=Path.Combine(root,"dt-"+Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(nested);Directory.CreateDirectory(other);
        File.WriteAllText(Path.Combine(nested,"payload.tmp"),"temporary IBCMD data");
        var retained=Path.Combine(other,"payload.tmp");File.WriteAllText(retained,"another job");
        var archive=Path.Combine(app.DirectoryPath,"source.dt");File.WriteAllText(archive,"source dump");

        Assert.True(IbcmdWorkspaceCleanup.TryDelete(root,id,NullLogger.Instance));
        Assert.False(Directory.Exists(work));Assert.True(Directory.Exists(root));
        Assert.Equal("another job",File.ReadAllText(retained));
        Assert.Equal("source dump",File.ReadAllText(archive));
    }

    [Fact]
    public void AlreadyMissingDirectoryIsCleanWithoutCreatingAnything()
    {
        using var app=new Factory();var root=Path.Combine(app.DirectoryPath,"ibcmd");
        Assert.True(IbcmdWorkspaceCleanup.TryDelete(root,Guid.NewGuid().ToString("N"),NullLogger.Instance));
        Assert.False(Directory.Exists(root));
    }

    [Theory]
    [InlineData("")]
    [InlineData("..")]
    [InlineData("../outside")]
    [InlineData("..\\outside")]
    public void MalformedJobIdCannotDeleteTheWorkingRoot(string id)
    {
        using var app=new Factory();Directory.CreateDirectory(app.DirectoryPath);
        var retained=Path.Combine(app.DirectoryPath,"keep.txt");File.WriteAllText(retained,"keep");
        Assert.False(IbcmdWorkspaceCleanup.TryDelete(app.DirectoryPath,id,NullLogger.Instance));
        Assert.Equal("keep",File.ReadAllText(retained));
    }

    [Fact]
    public void LockedFileProducesANonfatalCleanupFailure()
    {
        using var app=new Factory();var id=Guid.NewGuid().ToString("N");
        var root=Path.Combine(app.DirectoryPath,"ibcmd");var work=Path.Combine(root,"dt-"+id);
        Directory.CreateDirectory(work);
        using var held=new FileStream(Path.Combine(work,"locked.tmp"),FileMode.Create,FileAccess.ReadWrite,FileShare.None);
        Assert.False(IbcmdWorkspaceCleanup.TryDelete(root,id,NullLogger.Instance));
        Assert.True(Directory.Exists(work));Assert.True(Directory.Exists(root));
    }
}
