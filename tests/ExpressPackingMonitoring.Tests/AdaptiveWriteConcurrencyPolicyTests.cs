using System.Diagnostics;
using ExpressPackingMonitoring.Services;
using Xunit;

namespace ExpressPackingMonitoring.Tests;

public sealed class AdaptiveWriteConcurrencyPolicyTests
{
    [Fact]
    public void ActualWriteFailureImmediatelyReducesConcurrency()
    {
        var policy = new AdaptiveWriteConcurrencyPolicy(initialConcurrency: 4);

        policy.RecordWriteFailure();

        Assert.Equal(2, policy.CurrentLimit);
    }

    [Fact]
    public void ConcurrencyCanRecoverAfterATransientWriteFailure()
    {
        long now = 10 * Stopwatch.Frequency;
        var policy = new AdaptiveWriteConcurrencyPolicy(
            initialConcurrency: 4,
            timestampProvider: () => now,
            adaptationWindow: TimeSpan.FromSeconds(1));
        RecordWindow(policy, ref now, chunks: 4, queued: true);

        policy.RecordWriteFailure();
        Assert.Equal(2, policy.CurrentLimit);

        RecordWindow(policy, ref now, chunks: 4, queued: true);
        RecordWindow(policy, ref now, chunks: 4, queued: true);
        RecordWindow(policy, ref now, chunks: 4, queued: true);
        RecordWindow(policy, ref now, chunks: 4, queued: true);
        RecordWindow(policy, ref now, chunks: 4, queued: true);
        RecordWindow(policy, ref now, chunks: 4, queued: true);
        Assert.True(policy.CurrentLimit >= 3);
    }

    [Fact]
    public void RealWriteThroughputRaisesConcurrencyAndPlateausAtTheBestLevel()
    {
        long now = 10 * Stopwatch.Frequency;
        var policy = new AdaptiveWriteConcurrencyPolicy(
            initialConcurrency: 2,
            timestampProvider: () => now,
            adaptationWindow: TimeSpan.FromSeconds(1));

        RecordWindow(policy, ref now, chunks: 2, queued: true);
        Assert.Equal(2, policy.CurrentLimit);

        RecordWindow(policy, ref now, chunks: 2, queued: true);
        Assert.Equal(2, policy.CurrentLimit);

        RecordWindow(policy, ref now, chunks: 2, queued: true);
        Assert.Equal(3, policy.CurrentLimit);

        RecordWindow(policy, ref now, chunks: 3, queued: true);
        Assert.Equal(3, policy.CurrentLimit);

        RecordWindow(policy, ref now, chunks: 3, queued: true);
        RecordWindow(policy, ref now, chunks: 3, queued: true);
        Assert.Equal(4, policy.CurrentLimit);

        RecordWindow(policy, ref now, chunks: 3, queued: true);
        Assert.Equal(3, policy.CurrentLimit);
    }

    [Fact]
    public void ThroughputDropReducesConcurrencyInsteadOfKeepingAFixedDiskCap()
    {
        long now = 10 * Stopwatch.Frequency;
        var policy = new AdaptiveWriteConcurrencyPolicy(
            initialConcurrency: 4,
            timestampProvider: () => now,
            adaptationWindow: TimeSpan.FromSeconds(1));

        RecordWindow(policy, ref now, chunks: 4, queued: true);
        RecordWindow(policy, ref now, chunks: 4, queued: true);
        RecordWindow(policy, ref now, chunks: 4, queued: true);
        Assert.True(policy.CurrentLimit >= 4);

        RecordWindow(policy, ref now, chunks: 1, queued: true);
        Assert.True(policy.CurrentLimit < 5);
    }

    [Fact]
    public void WriteLatencySpikeReducesConcurrencyBeforeTheQueueCollapses()
    {
        long now = 10 * Stopwatch.Frequency;
        var policy = new AdaptiveWriteConcurrencyPolicy(
            initialConcurrency: 4,
            timestampProvider: () => now,
            adaptationWindow: TimeSpan.FromSeconds(1));

        RecordWindow(policy, ref now, chunks: 4, queued: true);
        Assert.Equal(4, policy.CurrentLimit);

        RecordWindow(
            policy,
            ref now,
            chunks: 4,
            queued: true,
            durationMilliseconds: 400);
        Assert.Equal(3, policy.CurrentLimit);
    }

    private static void RecordWindow(
        AdaptiveWriteConcurrencyPolicy policy,
        ref long now,
        int chunks,
        bool queued,
        int durationMilliseconds = 100)
    {
        for (int index = 0; index < chunks; index++)
        {
            policy.RecordWrite(
                4 * 1024 * 1024,
                TimeSpan.FromMilliseconds(durationMilliseconds),
                queued);
        }
        now += Stopwatch.Frequency;
        policy.RecordWrite(
            1,
            TimeSpan.FromMilliseconds(1),
            queued);
    }
}
