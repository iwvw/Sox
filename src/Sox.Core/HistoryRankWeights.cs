namespace Sox.Core;

// Behaviour weighting shared by the history caches and the result comparer. Values are initial
// calibrations from ADR-0013 and are expected to be tuned against real use; they are kept here rather
// than inline so the recorder, the cache builder, and the comparer cannot drift.
internal static class HistoryRankWeights
{
    public const double FrequencyScale = 300;

    public const double RecencyRecent = 800;  // opened within the last hour
    public const double RecencyDay = 400;     // within the last day
    public const double RecencyWeek = 200;    // within the last week

    public const double ContextBonus = 500;

    public const double PenaltyUnit = 200;

    public const int PassoverCap = 3;

    private const long Hour = 3600;
    private const long Day = 86400;
    private const long Week = 604800;

    public static double Score(int count, long unixTime, long nowUnix)
    {
        var score = Math.Log2(1 + Math.Max(0, count)) * FrequencyScale;

        var age = nowUnix - unixTime;
        if (age < Hour)
            score += RecencyRecent;
        else if (age < Day)
            score += RecencyDay;
        else if (age < Week)
            score += RecencyWeek;

        return score;
    }

    public static double Penalty(int passovers) => Math.Min(passovers, PassoverCap) * PenaltyUnit;
}
