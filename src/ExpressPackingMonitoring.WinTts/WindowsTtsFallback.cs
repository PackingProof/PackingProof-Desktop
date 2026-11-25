using System.Diagnostics;
using System.IO;
using System.Linq;
using Windows.Media.SpeechSynthesis;

namespace ExpressPackingMonitoring.WinTts
{
    /// <summary>
    /// Windows 系统语音合成（基于 WinRT）。该程序集只允许在现代 Windows 上
    /// 由主程序按需加载；Windows 7 的发布变体不会包含本程序集。
    /// 字段使用 object 保存合成器，防止类加载时解析 WinRT 类型。
    /// </summary>
    public sealed class WindowsTtsFallback : IDisposable
    {
        private object? _ttsNormal;
        private object? _ttsWarning;

        public string LastError { get; private set; } = "";

        public WindowsTtsFallback()
        {
            try
            {
                LastError = "";
                var voices = SpeechSynthesizer.AllVoices;
                var femaleZh = voices.FirstOrDefault(v => v != null && v.Gender == VoiceGender.Female && v.Language == "zh-CN");
                var maleZh = voices.FirstOrDefault(v => v != null && v.Gender == VoiceGender.Male && v.Language == "zh-CN");
                var anyZh = femaleZh ?? maleZh ?? voices.FirstOrDefault(v => v != null && v.Language.StartsWith("zh"));

                var normal = new SpeechSynthesizer();
                if (femaleZh != null) normal.Voice = femaleZh;
                else if (anyZh != null) normal.Voice = anyZh;
                _ttsNormal = normal;

                var warning = new SpeechSynthesizer();
                if (maleZh != null) warning.Voice = maleZh;
                else if (anyZh != null) warning.Voice = anyZh;
                _ttsWarning = warning;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[WinTts] Windows TTS init failed: {ex.Message}");
                LastError = $"Initialization failed: {ex.GetType().Name}: {ex.Message}";
                _ttsNormal = null;
                _ttsWarning = null;
            }
        }

        public bool TrySynthesize(string text, bool isWarning, float speed, out byte[] wavData)
        {
            wavData = Array.Empty<byte>();

            SpeechSynthesizer synth;
            if (isWarning)
            {
                if (_ttsWarning is not SpeechSynthesizer warningSynth)
                {
                    SetErrorIfEmpty("Warning synthesizer is unavailable");
                    return false;
                }
                synth = warningSynth;
            }
            else
            {
                if (_ttsNormal is not SpeechSynthesizer normalSynth)
                {
                    SetErrorIfEmpty("Normal synthesizer is unavailable");
                    return false;
                }
                synth = normalSynth;
            }

            try
            {
                LastError = "";
                try
                {
                    synth.Options.SpeakingRate = Math.Clamp((double)speed, 0.5, 2.0);
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"[WinTts] Speaking rate unavailable, using system default: {ex.Message}");
                }
                var result = synth.SynthesizeTextToStreamAsync(text).AsTask().GetAwaiter().GetResult();
                using var ms = new MemoryStream();
                result.AsStreamForRead().CopyTo(ms);
                var data = ms.ToArray();
                if (data.Length < 44)
                {
                    LastError = $"Synthesis returned only {data.Length} bytes";
                    return false;
                }
                wavData = data;
                return true;
            }
            catch (Exception ex)
            {
                LastError = $"Synthesis failed: {ex.GetType().Name}: {ex.Message}";
                Debug.WriteLine($"[WinTts] {LastError}");
                return false;
            }
        }

        private void SetErrorIfEmpty(string message)
        {
            if (string.IsNullOrWhiteSpace(LastError))
                LastError = message;
        }

        public void Dispose()
        {
            if (_ttsNormal is SpeechSynthesizer normal)
            {
                try { normal.Dispose(); } catch { }
            }
            if (_ttsWarning is SpeechSynthesizer warning)
            {
                try { warning.Dispose(); } catch { }
            }
            _ttsNormal = null;
            _ttsWarning = null;
        }
    }
}
