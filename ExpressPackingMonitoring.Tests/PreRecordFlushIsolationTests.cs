using Xunit;

namespace ExpressPackingMonitoring.Tests;

/// <summary>
/// 预录回灌不能把处理循环堵死。
///
/// 回灌要把整批预录帧塞进录像队列，队列满了就得等编码器写出去
/// （5 秒预录 = 302 帧 1080p，实测 1 秒上下）。以前这整段都在帧顺序锁里，
/// 处理循环卡在锁上，预览跟着冻住 —— 看上去就是"点开始录制卡一下"。
///
/// 现在的形状：锁里只挂"正在回灌"的牌子，回灌循环在锁外跑；
/// 实时帧这段时间先不入队（队列留给预录帧），但处理循环只做判断、不等待。
/// </summary>
public sealed class PreRecordFlushIsolationTests
{
    [Fact]
    public void FlushLoop_RunsOutsideTheFrameOrderLock()
    {
        string source = ReadProjectFile("ViewModels", "MainViewModel.Recording.cs");

        int guard = source.IndexOf("IsRecording = true;", StringComparison.Ordinal);
        Assert.True(guard > 0, "找不到录制开始的锁内代码");

        string startBlock = EnclosingBlock(source, guard);
        Assert.Contains("_preRecordFlushInProgress, 1", startBlock, StringComparison.Ordinal);
        Assert.DoesNotContain("for (int preFrameIndex", startBlock, StringComparison.Ordinal);

        int loop = source.IndexOf("for (int preFrameIndex", StringComparison.Ordinal);
        Assert.True(loop > guard, "回灌循环应在挂牌之后");
        Assert.Contains("_preRecordFlushInProgress, 0", source[loop..], StringComparison.Ordinal);
        Assert.Contains("TryEnqueueFrameForRecording(preFrame)", source[loop..], StringComparison.Ordinal);
    }

    [Fact]
    public void LiveEnqueue_WaitsForTheFlushWithoutBlockingTheLoop()
    {
        string source = ReadProjectFile("ViewModels", "MainViewModel.Camera.cs");

        int enqueue = source.IndexOf(
            "TryEnqueueFrameForRecording(processedFrame, currentFrameCapturedTicks)",
            StringComparison.Ordinal);
        Assert.True(enqueue > 0, "找不到实时帧入队");

        // 判断写在入队之前，而且和入队同在一个 lock 里：只做判断、不等待
        string before = source[..enqueue];
        Assert.Contains("Volatile.Read(ref _preRecordFlushInProgress) == 0", before, StringComparison.Ordinal);
    }

    /// <summary>取包含指定位置的最近一层大括号块（用花括号配对，够本地源码守卫用）。</summary>
    private static string EnclosingBlock(string source, int position)
    {
        int start = source.LastIndexOf('{', position);
        Assert.True(start > 0, "找不到所在代码块");

        int depth = 0;
        for (int i = start; i < source.Length; i++)
        {
            if (source[i] == '{')
                depth++;
            else if (source[i] == '}')
            {
                depth--;
                if (depth == 0)
                    return source[start..(i + 1)];
            }
        }

        return source[start..];
    }

    private static string ReadProjectFile(params string[] relativePathParts)
    {
        string path = Path.Combine(
            [FindRepositoryRoot(), "ExpressPackingMonitoring", .. relativePathParts]);
        return File.ReadAllText(path, System.Text.Encoding.UTF8);
    }

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
