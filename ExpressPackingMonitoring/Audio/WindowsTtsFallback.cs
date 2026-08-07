using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using Windows.Media.SpeechSynthesis;

namespace ExpressPackingMonitoring.Audio
{
    /// <summary>
    /// Windows 系统语音合成（基于 WinRT）。仅在现代 Windows 上创建，
    /// 避免 Windows 7 加载 WinRT 运行时导致进程崩溃。
    /// 字段使用 object 保存合成器，防止类加载时解析 WinRT 类型。
    /// </summary>
    internal sealed class WindowsTtsFallback : IDisposable
    {
        private object? _ttsNormal;
        private object? _ttsWarning;

        public WindowsTtsFallback()
        {
            try
            {
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
                Debug.WriteLine($"[SpeechService] Windows TTS init failed: {ex.Message}");
                _ttsNormal = null;
                _ttsWarning = null;
            }
        }

        public bool TrySynthesize(string text, bool isWarning, out byte[] wavData)
        {
            wavData = Array.Empty<byte>();

            SpeechSynthesizer synth;
            if (isWarning)
            {
                if (_ttsWarning is not SpeechSynthesizer warningSynth) return false;
                synth = warningSynth;
            }
            else
            {
                if (_ttsNormal is not SpeechSynthesizer normalSynth) return false;
                synth = normalSynth;
            }

            try
            {
                var result = synth.SynthesizeTextToStreamAsync(text).AsTask().GetAwaiter().GetResult();
                using var ms = new MemoryStream();
                result.AsStreamForRead().CopyTo(ms);
                var data = ms.ToArray();
                if (data.Length < 44) return false;
                wavData = data;
                return true;
            }
            catch
            {
                return false;
            }
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
