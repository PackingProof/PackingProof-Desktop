#nullable disable
using System;
using System.Collections.Generic;
using System.Linq;

namespace ExpressPackingMonitoring.Helpers
{
    internal static class EncodingHelper
    {
        internal sealed record EncoderCandidate(
            string Encoder,
            string Codec,
            bool IsHardware,
            double MeasuredEncodingFps,
            bool MeetsRealtimeRequirement);

        internal sealed record EncoderSelection(
            string Encoder,
            bool MeetsRealtimeRequirement,
            bool IsManual,
            string Reason);

        public static string GetCodecFromEncoder(string encoder)
        {
            return encoder switch
            {
                "libx264" or "h264_nvenc" or "h264_amf" or "h264_qsv" => "h264",
                "libx265" or "hevc_nvenc" or "hevc_amf" or "hevc_qsv" => "h265",
                "libsvtav1" or "libaom-av1" or "av1_nvenc" or "av1_amf" or "av1_qsv" => "av1",
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
                "hevc_qsv" => "I 265",
                "libx265" => "C 265",
                "av1_nvenc" => "N AV1",
                "av1_amf" => "A AV1",
                "av1_qsv" => "I AV1",
                "libsvtav1" => "C AV1",
                "libaom-av1" => "C AV1 (libaom)",
                _ => encoder
            };
        }

        public static bool IsKnownEncoder(string encoder)
        {
            return (encoder?.Trim().ToLowerInvariant()) switch
            {
                "h264_nvenc" or "hevc_nvenc" or "av1_nvenc"
                    or "h264_amf" or "hevc_amf" or "av1_amf"
                    or "h264_qsv" or "hevc_qsv" or "av1_qsv"
                    or "libx264" or "libx265" or "libsvtav1" or "libaom-av1" => true,
                _ => false
            };
        }

        public static bool IsHardwareEncoder(string encoder)
        {
            return IsKnownEncoder(encoder)
                && !(encoder!.StartsWith("lib", StringComparison.OrdinalIgnoreCase));
        }

        public static EncoderSelection SelectEncoder(
            IEnumerable<EncoderCandidate> candidates,
            string selectionMode,
            string manualEncoder)
        {
            List<EncoderCandidate> all = candidates?
                .Where(candidate => IsKnownEncoder(candidate.Encoder))
                .ToList()
                ?? [];
            bool manual = string.Equals(selectionMode, "manual", StringComparison.OrdinalIgnoreCase);
            string normalizedManual = manualEncoder?.Trim().ToLowerInvariant() ?? "";
            if (manual)
            {
                EncoderCandidate selected = all.FirstOrDefault(candidate =>
                    string.Equals(candidate.Encoder, normalizedManual, StringComparison.OrdinalIgnoreCase));
                if (selected == null || !selected.MeetsRealtimeRequirement)
                    return null;

                return new EncoderSelection(
                    selected.Encoder,
                    true,
                    true,
                    "已保留手动选择的编码器");
            }

            List<EncoderCandidate> automatic = all
                .Where(candidate => candidate.Codec is "h264" or "h265")
                .ToList();
            List<EncoderCandidate> qualified = automatic
                .Where(candidate => candidate.MeetsRealtimeRequirement)
                .ToList();

            EncoderCandidate preferred = PickFastest(qualified.Where(candidate =>
                candidate.IsHardware && candidate.Codec == "h265"));
            if (preferred != null)
                return CreateAutomaticSelection(preferred, "已选择实测最快的硬件 H.265 编码器");

            preferred = PickFastest(qualified.Where(candidate =>
                candidate.IsHardware && candidate.Codec == "h264"));
            if (preferred != null)
                return CreateAutomaticSelection(preferred, "已选择实测最快的硬件 H.264 编码器");

            preferred = PickFastest(qualified.Where(candidate => !candidate.IsHardware));
            if (preferred != null)
                return CreateAutomaticSelection(preferred, "没有满足余量的硬件编码器，已选择实测最快的 CPU 编码器");

            preferred = PickFastest(automatic);
            return preferred == null
                ? null
                : new EncoderSelection(
                    preferred.Encoder,
                    false,
                    false,
                    "没有编码器满足 20% 实时余量，已选择实测最快的编码器");
        }

        private static EncoderCandidate PickFastest(IEnumerable<EncoderCandidate> candidates)
        {
            return candidates
                .OrderByDescending(candidate => candidate.MeasuredEncodingFps)
                .ThenBy(candidate => candidate.Encoder, StringComparer.OrdinalIgnoreCase)
                .FirstOrDefault();
        }

        private static EncoderSelection CreateAutomaticSelection(EncoderCandidate candidate, string reason)
        {
            return new EncoderSelection(candidate.Encoder, true, false, reason);
        }
    }
}
