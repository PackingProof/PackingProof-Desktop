using System.Text;
using ExpressPackingMonitoring.Config;
using Xunit;

namespace ExpressPackingMonitoring.Tests;

/// <summary>
/// 语音声线按界面语言分别保存：中文 / 英文 / 日语各有一套引擎、在线音色与离线声线。
///
/// "改了语言但两种语言串成一份"的根因：保存时按**当前**语言写槽位，而那一刻
/// Config.Language 已经是新语言了，于是上一语言界面上的声线被写进新语言的槽位，
/// 紧接着的规范化又把它装回界面字段 —— 两边看起来永远是同一个声线。
/// </summary>
public sealed class VoiceSettingsPerLanguageTests
{
    [Fact]
    public void SwitchingLanguage_KeepsEachLanguageOwnVoices()
    {
        var config = new AppConfig { Language = "zh-Hans" };
        AppConfig.NormalizeAfterLoad(config);

        // 中文：改一套并存回中文槽位
        config.EdgeTtsVoice = "zh-CN-XiaoyiNeural";
        config.EdgeTtsWarningVoice = "zh-CN-YunyangNeural";
        config.AiTtsEngine = "Kokoro";
        config.AiTtsSpeakerId = 12;
        config.AiTtsWarningSpeakerId = 13;
        config.StoreSelectedSpeechVoices();

        // 切到英文：装进来的是英文自己那套（默认值）
        config.Language = "en-US";
        Assert.True(config.ApplySpeechVoicesForLanguage());
        Assert.Equal("en-US-JennyNeural", config.EdgeTtsVoice);
        Assert.Equal("en-US-GuyNeural", config.EdgeTtsWarningVoice);

        // 英文再改一套并保存
        config.EdgeTtsVoice = "en-US-AriaNeural";
        config.EdgeTtsWarningVoice = "en-US-DavisNeural";
        config.StoreSelectedSpeechVoices();

        // 切回中文：必须还是中文那套，没被英文覆盖
        config.Language = "zh-Hans";
        config.ApplySpeechVoicesForLanguage();
        Assert.Equal("zh-CN-XiaoyiNeural", config.EdgeTtsVoice);
        Assert.Equal("zh-CN-YunyangNeural", config.EdgeTtsWarningVoice);
        Assert.Equal("Kokoro", config.AiTtsEngine);
        Assert.Equal(12, config.AiTtsSpeakerId);
        Assert.Equal(13, config.AiTtsWarningSpeakerId);

        // 再切回英文：英文那套也还在
        config.Language = "en-US";
        config.ApplySpeechVoicesForLanguage();
        Assert.Equal("en-US-AriaNeural", config.EdgeTtsVoice);
        Assert.Equal("en-US-DavisNeural", config.EdgeTtsWarningVoice);
    }

    /// <summary>
    /// 回归：保存时按"界面上的选择所属的语言"写槽位。
    /// 旧实现固定用当前语言，会把中文界面上的声线写进英文槽位。
    /// </summary>
    [Fact]
    public void StoringWithTheSelectionLanguage_DoesNotClobberTheOtherLanguage()
    {
        var config = new AppConfig { Language = "zh-Hans" };
        AppConfig.NormalizeAfterLoad(config);
        config.EdgeTtsVoice = "zh-CN-XiaoyiNeural";
        config.StoreSelectedSpeechVoices();

        // 切成英文后马上保存：界面上的选择其实还属于中文
        config.Language = "en-US";
        config.StoreSelectedSpeechVoices("zh-Hans");

        Assert.Equal("zh-CN-XiaoyiNeural", config.EdgeTtsVoiceZhHans);
        Assert.Equal("en-US-JennyNeural", config.EdgeTtsVoiceEnUs);
    }

    /// <summary>日语也要有自己的一份，且默认走联网语音。</summary>
    [Fact]
    public void JapaneseLanguage_HasItsOwnEngineAndVoices()
    {
        var config = new AppConfig { Language = "ja-JP" };
        AppConfig.NormalizeAfterLoad(config);

        Assert.Equal("Edge", config.AiTtsEngine);
        Assert.Equal("ja-JP-NanamiNeural", config.EdgeTtsVoice);
        Assert.Equal("ja-JP-KeitaNeural", config.EdgeTtsWarningVoice);
    }

    /// <summary>
    /// 设置页切语言的瞬间就要"存旧语言 + 装新语言 + 刷新控件"，
    /// 否则控件还显示上一语言的声线，下次保存又把新槽位写坏。
    /// </summary>
    [Fact]
    public void SettingsWindow_SwapsVoicesWhenTheLanguageComboChanges()
    {
        string xaml = ReadProjectFile(Path.Combine("UI", "SettingsWindow.xaml"));
        Assert.Contains(
            "SelectionChanged=\"LanguageComboBox_SelectionChanged\"",
            xaml,
            StringComparison.Ordinal);

        string code = ReadProjectFile(Path.Combine("UI", "SettingsWindow.Speech.cs"));
        Assert.Contains("Config.StoreSelectedSpeechVoices(previousLanguage)", code, StringComparison.Ordinal);
        Assert.Contains("Config.ApplySpeechVoicesForLanguage(newLanguage)", code, StringComparison.Ordinal);
        Assert.Contains("SyncSpeechVoiceSelectionsFromConfig()", code, StringComparison.Ordinal);
        Assert.Contains("EdgeNormalVoiceComboBox", code, StringComparison.Ordinal);
        Assert.Contains("KokoroNormalSpeakerUpDown", code, StringComparison.Ordinal);
    }

    private static string ReadProjectFile(string relativePath) =>
        File.ReadAllText(
            Path.Combine(FindRepositoryRoot(), "ExpressPackingMonitoring", relativePath),
            Encoding.UTF8);

    private static string FindRepositoryRoot()
    {
        foreach (string startPath in new[] { Directory.GetCurrentDirectory(), AppContext.BaseDirectory })
        {
            DirectoryInfo? directory = new(Path.GetFullPath(startPath));
            while (directory is not null)
            {
                if (File.Exists(Path.Combine(directory.FullName, "ExpressPackingMonitoring.sln")))
                    return directory.FullName;
                directory = directory.Parent;
            }
        }

        throw new DirectoryNotFoundException("ExpressPackingMonitoring repository root was not found.");
    }
}
