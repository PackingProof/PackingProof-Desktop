using ExpressPackingMonitoring.Logging;
using System.Runtime.InteropServices;

namespace ExpressPackingMonitoring.Services.Gpu;

/// <summary>
/// 运行时编译 HLSL。
///
/// 用运行时编译而不是预编译：着色器很短、只在摄像头启动时编译一次，
/// 预编译要把 fxc 塞进构建流程并管理产物，不值得。
///
/// d3dcompiler_47.dll 是 Windows 自带的（8 以后随系统分发），
/// 但精简镜像可能缺失，所以编译失败一律回退 CPU 路径而不是抛出。
/// </summary>
internal static class HlslCompiler
{
    [DllImport("d3dcompiler_47.dll", ExactSpelling = true, CharSet = CharSet.Ansi)]
    private static extern int D3DCompile(
        byte[] srcData,
        IntPtr srcDataSize,
        string? sourceName,
        IntPtr defines,
        IntPtr include,
        string entryPoint,
        string target,
        uint flags1,
        uint flags2,
        out ID3DBlob code,
        out ID3DBlob errorMsgs);

    /// <summary>D3DCOMPILE_OPTIMIZATION_LEVEL3：着色器每帧跑几百万次，值得让编译器多花点时间。</summary>
    private const uint OptimizationLevel3 = 1 << 15;

    /// <summary>
    /// 编译一段着色器。返回 null 表示编译不了（语法错、缺 DLL），调用方回退 CPU 路径。
    /// </summary>
    internal static byte[]? TryCompile(string source, string entryPoint, string target)
    {
        ID3DBlob? code = null;
        ID3DBlob? errors = null;
        try
        {
            byte[] sourceBytes = System.Text.Encoding.ASCII.GetBytes(source);
            int hr = D3DCompile(
                sourceBytes,
                (IntPtr)sourceBytes.Length,
                null,
                IntPtr.Zero,
                IntPtr.Zero,
                entryPoint,
                target,
                OptimizationLevel3,
                0,
                out code,
                out errors);

            if (hr < 0 || code == null)
            {
                // 编译错误信息必须带出来：着色器写错时没有它根本无从下手。
                string message = ReadErrorMessage(errors);
                RuntimeLog.Warn(
                    "Camera",
                    $"编译着色器失败（{entryPoint}/{target}）：HRESULT=0x{hr:X8} {message}");
                return null;
            }

            IntPtr buffer = code.GetBufferPointer();
            int size = (int)code.GetBufferSize();
            if (buffer == IntPtr.Zero || size <= 0)
                return null;

            byte[] bytecode = new byte[size];
            Marshal.Copy(buffer, bytecode, 0, size);
            return bytecode;
        }
        catch (Exception ex)
        {
            // DllNotFoundException（精简镜像缺 d3dcompiler_47.dll）也走这里。
            RuntimeLog.Warn("Camera", $"编译着色器异常（{entryPoint}）：{ex.Message}");
            return null;
        }
        finally
        {
            if (errors != null)
            {
                try { Marshal.ReleaseComObject(errors); } catch { }
            }
            if (code != null)
            {
                try { Marshal.ReleaseComObject(code); } catch { }
            }
        }
    }

    private static string ReadErrorMessage(ID3DBlob? errors)
    {
        if (errors == null)
            return "";

        try
        {
            IntPtr buffer = errors.GetBufferPointer();
            return buffer == IntPtr.Zero
                ? ""
                : Marshal.PtrToStringAnsi(buffer, (int)errors.GetBufferSize())?.Trim() ?? "";
        }
        catch
        {
            return "";
        }
    }
}

[ComImport]
[Guid("8ba5fb08-5195-40e2-ac58-0d989c3a0102")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface ID3DBlob
{
    [PreserveSig] IntPtr GetBufferPointer();
    [PreserveSig] IntPtr GetBufferSize();
}
