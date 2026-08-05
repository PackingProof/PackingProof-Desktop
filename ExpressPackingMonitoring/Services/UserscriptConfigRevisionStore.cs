using System.IO;
using System.Text.Json;

namespace ExpressPackingMonitoring.Services;

/// <summary>
/// 维护油猴脚本对外版本的“配置修订号”：设备配置指纹变化时修订号 +1，
/// 让 Tampermonkey 在模板版本不变、设备列表变化时也能自动更新对齐配置。
/// </summary>
internal sealed class UserscriptConfigRevisionStore
{
    private readonly string _path;
    private readonly object _sync = new();
    private State _state;

    internal UserscriptConfigRevisionStore(string path)
    {
        _path = path ?? throw new ArgumentNullException(nameof(path));
        _state = Load(path);
    }

    internal int GetRevision(string fingerprint)
    {
        string value = fingerprint ?? "";
        lock (_sync)
        {
            if (!string.Equals(_state.Fingerprint, value, StringComparison.Ordinal))
            {
                _state = new State
                {
                    Fingerprint = value,
                    // 首次指纹从 0 开始；此后每次配置变化只增不减。
                    Revision = _state.Fingerprint.Length == 0 ? 0 : _state.Revision + 1
                };
                TrySave(_state);
            }
            return _state.Revision;
        }
    }

    private void TrySave(State state)
    {
        try
        {
            string? directory = Path.GetDirectoryName(_path);
            if (!string.IsNullOrWhiteSpace(directory)) Directory.CreateDirectory(directory);
            string temporaryPath = _path + ".tmp";
            File.WriteAllText(temporaryPath, JsonSerializer.Serialize(state));
            File.Move(temporaryPath, _path, true);
        }
        catch
        {
            // 写失败不阻塞脚本服务；修订号仍保留在内存中，下次变化会再次尝试。
        }
    }

    private static State Load(string path)
    {
        try
        {
            if (!File.Exists(path)) return new State();
            return JsonSerializer.Deserialize<State>(File.ReadAllText(path)) ?? new State();
        }
        catch
        {
            return new State();
        }
    }

    private sealed class State
    {
        public string Fingerprint { get; set; } = "";
        public int Revision { get; set; }
    }
}
