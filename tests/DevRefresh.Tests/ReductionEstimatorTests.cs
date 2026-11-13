using DevRefresh;
using Xunit;

namespace DevRefresh.Tests;

public sealed class ReductionEstimatorTests
{
    static AnalyzedTable Table(string key,decimal size,string? date=null,string kind="РегистрСведений",string role="Main",string? root=null)=>
        new(key,key,"dbo","_T"+key,kind,root??key,role,1,true,null,date,null,null,2000,[],[],["_Key"],100,size,0,size,size);
    static AnalysisData Analysis(params AnalyzedTable[] tables)=>new("id","bp","bp.bak","Test","1",2000,"schema",tables,[],"2026-10-28T00:00:00Z");
    [Fact]
    public void DocumentAndExplicitPartDoNotCountTheSameBytesTwice()
    {
        var doc=Table("doc",1000,"_Date","Документ") with{PeriodDistribution=new(100,[new("2025-01-01",50),new("2026-01-01",50)])};
        var part=Table("part",500,"_Date","Документ","Part","doc") with{PeriodDistribution=new(100,[new("2025-01-01",60),new("2026-01-01",40)])};
        var estimate=ReductionEstimator.Calculate(Analysis(doc,part,Table("other",500)),[new("doc","2026-01-01"),new("part","2026-01-01")]);
        Assert.Equal(800,estimate.RemovedMinMB);Assert.Equal(800,estimate.RemovedMaxMB);
        Assert.Equal(1200,estimate.ResultMinMB);Assert.Equal(40,estimate.RemovedMinPercent);
    }
    [Fact]
    public void OldPeriodAnalysisReturnsRangeInsteadOfAssumingEntireTableWillBeDeleted()
    {
        var estimate=ReductionEstimator.Calculate(Analysis(Table("period",500,"_Period"),Table("other",500)),[new("period","2026-01-01")]);
        Assert.True(estimate.NeedsPeriodAnalysis);Assert.Equal(0,estimate.RemovedMinMB);Assert.Equal(500,estimate.RemovedMaxMB);
        Assert.Equal(500,estimate.ResultMinMB);Assert.Equal(1000,estimate.ResultMaxMB);
    }
    [Fact]
    public void CutInsideLeapMonthUsesDayFractionAndKeepsEmptyDates()
    {
        var table=Table("period",1160,"_Period") with{PeriodDistribution=new(200,[new("2024-02-01",100)])};
        var estimate=ReductionEstimator.Calculate(Analysis(table),[new("period","2024-02-15")]);
        Assert.Equal(280,estimate.RemovedMinMB);Assert.False(estimate.NeedsPeriodAnalysis);
        var afterMonth=ReductionEstimator.Calculate(Analysis(table),[new("period","2024-03-01")]);
        Assert.Equal(580,afterMonth.RemovedMaxMB);
    }
    [Fact]
    public void ObjectHistoryContributesOnlyWhenSelected()
    {
        var root=Table("reg",400);
        var aux=Table("reg.changes",100,role:"Changes",root:"reg");
        var history=Table("РегистрСведений.ВерсииОбъектов",200);
        var estimate=ReductionEstimator.Calculate(Analysis(root,aux,history),[new("reg",null)]);
        Assert.Equal(500,estimate.RemovedMinMB);Assert.Equal(200,estimate.ResultMinMB);
        Assert.Equal(200,ReductionEstimator.Calculate(Analysis(history),[new(history.Key,null)]).RemovedMinMB);
        var empty=ReductionEstimator.Calculate(Analysis(),[]);
        Assert.Equal(0,empty.RemovedMaxPercent);Assert.Equal(0,empty.ResultMaxMB);
    }
    [Fact]
    public void RegisterAuxiliariesAreEstimatedOnlyForExplicitlySelectedRegisters()
    {
        const string history = "РегистрСведений.ВерсииОбъектов";
        var document = Table("document", 100, kind:"Документ");
        var register = Table("register", 200);
        var slice = Table("register.slice", 50, role:"SliceLast", root:register.Key);
        var totals = Table("register.totals", 30, role:"Totals", root:register.Key);
        var versions = Table(history, 20) with { CanSelect = false };
        var versionsSlice = Table("versions.slice", 10, role:"SliceLast", root:history);
        var analysis = Analysis(document, register, slice, totals, versions, versionsSlice);

        Assert.Equal(100, ReductionEstimator.Calculate(analysis, [new(document.Key,null)]).RemovedMaxMB);
        Assert.Equal(380, ReductionEstimator.Calculate(analysis, [new(document.Key,null), new(register.Key,null)]).RemovedMaxMB);
    }
}
