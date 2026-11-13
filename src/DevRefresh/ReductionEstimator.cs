namespace DevRefresh;

public static class ReductionEstimator
{
    private readonly record struct Fraction(decimal Min,decimal Max);

    public static ReductionEstimate Calculate(AnalysisData analysis,ReductionRule[] selected)
    {
        WorkflowRules.ValidateSelection(analysis,selected);
        var tables=analysis.Tables.ToDictionary(t=>t.Key,StringComparer.Ordinal);
        var fractions=new Dictionary<string,Fraction>(StringComparer.Ordinal);
        var needsPeriodAnalysis=false;
        Fraction ForDate(AnalyzedTable table,string? before)
        {
            if(table.Rows==0)return new(0,0);
            if(before is null)return new(1,1);
            if(table.PeriodDistribution is not {} distribution)
            {needsPeriodAnalysis=true;return new(0,1);}
            if(distribution.TotalRows<=0)return new(0,0);
            var cutoff=DateOnly.ParseExact(before,"yyyy-MM-dd");
            decimal removed=0;
            foreach(var bucket in distribution.Buckets)
            {
                var start=DateOnly.ParseExact(bucket.Month,"yyyy-MM-dd");
                var days=DateTime.DaysInMonth(start.Year,start.Month);
                var part=Math.Clamp((decimal)(cutoff.DayNumber-start.DayNumber)/days,0,1);
                removed+=bucket.Rows*part;
            }
            var ratio=Math.Clamp(removed/distribution.TotalRows,0,1);
            return new(ratio,ratio);
        }
        void Include(AnalyzedTable table,Fraction fraction)
        {
            if(table.Rows==0)return;
            var current=fractions.GetValueOrDefault(table.Key);
            // The same physical rows may be selected directly and through their owner.
            fractions[table.Key]=new(Math.Max(current.Min,fraction.Min),Math.Max(current.Max,fraction.Max));
        }
        void Rule(AnalyzedTable table,string? before)
        {
            Include(table,ForDate(table,before));
            if(table.Role=="Main" && table.Kind is "Документ" or "Справочник")
                foreach(var part in analysis.Tables.Where(t=>t.RootKey==table.Key&&t.Role=="Part"))
                    Include(part,ForDate(part,before));
        }
        foreach(var rule in selected)Rule(tables[rule.Key],rule.Before);
        foreach(var root in selected.Select(rule=>tables[rule.Key]).Where(t=>t.Role=="Main"&&t.Kind.StartsWith("Регистр",StringComparison.Ordinal)))
            if(fractions.TryGetValue(root.Key,out var fraction))
                foreach(var aux in analysis.Tables.Where(t=>t.RootKey==root.Key&&t.Role is "Changes" or "Totals" or "DimValues" or "SliceFirst" or "SliceLast"))
                    Include(aux,fraction);
        var original=analysis.Tables.Sum(t=>Math.Max(0,t.UsedMB));
        var min=Math.Round(Math.Clamp(fractions.Sum(p=>tables[p.Key].UsedMB*p.Value.Min),0,original),3);
        var max=Math.Round(Math.Clamp(fractions.Sum(p=>tables[p.Key].UsedMB*p.Value.Max),min,original),3);
        var notes=new List<string>
        {
            "Оценка объёма данных и индексов по сохранённому анализу. Свободное место и журнал SQL не включены; фактический размер файлов после сжатия может отличаться.",
            "Для периода учитывается распределение строк по месяцам; внутри месяца — пропорционально дням. Средний размер строк, индексы и связанные данные дают погрешность."
        };
        if(needsPeriodAnalysis)notes.Add("Для части таблиц в старом анализе нет статистики дат: показан диапазон. Новый анализ бэкапа и сохранение профиля уточнят оценку.");
        return new(original,min,max,original-max,original-min,original==0?0:Math.Round(100*min/original,2),
            original==0?0:Math.Round(100*max/original,2),needsPeriodAnalysis,analysis.AnalyzedAt,[..notes]);
    }
}
