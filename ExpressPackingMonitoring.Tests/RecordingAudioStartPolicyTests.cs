using ExpressPackingMonitoring.ViewModels;
using Xunit;

namespace ExpressPackingMonitoring.Tests;

/// <summary>
/// 麦克风启动失败的处理策略：现场日志显示一次端点瞬时不可用会 complete 队列、删掉文件，
/// 把已经开写的整段录像赔掉。这里固定"先重试、再降级为仅录视频"的行为。
/// </summary>
public sealed class RecordingAudioStartPolicyTests
{
    [Fact]
    public void FirstFailureAlwaysRetries()
    {
        Assert.Equal(
            RecordingAudioStartPolicy.AudioStartFailureAction.RetryOnce,
            RecordingAudioStartPolicy.Decide(directAac: false, attemptIndex: 0));
        Assert.Equal(
            RecordingAudioStartPolicy.AudioStartFailureAction.RetryOnce,
            RecordingAudioStartPolicy.Decide(directAac: true, attemptIndex: 0));
    }

    /// <summary>普通 WAV 路径：音频是独立文件，缺声音不该让录像失败。</summary>
    [Fact]
    public void SecondFailureKeepsVideoWhenAudioIsSeparate()
    {
        Assert.Equal(
            RecordingAudioStartPolicy.AudioStartFailureAction.ContinueVideoOnly,
            RecordingAudioStartPolicy.Decide(directAac: false, attemptIndex: 1));
    }

    /// <summary>实时 AAC 直录：音频是 FFmpeg 的第二个输入，管道不连接编码器会一直等，只能取消。</summary>
    [Fact]
    public void SecondFailureAbortsWhenAudioIsEmbeddedInThePipe()
    {
        Assert.Equal(
            RecordingAudioStartPolicy.AudioStartFailureAction.Abort,
            RecordingAudioStartPolicy.Decide(directAac: true, attemptIndex: 1));
    }

    [Fact]
    public void AttemptsAndDelayStayBounded()
    {
        Assert.Equal(2, RecordingAudioStartPolicy.AudioStartAttempts);
        Assert.InRange(RecordingAudioStartPolicy.RetryDelayMs, 100, 1500);
    }

    /// <summary>最后一次尝试必须给出终态（降级或取消），否则开录会一直卡在重试里。</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void LastAttemptAlwaysResolves(bool directAac)
    {
        RecordingAudioStartPolicy.AudioStartFailureAction action =
            RecordingAudioStartPolicy.Decide(directAac, RecordingAudioStartPolicy.AudioStartAttempts - 1);

        Assert.NotEqual(RecordingAudioStartPolicy.AudioStartFailureAction.RetryOnce, action);
    }
}
