namespace BBT.Workflow.Monitor.Stats;

/// <summary>Pure rate helper for monitor statistics (guards divide-by-zero).</summary>
public static class StatsRateCalculator
{
    /// <summary>Returns part/total, or 0 when total is zero.</summary>
    public static double Rate(int part, int total) => total == 0 ? 0d : (double)part / total;
}
