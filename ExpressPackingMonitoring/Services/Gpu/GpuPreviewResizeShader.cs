namespace ExpressPackingMonitoring.Services.Gpu;

/// <summary>按像素覆盖面积缩小 BGR24，保持现有 INTER_AREA 预览的抗锯齿效果。</summary>
internal static class GpuPreviewResizeShader
{
    internal const string PixelShader = """
        Texture2D<float> source : register(t0);
        cbuffer Dimensions : register(b0) { float2 sourceSize; float2 targetSize; };

        float4 main(float4 position : SV_POSITION, float2 uv : TEXCOORD0) : SV_TARGET
        {
            float2 scale = sourceSize / targetSize;
            float2 start = floor(position.xy) * scale;
            float2 end = min(start + scale, sourceSize);
            float3 sum = 0;
            for (int y = (int)floor(start.y); y < (int)ceil(end.y); y++)
            {
                float wy = max(0, min(end.y, y + 1.0) - max(start.y, (float)y));
                for (int x = (int)floor(start.x); x < (int)ceil(end.x); x++)
                {
                    float wx = max(0, min(end.x, x + 1.0) - max(start.x, (float)x));
                    int column = min(x, (int)sourceSize.x - 1) * 3;
                    int row = min(y, (int)sourceSize.y - 1);
                    float b = source.Load(int3(column, row, 0));
                    float g = source.Load(int3(column + 1, row, 0));
                    float r = source.Load(int3(column + 2, row, 0));
                    sum += float3(r, g, b) * wx * wy;
                }
            }
            return float4(sum / (scale.x * scale.y), 1);
        }
        """;
}
