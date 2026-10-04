using System;
using System.Windows.Controls;

namespace ExpressPackingMonitoring.UI
{
    /// <summary>
    /// 语音声线按界面语言分别保存（引擎 / 在线音色 / 离线声线各一份）。
    ///
    /// 设置页的语音控件绑定的是 Config 上"当前语言那一份"的字段，而 Config 是纯 POCO、
    /// 不会发属性通知；所以**切语言的瞬间**就得把上一语言的界面选择存回它的槽位、
    /// 再把新语言的槽位装进 Config 并刷新控件。只靠保存时补一刀不够：
    /// 那时 Config.Language 已经变了，会把上一语言的声线写进新语言的槽位，两种语言就串成一份。
    /// </summary>
    public partial class SettingsWindow
    {
        /// <summary>界面上这些语音选择当前属于哪个语言；没动过语言时等于进入设置页时的语言。</summary>
        private string? _voiceSelectionsLanguage;

        private void LanguageComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (Config == null || !Capabilities.IsRecordingDevice)
                return;

            string newLanguage = Config.Language;
            string previousLanguage = _voiceSelectionsLanguage ?? _originalLanguage;
            if (string.Equals(previousLanguage, newLanguage, StringComparison.OrdinalIgnoreCase))
                return;

            // 1) 界面上这些选择属于上一语言：先存回它的槽位
            Config.StoreSelectedSpeechVoices(previousLanguage);
            // 2) 把新语言的槽位装进 Config（与加载配置时同一条规则）
            Config.ApplySpeechVoicesForLanguage(newLanguage);
            _voiceSelectionsLanguage = newLanguage;
            // 3) 控件不会自己刷新，手动同步一次，免得下次保存又把旧语言的声线写进新槽位
            SyncSpeechVoiceSelectionsFromConfig();
        }

        /// <summary>把 Config 上当前语言的语音选择刷到控件上（引擎下拉、在线音色、离线声线）。</summary>
        private void SyncSpeechVoiceSelectionsFromConfig()
        {
            SyncVoiceEngineComboBoxFromConfig();

            EdgeNormalVoiceComboBox?.GetBindingExpression(ComboBox.SelectedValueProperty)?.UpdateTarget();
            EdgeWarningVoiceComboBox?.GetBindingExpression(ComboBox.SelectedValueProperty)?.UpdateTarget();
            KokoroNormalSpeakerUpDown?.GetBindingExpression(Xceed.Wpf.Toolkit.IntegerUpDown.ValueProperty)?.UpdateTarget();
            KokoroWarningSpeakerUpDown?.GetBindingExpression(Xceed.Wpf.Toolkit.IntegerUpDown.ValueProperty)?.UpdateTarget();
        }
    }
}
