using ExpressPackingMonitoring.Services.Gpu;
using Xunit;

namespace ExpressPackingMonitoring.Tests;

/// <summary>
/// 着色器必须能真编译过。
///
/// 这是渲染管线能否成立的前提：HLSL 写错了只有编译器知道，
/// 而编译在运行时发生 —— 没有这组用例，语法错要等到用户启动摄像头才暴露，
/// 而那时表现只是"GPU 路径不可用，回退 CPU"，问题被回退逻辑掩盖掉。
/// </summary>
public sealed class HlslCompilerTests
{
    /// <summary>顶点着色器：铺满视口的两个三角形。</summary>
    [Fact]
    public void CompilesVertexShader()
    {
        byte[]? bytecode = HlslCompiler.TryCompile(
            GpuConversionShaders.VertexShader,
            "main",
            "vs_4_0");

        // 编译器不可用（精简镜像缺 d3dcompiler_47.dll）时跳过，不制造假失败。
        if (bytecode == null && !IsCompilerAvailable())
            return;

        Assert.NotNull(bytecode);
        // 实测本机编译出 716 字节字节码，确认这条用例真跑了编译器而不是跳过。
        Assert.True(bytecode!.Length > 0);
    }

    /// <summary>YUY2 像素着色器：解码 + BT.709/601 + 缩放。</summary>
    [Fact]
    public void CompilesYuy2PixelShader()
    {
        byte[]? bytecode = HlslCompiler.TryCompile(
            GpuConversionShaders.Yuy2PixelShader,
            "main",
            "ps_4_0");

        if (bytecode == null && !IsCompilerAvailable())
            return;

        Assert.NotNull(bytecode);
        Assert.True(bytecode!.Length > 0);
    }

    /// <summary>NV12 像素着色器：双平面采样。</summary>
    [Fact]
    public void CompilesNv12PixelShader()
    {
        byte[]? bytecode = HlslCompiler.TryCompile(
            GpuConversionShaders.Nv12PixelShader,
            "main",
            "ps_4_0");

        if (bytecode == null && !IsCompilerAvailable())
            return;

        Assert.NotNull(bytecode);
        Assert.True(bytecode!.Length > 0);
    }

    /// <summary>
    /// 语法错必须被报告为失败，而不是静默返回空字节码 ——
    /// 否则渲染管线会拿着空着色器去创建，错误延后到更难定位的地方。
    /// </summary>
    [Fact]
    public void ReportsFailureForInvalidShader()
    {
        byte[]? bytecode = HlslCompiler.TryCompile(
            "这不是合法的 HLSL",
            "main",
            "ps_4_0");

        Assert.Null(bytecode);
    }

    /// <summary>入口点写错时也要失败，不能编译出一个空壳。</summary>
    [Fact]
    public void ReportsFailureForMissingEntryPoint()
    {
        byte[]? bytecode = HlslCompiler.TryCompile(
            GpuConversionShaders.VertexShader,
            "不存在的入口",
            "vs_4_0");

        Assert.Null(bytecode);
    }

    /// <summary>
    /// 编译器本身是否可用。用一段最简着色器探测：
    /// 能编过说明 DLL 在，那么上面那些用例的失败就是真失败。
    /// </summary>
    private static bool IsCompilerAvailable() =>
        HlslCompiler.TryCompile(
            "float4 main() : SV_TARGET { return float4(0,0,0,1); }",
            "main",
            "ps_4_0") != null;
}
