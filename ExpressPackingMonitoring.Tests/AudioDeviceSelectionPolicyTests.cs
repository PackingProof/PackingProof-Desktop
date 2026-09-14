using ExpressPackingMonitoring.Audio;
using ExpressPackingMonitoring.Config;
using ExpressPackingMonitoring.UI;
using NAudio.CoreAudioApi;
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

    /// <summary>
    /// 播放设备列表第一项必须是"跟随系统默认"，并且带显式标记而不是空值。
    /// 用空值表示跟随默认会被判定成"没选过设备"，历史上正是这样导致了
    /// 设置页的音频提醒，以及录制时直接跳过音频采集录出没有声音的视频。
    /// </summary>
    [Fact]
    public void PlaybackDeviceList_StartsWithExplicitSystemDefault()
    {
        List<MicInfo> devices = AudioDeviceSelectionPolicy.BuildPlaybackDeviceList();

        Assert.NotEmpty(devices);
        Assert.Equal(AudioDeviceSelectionPolicy.FollowSystemDefaultText, devices[0].Name);
        Assert.Equal(AudioEndpointCatalog.SystemDefaultId, devices[0].Moniker);
        Assert.NotEqual(string.Empty, devices[0].Moniker);
    }

    /// <summary>麦克风也要有"跟随系统默认"，与播放设备保持一致。</summary>
    [Fact]
    public void MicrophoneDeviceList_StartsWithExplicitSystemDefault()
    {
        List<MicInfo> devices = AudioDeviceSelectionPolicy.BuildDeviceList(DataFlow.Capture);

        Assert.NotEmpty(devices);
        Assert.Equal(AudioDeviceSelectionPolicy.FollowSystemDefaultText, devices[0].Name);
        Assert.Equal(AudioEndpointCatalog.SystemDefaultId, devices[0].Moniker);
    }

    /// <summary>选择"跟随系统默认"要写入显式标记，而不是把配置清空。</summary>
    [Fact]
    public void ApplyPlaybackSelection_FollowSystemDefault_PersistsSentinel()
    {
        var config = new AppConfig
        {
            PlaybackDeviceName = "旧喇叭",
            PlaybackDeviceMoniker = "id-old"
        };

        AudioDeviceSelectionPolicy.ApplyPlaybackSelection(
            config,
            new MicInfo
            {
                Name = AudioDeviceSelectionPolicy.FollowSystemDefaultText,
                Moniker = AudioEndpointCatalog.SystemDefaultId
            });

        Assert.Equal(AudioEndpointCatalog.SystemDefaultId, config.PlaybackDeviceMoniker);
        Assert.Equal(AudioDeviceSelectionPolicy.FollowSystemDefaultText, config.PlaybackDeviceName);
    }

    /// <summary>
    /// 麦克风选"跟随系统默认"后必须被认定为"已选择"，
    /// 否则设置页会弹出"未选择麦克风"的提醒，录制也会没有声音。
    /// </summary>
    [Fact]
    public void SystemDefaultMicrophone_CountsAsUsableSelection()
    {
        var config = new AppConfig();

        AudioDeviceSelectionPolicy.ApplyMicrophoneSelection(
            config,
            new MicInfo
            {
                Name = AudioDeviceSelectionPolicy.FollowSystemDefaultText,
                Moniker = AudioEndpointCatalog.SystemDefaultId
            });

        Assert.True(AudioEndpointCatalog.IsSystemDefault(config.AudioDeviceMoniker));
        Assert.True(AudioDeviceSelectionPolicy.HasUsableMicrophoneSelection(config));
    }

    /// <summary>真正没选过设备时仍要提示，不能被"跟随系统默认"的改动顺手掩盖掉。</summary>
    [Fact]
    public void EmptyMicrophoneConfig_IsStillReportedAsUnselected()
    {
        Assert.False(AudioDeviceSelectionPolicy.HasUsableMicrophoneSelection(new AppConfig()));
    }

    /// <summary>配置里存着显式标记时，下拉要选中"跟随系统默认"这一项。</summary>
    [Fact]
    public void Match_SelectsSystemDefaultEntry()
    {
        var devices = new List<MicInfo>
        {
            new()
            {
                Name = AudioDeviceSelectionPolicy.FollowSystemDefaultText,
                Moniker = AudioEndpointCatalog.SystemDefaultId
            },
            new() { Name = "USB 麦克风", Moniker = "id-a" }
        };

        MicInfo? matched = AudioDeviceSelectionPolicy.Match(
            devices,
            AudioEndpointCatalog.SystemDefaultId,
            AudioDeviceSelectionPolicy.FollowSystemDefaultText);

        Assert.Equal(AudioEndpointCatalog.SystemDefaultId, matched?.Moniker);
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

    /// <summary>
    /// 设置页的麦克风下拉曾经自己拼了一个 Moniker 为空的"跟随系统默认"项：
    /// 选中它保存后配置成了"名字=跟随系统默认、Moniker 为空"，录制会因为找不到
    /// 同名设备而放弃这一单。这里锁住哨兵项必须能被选中并原样回写。
    /// </summary>
    [Fact]
    public void PrependFollowSystemDefault_EntryIsMatchableAndKeepsSentinel()
    {
        List<MicInfo> devices = AudioDeviceSelectionPolicy.PrependFollowSystemDefault(
            new List<MicInfo> { new() { Name = "USB 麦克风", Moniker = "id-a" } });

        Assert.Equal(AudioDeviceSelectionPolicy.FollowSystemDefaultText, devices[0].Name);
        Assert.Equal(AudioEndpointCatalog.SystemDefaultId, devices[0].Moniker);
        Assert.Equal("id-a", devices[1].Moniker);

        MicInfo? matched = AudioDeviceSelectionPolicy.Match(
            devices,
            AudioEndpointCatalog.SystemDefaultId,
            AudioDeviceSelectionPolicy.FollowSystemDefaultText);

        Assert.NotNull(matched);
        Assert.Equal(AudioEndpointCatalog.SystemDefaultId, matched!.Moniker);

        var config = new AppConfig();
        AudioDeviceSelectionPolicy.ApplyMicrophoneSelection(config, matched);

        Assert.Equal(AudioEndpointCatalog.SystemDefaultId, config.AudioDeviceMoniker);
        Assert.True(AudioDeviceSelectionPolicy.HasUsableMicrophoneSelection(config));
    }

    /// <summary>配置为空时下拉落到哨兵项，保存后仍然是可用的"跟随系统默认"。</summary>
    [Fact]
    public void EmptyMicrophoneConfig_FallsBackToSentinelEntry()
    {
        List<MicInfo> devices = AudioDeviceSelectionPolicy.PrependFollowSystemDefault(
            new List<MicInfo> { new() { Name = "USB 麦克风", Moniker = "id-a" } });

        MicInfo? selected = AudioDeviceSelectionPolicy.Match(devices, "", "")
            ?? devices.FirstOrDefault();

        Assert.NotNull(selected);
        Assert.Equal(AudioEndpointCatalog.SystemDefaultId, selected!.Moniker);
    }

    /// <summary>设置页必须用策略插入哨兵项，不能自己拼一个空 Moniker 的项。</summary>
    [Fact]
    public void SettingsWindow_MicrophoneListIsBuiltByPolicy()
    {
        string source = ReadRepositoryFile(
            "ExpressPackingMonitoring", "UI", "SettingsWindow.xaml.cs");

        Assert.Contains(
            "AudioDeviceSelectionPolicy.PrependFollowSystemDefault(result.Mics)",
            source,
            StringComparison.Ordinal);
    }

    private static string ReadRepositoryFile(params string[] parts)
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory != null)
        {
            string path = Path.Combine([directory.FullName, .. parts]);
            if (File.Exists(path))
                return File.ReadAllText(path);
            directory = directory.Parent;
        }

        throw new FileNotFoundException($"找不到仓库文件：{string.Join('/', parts)}");
    }
}
