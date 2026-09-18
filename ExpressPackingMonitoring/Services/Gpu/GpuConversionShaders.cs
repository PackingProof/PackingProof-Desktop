namespace ExpressPackingMonitoring.Services.Gpu;

/// <summary>
/// GPU 转换用的 HLSL 着色器源码。
///
/// 一次 draw 同时完成：YUV 解码（BT.709/BT.601）、色度校正、缩放。
/// 现在这三件事在 CPU 上是三次全帧遍历，1080p@60 下每一遍都是 124 兆像素/秒；
/// 实测整条链路（含系统 CSC 与预览位图）吃掉 2.15 个核心，而 OBS 同样的活只用 0.11 个
/// —— 差别就在于它一帧都不让 CPU 摸像素。
///
/// 源码用逐行数组拼接，不用原始字符串字面量：实测 """ 的缩进剥离会让
/// 编译器收到残缺的源码并报 "syntax error: unexpected end of file"，
/// 而那个错误看起来像着色器本身写错了，极易误导。
/// </summary>
internal static class GpuConversionShaders
{
    /// <summary>
    /// 顶点着色器：画一个铺满渲染目标的三角形带。
    ///
    /// 不用顶点缓冲，直接由顶点 ID 算出位置 —— 少一个资源要管，
    /// 也省掉每帧绑定顶点缓冲的开销。
    /// </summary>
    internal static readonly string VertexShader = string.Join('\n',
    [
        "struct VsOut",
        "{",
        "    float4 position : SV_POSITION;",
        "    float2 uv : TEXCOORD0;",
        "};",
        "",
        "VsOut main(uint vertexId : SV_VertexID)",
        "{",
        "    // Two triangles covering the whole viewport: (0,0) (1,0) (0,1) (1,1)",
        "    float2 corner = float2(vertexId & 1, (vertexId >> 1) & 1);",
        "    VsOut output;",
        "    // Clip space is [-1,1], UV is [0,1], Y axis inverted.",
        "    output.position = float4(corner.x * 2.0 - 1.0, 1.0 - corner.y * 2.0, 0.0, 1.0);",
        "    output.uv = corner;",
        "    return output;",
        "}",
    ]);

    /// <summary>
    /// 共用的 YUV→RGB。
    ///
    /// 系数直接作用在 0..255 域上，不先归一化：归一化写法里"色度除以 224"
    /// 得到 ±0.5 范围，而常见的 1.5748/1.8556 那组系数要求 ±1.0，配错了
    /// 只在彩色区域偏色、灰阶完全正常，极难发现（第一版就栽在这里，G 通道差 20~40）。
    /// </summary>
    private static readonly string YuvToRgbFunction = string.Join('\n',
    [
        "float3 YuvToRgb(float y, float u, float v, bool bt709)",
        "{",
        "    float yy = y * 255.0 - 16.0;",
        "    float uu = u * 255.0 - 128.0;",
        "    float vv = v * 255.0 - 128.0;",
        "",
        "    float3 rgb;",
        "    if (bt709)",
        "    {",
        "        rgb.r = 1.164383 * yy + 1.792741 * vv;",
        "        rgb.g = 1.164383 * yy - 0.213249 * uu - 0.532909 * vv;",
        "        rgb.b = 1.164383 * yy + 2.112402 * uu;",
        "    }",
        "    else",
        "    {",
        "        rgb.r = 1.164383 * yy + 1.596027 * vv;",
        "        rgb.g = 1.164383 * yy - 0.391762 * uu - 0.812968 * vv;",
        "        rgb.b = 1.164383 * yy + 2.017232 * uu;",
        "    }",
        "    return saturate(rgb / 255.0);",
        "}",
    ]);

    /// <summary>
    /// YUY2 像素着色器。
    ///
    /// YUY2 每两个像素共用一组色度，内存里是 Y0 U Y1 V。按 R8G8 纹理上传后，
    /// 一个纹素就是一个像素的 (Y, 色度)，但色度是 U 还是 V 取决于像素奇偶：
    /// 偶数像素带 U、奇数像素带 V，所以要采相邻纹素补齐另一半。
    ///
    /// 解包必须用 Load 精确取纹素，不能用采样器插值 ——
    /// 对 YUY2 做双线性会把 U 和 V 混在一起。缩放由渲染目标尺寸与 UV 自然表达。
    /// </summary>
    internal static readonly string Yuy2PixelShader = string.Join('\n',
    [
        "Texture2D<float2> packed : register(t0);",
        "SamplerState texSampler : register(s0);",
        "",
        "cbuffer Params : register(b0)",
        "{",
        "    // x=source width, y=source height, z=use BT.709 (1/0), w=reserved",
        "    float4 sourceInfo;",
        "};",
        "",
        "struct VsOut",
        "{",
        "    float4 position : SV_POSITION;",
        "    float2 uv : TEXCOORD0;",
        "};",
        "",
        YuvToRgbFunction,
        "",
        "float4 main(VsOut input) : SV_TARGET",
        "{",
        "    bool bt709 = sourceInfo.z > 0.5;",
        "",
        "    // Map this output pixel to a source column, then check its parity.",
        "    int column = (int)floor(input.uv.x * sourceInfo.x);",
        "    int row = (int)floor(input.uv.y * sourceInfo.y);",
        "    int pairBase = column & ~1;",
        "",
        "    // Two texels of a pair: even column carries U, odd column carries V.",
        "    float2 texelEven = packed.Load(int3(pairBase, row, 0));",
        "    float2 texelOdd = packed.Load(int3(pairBase + 1, row, 0));",
        "",
        "    float luma = ((column & 1) == 0) ? texelEven.r : texelOdd.r;",
        "    return float4(YuvToRgb(luma, texelEven.g, texelOdd.g, bt709), 1.0);",
        "}",
    ]);

    /// <summary>
    /// NV12 像素着色器。亮度是一整面，色度是半尺寸的双通道面。
    /// 两个面分别作为纹理绑定，采样器天然完成色度的双线性上采样。
    /// </summary>
    internal static readonly string Nv12PixelShader = string.Join('\n',
    [
        "Texture2D<float> luma : register(t0);",
        "Texture2D<float2> chroma : register(t1);",
        "SamplerState texSampler : register(s0);",
        "",
        "cbuffer Params : register(b0)",
        "{",
        "    float4 sourceInfo;",
        "};",
        "",
        "struct VsOut",
        "{",
        "    float4 position : SV_POSITION;",
        "    float2 uv : TEXCOORD0;",
        "};",
        "",
        YuvToRgbFunction,
        "",
        "float4 main(VsOut input) : SV_TARGET",
        "{",
        "    bool bt709 = sourceInfo.z > 0.5;",
        "    float y = luma.Sample(texSampler, input.uv).r;",
        "    float2 uv = chroma.Sample(texSampler, input.uv);",
        "    return float4(YuvToRgb(y, uv.x, uv.y, bt709), 1.0);",
        "}",
    ]);
}
