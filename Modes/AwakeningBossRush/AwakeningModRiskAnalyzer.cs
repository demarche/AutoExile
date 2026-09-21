namespace AutoExile.Modes.AwakeningBossRush;

public sealed record ModRisk(string Mod, string Stratum, int Exposed, int Controls, double DeathDifference,
    double LowerDifference, double? FightRatio, double? ProfitDifference, double PValue, string Status, string[] EvidenceRuns,
    double FightMedian, double FightP90, double TimeoutRate, int[] ObservedValues, string[] InseparableMods);

public static class AwakeningModRiskAnalyzer
{
    private static (double Lower, double Upper) Wilson(int deaths, int n)
    {
        if (n == 0) return (0, 1);
        const double z = 1.96;
        var p = (double)deaths / n;
        var center = (p + z * z / (2 * n)) / (1 + z * z / n);
        var radius = z * Math.Sqrt(p * (1 - p) / n + z * z / (4 * n * n)) / (1 + z * z / n);
        return (center - radius, center + radius);
    }
    public static IReadOnlyList<ModRisk> Analyze(IEnumerable<AwakeningRun> attempts)
    {
        // Reentries are correlated: aggregate by consumed map, not by attempts. Keep deaths across retries.
        var runs = attempts.Where(x => x.ActivationConfirmed && x.Map != null && x.Outcome != AttemptOutcome.None)
            .GroupBy(x => x.RunId).Select(g => g.OrderByDescending(x => x.AttemptNumber).First()).ToList();
        var risks = new List<ModRisk>();
        foreach (var stratum in runs.GroupBy(x => x.BuildFingerprint + ":" + x.BuildMvid + ":" + string.Join(",", x.Bosses.Values.Select(b => b.Group).Distinct().Order()) + ":iiq" + (int)(x.Map!.Quantity / 25)))
        {
            var all = stratum.Where(x => !x.InputFault && x.Outcome != AttemptOutcome.ManualIntervention).ToList();
            foreach (var mod in all.SelectMany(x => x.Map!.Mods.Select(m => m.Id)).Distinct())
            {
                var yes = all.Where(x => x.Map!.Mods.Any(m => m.Id == mod)).ToList();
                var no = all.Except(yes).ToList();
                var dy = yes.Count(x => x.Deaths > 0); var dn = no.Count(x => x.Deaths > 0);
                var delta = (double)dy / Math.Max(1, yes.Count) - (double)dn / Math.Max(1, no.Count);
                var lower = Wilson(dy, yes.Count).Lower - Wilson(dn, no.Count).Upper;
                var pooled = (double)(dy + dn) / Math.Max(1, yes.Count + no.Count);
                var se = Math.Sqrt(pooled * (1 - pooled) * (1d / Math.Max(1, yes.Count) + 1d / Math.Max(1, no.Count)));
                // Conservative one-sided Gaussian tail bound; BH corrects across tested mod/strata pairs.
                var p = se > 0 && delta > 0 ? Math.Min(1, Math.Exp(-0.5 * Math.Pow(delta / se, 2))) : 1;
                double Fight(AwakeningRun r) => r.PhaseSeconds.GetValueOrDefault("Fight") + r.PhaseSeconds.GetValueOrDefault("MapBoss");
                double Profit(AwakeningRun r) => r.CostKnown && r.RevenueKnown && r.OperatingSeconds > 0 ? (r.RevenueChaos - r.CostChaos) * 3600 / r.OperatingSeconds : double.NaN;
                double? AverageProfit(List<AwakeningRun> list) { var values = list.Select(Profit).Where(double.IsFinite).ToArray(); return values.Length == 0 ? null : values.Average(); }
                var baseline = no.Count == 0 ? 0 : no.Average(Fight);
                double? ratio = baseline > 0 ? yes.Average(Fight) / baseline : null;
                var fightTimes = yes.Select(Fight).Order().ToArray();
                var inseparable = yes.SelectMany(r => r.Map!.Mods.Select(m => m.Id)).Distinct().Where(m => m != mod &&
                    yes.All(r => r.Map!.Mods.Any(x => x.Id == m)) && no.All(r => r.Map!.Mods.All(x => x.Id != m))).ToArray();
                risks.Add(new(mod, stratum.Key, yes.Count, no.Count, delta, lower, ratio, AverageProfit(yes) - AverageProfit(no), p,
                    yes.Count < 20 || no.Count < 20 ? "InsufficientEvidence" : "Watch", yes.Select(x => x.RunId).ToArray(),
                    Quantile(fightTimes, .5), Quantile(fightTimes, .9), (double)yes.Count(x => x.Outcome == AttemptOutcome.Timeout) / yes.Count,
                    yes.SelectMany(r => r.Map!.Mods.Where(m => m.Id == mod).SelectMany(m => m.Values)).Distinct().Order().ToArray(), inseparable));
            }
        }
        var eligible = risks.Select((x, i) => (Risk: x, Index: i)).Where(x => x.Risk.Status == "Watch").OrderBy(x => x.Risk.PValue).ToList();
        int accepted = -1;
        for (int rank = 0; rank < eligible.Count; rank++) if (eligible[rank].Risk.PValue <= .05 * (rank + 1) / eligible.Count) accepted = rank;
        for (int i = 0; i <= accepted; i++)
        {
            var candidate = eligible[i];
            if (candidate.Risk.LowerDifference > 0 && candidate.Risk.DeathDifference >= .1)
                risks[candidate.Index] = candidate.Risk with { Status = "CandidateNG" };
        }
        return risks.OrderByDescending(x => x.Status == "CandidateNG").ThenByDescending(x => x.DeathDifference).ToList();
    }
    private static double Quantile(double[] sorted, double q)
    {
        if (sorted.Length == 0) return 0;
        var index = (sorted.Length - 1) * q;
        return sorted[(int)index] + (sorted[(int)Math.Ceiling(index)] - sorted[(int)index]) * (index - (int)index);
    }
}
