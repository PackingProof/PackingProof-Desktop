using System;
using System.Collections.Generic;
using System.Linq;

namespace ExpressPackingMonitoring.Input
{
    public static class ScannerAutoSubmitPolicy
    {
        public static bool IsFastSequence(
            IReadOnlyList<double> intervalsMs,
            int characterCount,
            int maxAverageIntervalMs,
            int maxKeyIntervalMs)
        {
            if (characterCount < 2) return false;

            int expectedIntervalCount = characterCount - 1;
            if (intervalsMs.Count < expectedIntervalCount) return false;

            var recentIntervals = intervalsMs
                .Skip(Math.Max(0, intervalsMs.Count - expectedIntervalCount))
                .ToList();

            if (recentIntervals.Count == 0) return false;

            double average = recentIntervals.Average();
            if (average <= maxAverageIntervalMs)
                return true;

            int fastCount = recentIntervals.Count(ms => ms <= maxKeyIntervalMs);
            int requiredFastCount = (int)Math.Ceiling(recentIntervals.Count * 0.8);
            return fastCount >= requiredFastCount;
        }
    }
}
