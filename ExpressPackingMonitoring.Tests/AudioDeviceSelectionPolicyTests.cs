using ExpressPackingMonitoring.Config;
using ExpressPackingMonitoring.UI;
using Xunit;

namespace ExpressPackingMonitoring.Tests;

/// <summary>
/// 设置页音频设备下拉的选中与回写规则。
/// 选错设备会直接导致没有录音或播报跑到别的喇叭，这里锁定边界。
/// </summary>
public sealed class AudioDeviceSelectionPolicyTests
{
    [Fact]
    public void Match_PrefersMonikerOverName()
    {
        var devices = new List<MicInfo>
        {
            new() { Name = "旧名字", Moniker = "id-a" },
            new() { Name = "目标设备", Moniker = "id-b" }
        };

        MicInfo? matched = AudioDeviceSelectionPolicy.Match(devices, "id-b", "旧名字");

        Assert.Equal("id-b", matched?.Moniker);
    }

    /// <summary>换机器后 Id 会失效，这时按名称兜底比什么都不选更符合预期。</summary>
    [Fact]
    public void Match_FallsBackToNameWhenMonikerMissing()
    {
        var devices = new List<MicInfo>
        {
            new() { Name = "USB 麦克风", Moniker = "id-new" }
        };

        MicInfo? matched = AudioDeviceSelectionPolicy.Match(devices, "id-stale", "USB 麦克风");

        Assert.Equal("id-new", matched?.Moniker);
    }

    [Fact]
    public void Match_ReturnsNullWhenNothingFits()
    {
        var devices = new List<MicInfo> { new() { Name = "A", Moniker = "id-a" } };

        Assert.Null(AudioDeviceSelectionPolicy.Match(devices, "id-x", "B"));
    }

    /// <summary>"未检测到麦克风"是占位项，不能被当成真实设备写进配置。</summary>
    [Fact]
    public void PlaceholderMicrophone_IsNotAvailable()
    {
        var placeholder = new MicInfo { Name = AudioDeviceSelectionPolicy.NoMicrophoneText };

        Assert.False(AudioDeviceSelectionPolicy.IsAvailable(placeholder));
    }

    /// <summary>播放设备列表第一项必须是"跟随系统默认"，保留不选具体喇叭的可能。</summary>
    [Fact]
    public void PlaybackDeviceList_StartsWithFollowSystemDefault()
    {
        List<MicInfo> devices = AudioDeviceSelectionPolicy.BuildPlaybackDeviceList();

        Assert.NotEmpty(devices);
        Assert.Equal(AudioDeviceSelectionPolicy.FollowSystemDefaultText, devices[0].Name);
        Assert.Equal(string.Empty, devices[0].Moniker);
    }

    /// <summary>选择"跟随系统默认"要清空配置，让 SpeechService 回落到默认端点。</summary>
    [Fact]
    public void ApplyPlaybackSelection_FollowSystemDefault_ClearsConfig()
    {
        var config = new AppConfig
        {
            PlaybackDeviceName = "旧喇叭",
            PlaybackDeviceMoniker = "id-old"
        };

        AudioDeviceSelectionPolicy.ApplyPlaybackSelection(
            config,
            new MicInfo { Name = AudioDeviceSelectionPolicy.FollowSystemDefaultText, Moniker = "" });

        Assert.Equal(string.Empty, config.PlaybackDeviceName);
        Assert.Equal(string.Empty, config.PlaybackDeviceMoniker);
    }

    [Fact]
    public void ApplyPlaybackSelection_ExplicitDevice_IsPersisted()
    {
        var config = new AppConfig();

        AudioDeviceSelectionPolicy.ApplyPlaybackSelection(
            config,
            new MicInfo { Name = "会议音箱", Moniker = "id-speaker" });

        Assert.Equal("会议音箱", config.PlaybackDeviceName);
        Assert.Equal("id-speaker", config.PlaybackDeviceMoniker);
    }

    /// <summary>没有选中项时也要落到"跟随系统默认"，不能留下半截旧配置。</summary>
    [Fact]
    public void ApplyPlaybackSelection_NullSelection_ClearsConfig()
    {
        var config = new AppConfig
        {
            PlaybackDeviceName = "旧喇叭",
            PlaybackDeviceMoniker = "id-old"
        };

        AudioDeviceSelectionPolicy.ApplyPlaybackSelection(config, null);

        Assert.Equal(string.Empty, config.PlaybackDeviceName);
        Assert.Equal(string.Empty, config.PlaybackDeviceMoniker);
    }

    [Fact]
    public void ApplyMicrophoneSelection_PlaceholderClearsConfig()
    {
        var config = new AppConfig
        {
            AudioDeviceName = "旧麦克风",
            AudioDeviceMoniker = "id-old"
        };

        AudioDeviceSelectionPolicy.ApplyMicrophoneSelection(
            config,
            new MicInfo { Name = AudioDeviceSelectionPolicy.NoMicrophoneText });

        Assert.Equal(string.Empty, config.AudioDeviceName);
        Assert.Equal(string.Empty, config.AudioDeviceMoniker);
    }
}
