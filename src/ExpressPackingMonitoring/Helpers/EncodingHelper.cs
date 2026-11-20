#nullable disable
using System;
using System.Collections.Generic;
using ExpressPackingMonitoring.ViewModels;

namespace ExpressPackingMonitoring.Helpers
{
    internal static class EncodingHelper
    {
        public static string NormalizeGpuSetting(string setting)
        {
            return (setting?.Trim().ToLowerInvariant()) switch
            {
                "h264_nvenc" or "hevc_nvenc" or "av1_nvenc" or "nvidia" => "nvidia",
                "h264_amf" or "hevc_amf" or "av1_amf" or "amd" => "amd",
                "h264_qsv" or "hevc_qsv" or "av1_qsv" or "intel" => "intel",
                "libx264" or "libx265" or "libsvtav1" or "cpu" => "cpu",
                _ => setting ?? "auto"
            };
        }

        public static string GetCodecFromEncoder(string encoder)
        {
            return encoder switch
            {
                "libx264" or "h264_nvenc" or "h264_amf" or "h264_qsv" => "h264",
                "libx265" or "hevc_nvenc" or "hevc_amf" or "hevc_qsv" => "h265",
                "libsvtav1" or "av1_nvenc" or "av1_amf" or "av1_qsv" => "av1",
                _ => "h264"
            };
        }

        public static string GetCodecLabel(string codec)
        {
            return codec switch
            {
                "h265" => "H.265 / HEVC",
                "av1" => "AV1",
                _ => "H.264"
            };
        }

        public static string GetEncoderLabel(string encoder)
        {
            return encoder switch
            {
                "h264_nvenc" => "N 264",
                "h264_amf" => "A 264",
                "h264_qsv" => "I 264",
                "libx264" => "C 264",
                "hevc_nvenc" => "N 265",
                "hevc_amf" => "A 265",
                "hevc_qsv" => "I 265)",
                "libx265" => "C 265)",
                "av1_nvenc" => "N AV1",
                "av1_amf" => "A AV1",
                "av1_qsv" => "I AV1",
                "libsvtav1" => "C AV1",
                _ => encoder
            };
        }

        private static string ResolveRequestedEncoder(string gpu, string codec)
        {
            gpu = NormalizeGpuSetting(gpu ?? "auto");
            return (gpu, codec) switch
            {
                ("nvidia", "h264") => "h264_nvenc",
                ("nvidia", "h265") => "hevc_nvenc",
                ("nvidia", "av1") => "av1_nvenc",
                ("amd", "h264") => "h264_amf",
                ("amd", "h265") => "hevc_amf",
                ("amd", "av1") => "av1_amf",
                ("intel", "h264") => "h264_qsv",
                ("intel", "h265") => "hevc_qsv",
                ("intel", "av1") => "av1_qsv",
                ("cpu", "h264") => "libx264",
                ("cpu", "h265") => "libx265",
                ("cpu", "av1") => "libsvtav1",
                _ => codec switch
                {
                    "h265" => "hevc_nvenc",
                    "av1" => "av1_nvenc",
                    _ => "h264_nvenc"
                }
            };
        }

        public static bool TryResolveEncoder(
            string gpu,
            string codec,
            ISet<string> validated,
            IReadOnlyDictionary<string, double> scores,
            out string encoder,
            ISet<string> autoEligible = null)
        {
            validated ??= new HashSet<string>();
            autoEligible ??= validated;
            scores ??= new Dictionary<string, double>(
                StringComparer.OrdinalIgnoreCase);
            gpu = NormalizeGpuSetting(gpu ?? "auto");
            codec = NormalizeCodec(codec);
            encoder = "";

            if (gpu == "cpu")
            {
                string requested = ResolveRequestedEncoder(gpu, codec);
                if (validated.Contains(requested))
                {
                    encoder = requested;
                    return true;
                }
                if (codec == "av1")
                {
                    requested = ResolveRequestedEncoder(gpu, "h265");
                    if (validated.Contains(requested))
                    {
                        encoder = requested;
                        return true;
                    }
                }
                return false;
            }

            if (gpu != "auto")
            {
                string requested = ResolveRequestedEncoder(gpu, codec);
                if (validated.Contains(requested))
                {
                    encoder = requested;
                    return true;
                }
                return false;
            }

            IReadOnlyList<string> fallbackCodecs = codec switch
            {
                "av1" => ["av1", "h265", "h264"],
                "h265" => ["h265", "h264"],
                _ => ["h264"]
            };
            if (codec == "av1")
            {
                foreach (string fallbackCodec in fallbackCodecs)
                {
                    string bestHardware = HardwareEncodersForCodec(fallbackCodec)
                        .Where(autoEligible.Contains)
                        .Where(candidate =>
                            scores.TryGetValue(candidate, out double score)
                            && score > 0)
                        .OrderByDescending(candidate => scores[candidate])
                        .ThenBy(candidate => candidate, StringComparer.Ordinal)
                        .FirstOrDefault();
                    if (bestHardware != null)
                    {
                        encoder = bestHardware;
                        return true;
                    }

                    string cpuEncoder = ResolveRequestedEncoder(
                        "cpu",
                        fallbackCodec);
                    if (autoEligible.Contains(cpuEncoder))
                    {
                        encoder = cpuEncoder;
                        return true;
                    }

                    string hardware =
                        HardwareEncodersForCodec(fallbackCodec)
                            .FirstOrDefault(autoEligible.Contains);
                    if (hardware != null)
                    {
                        encoder = hardware;
                        return true;
                    }
                }
                return false;
            }

            foreach (string fallbackCodec in fallbackCodecs)
            {
                string best = HardwareEncodersForCodec(fallbackCodec)
                    .Where(autoEligible.Contains)
                    .Where(candidate =>
                        scores.TryGetValue(candidate, out double score)
                        && score > 0)
                    .OrderByDescending(candidate => scores[candidate])
                    .ThenBy(candidate => candidate, StringComparer.Ordinal)
                    .FirstOrDefault();
                if (best != null)
                {
                    encoder = best;
                    return true;
                }
            }

            // Auto 的首要目标是“能开录”。评分失败只影响排序，不应让已验证硬件
            // 或 CPU 编码器从自动选择中消失。
            foreach (string fallbackCodec in fallbackCodecs)
            {
                string hardware = HardwareEncodersForCodec(fallbackCodec)
                    .FirstOrDefault(autoEligible.Contains);
                if (hardware != null)
                {
                    encoder = hardware;
                    return true;
                }
            }
            foreach (string fallbackCodec in fallbackCodecs)
            {
                string cpuEncoder = ResolveRequestedEncoder("cpu", fallbackCodec);
                if (autoEligible.Contains(cpuEncoder))
                {
                    encoder = cpuEncoder;
                    return true;
                }
            }

            return false;
        }

        public static bool IsExactSelectionAvailable(
            string gpu,
            string codec,
            ISet<string> validated,
            ISet<string> autoEligible = null)
        {
            validated ??= new HashSet<string>();
            autoEligible ??= validated;
            gpu = NormalizeGpuSetting(gpu ?? "auto");
            codec = NormalizeCodec(codec);
            if (gpu == "auto")
            {
                return HardwareEncodersForCodec(codec)
                           .Any(autoEligible.Contains)
                       || autoEligible.Contains(ResolveRequestedEncoder("cpu", codec));
            }

            return validated.Contains(ResolveRequestedEncoder(gpu, codec));
        }

        public static string NormalizeCodec(string codec) =>
            codec?.Trim().ToLowerInvariant() switch
            {
                "h264" or "h265" or "av1" => codec.Trim().ToLowerInvariant(),
                _ => "h264"
            };

        public static IReadOnlyList<string> HardwareEncodersForCodec(string codec) =>
            NormalizeCodec(codec) switch
            {
                "h265" => ["hevc_nvenc", "hevc_amf", "hevc_qsv"],
                "av1" => ["av1_nvenc", "av1_amf", "av1_qsv"],
                _ => ["h264_nvenc", "h264_amf", "h264_qsv"]
            };

    }
}
