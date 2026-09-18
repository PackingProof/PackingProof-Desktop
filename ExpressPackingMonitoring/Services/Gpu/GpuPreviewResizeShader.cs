namespace ExpressPackingMonitoring.Services.Gpu;

/// <summary>轻度缩小使用双三次保留细节，大幅缩小时按像素覆盖面积抗锯齿。</summary>
internal static class GpuPreviewResizeShader
{
    internal const string PixelShader = """
        Texture2D<float> source : register(t0);
        cbuffer Dimensions : register(b0) { float2 sourceSize; float2 targetSize; };

        float cubic(float distance)
        {
            float x = abs(distance);
            if (x <= 1) return (1.25 * x - 2.25) * x * x + 1;
            if (x < 2) return ((-0.75 * x + 3.75) * x - 6) * x + 3;
            return 0;
        }

        float3 readBgr(int2 pixel)
        {
            pixel = clamp(pixel, int2(0, 0), (int2)sourceSize - 1);
            int column = pixel.x * 3;
            return float3(source.Load(int3(column + 2, pixel.y, 0)),
                source.Load(int3(column + 1, pixel.y, 0)),
                source.Load(int3(column, pixel.y, 0)));
        }

        float4 main(float4 position : SV_POSITION, float2 uv : TEXCOORD0) : SV_TARGET
        {
            float2 scale = sourceSize / targetSize;
            if (scale.x < 3 && scale.y < 3)
            {
                float2 center = position.xy * scale - 0.5;
                int2 origin = (int2)floor(center);
                float3 color = 0;
                for (int cy = -1; cy <= 2; cy++)
                    for (int cx = -1; cx <= 2; cx++)
                        color += readBgr(origin + int2(cx, cy))
                            * cubic(center.x - origin.x - cx)
                            * cubic(center.y - origin.y - cy);
                return float4(saturate(color), 1);
            }
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
