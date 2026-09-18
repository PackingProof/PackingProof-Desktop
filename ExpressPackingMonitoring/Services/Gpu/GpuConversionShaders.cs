namespace ExpressPackingMonitoring.Services.Gpu;

/// <summary>
/// GPU 转换用的 HLSL 着色器源码。
///
/// 一次 draw 同时完成：YUV 解码（BT.709/BT.601）、色度校正、缩放。
/// 现在这三件事在 CPU 上是三次全帧遍历，1080p@60 下每一遍都是 124 兆像素/秒；
/// 实测整条链路（含系统 CSC 与预览位图）吃掉 2.15 个核心，而 OBS 同样的活只用 0.11 个
/// —— 差别就在于它一帧都不让 CPU 摸像素。
///
/// 着色器在运行时用 D3DCompiler 编译：预编译需要把 fxc 塞进构建流程，
/// 而这段代码很短、只在摄像头启动时编译一次，运行时编译更简单也不影响帧率。
/// </summary>
internal static class GpuConversionShaders
{
    /// <summary>
    /// 顶点着色器：画一个铺满渲染目标的三角形带。
    ///
    /// 不用顶点缓冲，直接由顶点 ID 算出位置 —— 少一个资源要管，
    /// 也省掉每帧绑定顶点缓冲的开销。
    /// </summary>
    internal const string VertexShader = """
        struct VsOut
        {
            float4 position : SV_POSITION;
            float2 uv : TEXCOORD0;
        };

        VsOut main(uint vertexId : SV_VertexID)
        {
            // 两个三角形铺满整个视口：(0,0) (1,0) (0,1) (1,1)
            float2 corner = float2((vertexId & 1), (vertexId >> 1) & 1);
            VsOut output;
            // 裁剪空间是 [-1,1]，纹理坐标是 [0,1]，且 Y 方向相反。
            output.position = float4(corner.x * 2.0 - 1.0, 1.0 - corner.y * 2.0, 0.0, 1.0);
            output.uv = corner;
            return output;
        }
        """;

    /// <summary>
    /// YUY2 像素着色器。
    ///
    /// YUY2 每两个像素共用一组色度，内存里是 Y0 U Y1 V。按 R8G8 纹理上传后，
    /// 一个纹素就是一个像素的 (Y, 色度)，但色度是 U 还是 V 取决于像素的奇偶：
    /// 偶数像素带 U、奇数像素带 V，所以要采样相邻纹素补齐另一半。
    ///
    /// 缩放由采样器的双线性插值顺带完成 —— 但只能在解码之后做，
    /// 直接对 YUY2 做双线性会把 U 和 V 混在一起。所以这里先解码到 RGB，
    /// 缩放交给渲染目标尺寸与采样器（源尺寸 → 目标尺寸的比例由 UV 自然表达）。
    /// </summary>
    internal const string Yuy2PixelShader = """
        Texture2D<float2> packed : register(t0);
        SamplerState pointSampler : register(s0);

        cbuffer Params : register(b0)
        {
            // x=源宽, y=源高, z=是否 BT.709(1/0), w=保留
            float4 sourceInfo;
        };

        struct VsOut
        {
            float4 position : SV_POSITION;
            float2 uv : TEXCOORD0;
        };

        float3 YuvToRgb(float y, float u, float v, bool bt709)
        {
            // 直接在 0..255 域上用标准解码系数，避免"先归一化再乘系数"时
            // 归一化尺度与系数不配套（±0.5 还是 ±1.0）的歧义 ——
            // 那正是这里最容易写错、且只在彩色区域才看得出来的地方。
            float yy = y * 255.0 - 16.0;
            float uu = u * 255.0 - 128.0;
            float vv = v * 255.0 - 128.0;

            float3 rgb;
            if (bt709)
            {
                rgb.r = 1.164383 * yy + 1.792741 * vv;
                rgb.g = 1.164383 * yy - 0.213249 * uu - 0.532909 * vv;
                rgb.b = 1.164383 * yy + 2.112402 * uu;
            }
            else
            {
                rgb.r = 1.164383 * yy + 1.596027 * vv;
                rgb.g = 1.164383 * yy - 0.391762 * uu - 0.812968 * vv;
                rgb.b = 1.164383 * yy + 2.017232 * uu;
            }
            return saturate(rgb / 255.0);
        }

        float4 main(VsOut input) : SV_TARGET
        {
            float sourceWidth = sourceInfo.x;
            bool bt709 = sourceInfo.z > 0.5;

            // 先算出这个输出像素对应的源像素列，再据此判断奇偶。
            float sourceX = input.uv.x * sourceWidth;
            int column = (int)floor(sourceX);
            int pairBase = column & ~1;

            // 一对像素的两个纹素：偶数列带 U，奇数列带 V。
            float2 texelEven = packed.Load(int3(pairBase, (int)(input.uv.y * sourceInfo.y), 0));
            float2 texelOdd = packed.Load(int3(pairBase + 1, (int)(input.uv.y * sourceInfo.y), 0));

            float luma = ((column & 1) == 0) ? texelEven.r : texelOdd.r;
            float chromaU = texelEven.g;
            float chromaV = texelOdd.g;

            return float4(YuvToRgb(luma, chromaU, chromaV, bt709), 1.0);
        }
        """;

    /// <summary>
    /// NV12 像素着色器。亮度是一整面，色度是半尺寸的双通道面。
    /// 两个面分别作为纹理绑定，采样器天然完成色度的双线性上采样。
    /// </summary>
    internal const string Nv12PixelShader = """
        Texture2D<float> luma : register(t0);
        Texture2D<float2> chroma : register(t1);
        SamplerState linearSampler : register(s0);

        cbuffer Params : register(b0)
        {
            float4 sourceInfo;
        };

        struct VsOut
        {
            float4 position : SV_POSITION;
            float2 uv : TEXCOORD0;
        };

        float4 main(VsOut input) : SV_TARGET
        {
            bool bt709 = sourceInfo.z > 0.5;

            float y = luma.Sample(linearSampler, input.uv).r;
            float2 uv = chroma.Sample(linearSampler, input.uv);

            // 与 YUY2 着色器共用同一套系数，写法也保持一致，
            // 避免两个着色器的颜色表现出现偏差。
            float yy = y * 255.0 - 16.0;
            float uu = uv.x * 255.0 - 128.0;
            float vv = uv.y * 255.0 - 128.0;

            float3 rgb;
            if (bt709)
            {
                rgb.r = 1.164383 * yy + 1.792741 * vv;
                rgb.g = 1.164383 * yy - 0.213249 * uu - 0.532909 * vv;
                rgb.b = 1.164383 * yy + 2.112402 * uu;
            }
            else
            {
                rgb.r = 1.164383 * yy + 1.596027 * vv;
                rgb.g = 1.164383 * yy - 0.391762 * uu - 0.812968 * vv;
                rgb.b = 1.164383 * yy + 2.017232 * uu;
            }
            return float4(saturate(rgb / 255.0), 1.0);
        }
        """;
}
