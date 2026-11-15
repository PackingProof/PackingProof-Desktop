using ExpressPackingMonitoring.Vision;

namespace ExpressPackingMonitoring.Services;

/// <summary>
/// 从一帧 OBB 检测里只挑一个最值得解码的 Barcode 候选。
/// 先做硬条件（有 Waybill、Barcode 在 Waybill 内、对角线达标），再按
/// 置信度、画幅占比和面单面积占比排序；不再把所有候选都送进 ZXing。
/// </summary>
internal static class VisionObbCandidatePolicy
{
    internal static VisionObbDetection? SelectBestDecodeCandidate(
        IReadOnlyList<VisionObbDetection> detections,
        int frameWidth,
        int frameHeight,
        bool qrCodeEnabled,
        bool hasBarcodeClassSupport)
    {
        IReadOnlyList<VisionObbDetection> candidates =
            SelectBestDecodeCandidates(
                detections,
                frameWidth,
                frameHeight,
                qrCodeEnabled,
                hasBarcodeClassSupport,
                maxCandidates: 1);
        return candidates.Count > 0 ? candidates[0] : null;
    }

    internal static IReadOnlyList<VisionObbDetection> SelectBestDecodeCandidates(
        IReadOnlyList<VisionObbDetection> detections,
        int frameWidth,
        int frameHeight,
        bool qrCodeEnabled,
        bool hasBarcodeClassSupport,
        int maxCandidates)
    {
        if (detections.Count == 0 || frameWidth <= 0 || frameHeight <= 0)
            return [];

        if (!hasBarcodeClassSupport)
        {
            VisionObbDetection? legacy = SelectBestLegacyDetection(detections);
            return legacy is { } detection ? [detection] : [];
        }

        VisionObbDetection[] waybills = detections
            .Where(detection => VisionObbClassPolicy.IsWaybill(detection))
            .ToArray();
        VisionObbDetection[] barcodes = detections
            .Where(detection => VisionObbClassPolicy.IsBarcode(detection))
            .ToArray();
        if (barcodes.Length == 0 || waybills.Length == 0)
            return [];

        var ranked = new List<(VisionObbDetection Barcode, double Score)>();
        foreach (VisionObbDetection barcode in barcodes)
        {
            int width = Math.Max(1, (int)Math.Round(barcode.Width));
            int height = Math.Max(1, (int)Math.Round(barcode.Height));
            if (!CameraBarcodeDecodeSizePolicy.ShouldDecode(
                    width,
                    height,
                    frameWidth,
                    frameHeight,
                    qrCodeEnabled))
            {
                continue;
            }

            VisionObbDetection? containing = waybills
                .Where(waybill =>
                    VisionObbGeometry.ContainsWithCenterAndThreeCorners(
                        waybill,
                        barcode))
                .OrderByDescending(waybill => waybill.Score)
                .ThenByDescending(waybill => waybill.Width * waybill.Height)
                .Cast<VisionObbDetection?>()
                .FirstOrDefault();
            if (containing is not { } waybill)
                continue;

            ranked.Add((barcode, CalculateScore(
                barcode,
                waybill,
                frameWidth,
                frameHeight,
                width,
                height)));
        }

        return ranked
            .OrderByDescending(item => item.Score)
            .Take(Math.Max(1, maxCandidates))
            .Select(item => item.Barcode)
            .ToArray();
    }

    internal static double CalculateScore(
        in VisionObbDetection barcode,
        in VisionObbDetection waybill,
        int frameWidth,
        int frameHeight,
        int barcodeWidth,
        int barcodeHeight)
    {
        double confidence = NormalizeConfidence(barcode.Score);
        double waybillConfidence = NormalizeConfidence(waybill.Score);
        double diagonalRatio = CameraBarcodeDecodeSizePolicy.FrameDiagonalRatio(
            barcodeWidth,
            barcodeHeight,
            frameWidth,
            frameHeight);
        double sizeScore = Math.Clamp(diagonalRatio / 0.20, 0, 1);

        double barcodeArea = Math.Max(1, barcode.Width * barcode.Height);
        double waybillArea = Math.Max(1, waybill.Width * waybill.Height);
        double areaRatio = Math.Clamp(barcodeArea / waybillArea, 0, 1);
        // 面单上的条码通常只占面单的一小块；过小是噪点，过大可能框错。
        double areaScore = areaRatio <= 0.15
            ? Math.Clamp(areaRatio / 0.08, 0, 1)
            : Math.Clamp((0.30 - areaRatio) / 0.15, 0, 1);

        double waybillDiagonal = Math.Max(
            1,
            Math.Sqrt(
                waybill.Width * waybill.Width
                + waybill.Height * waybill.Height));
        double centerDistance = Math.Sqrt(
            Math.Pow(barcode.CenterX - waybill.CenterX, 2)
            + Math.Pow(barcode.CenterY - waybill.CenterY, 2));
        double centerScore = Math.Clamp(
            1.0 - centerDistance / (0.5 * waybillDiagonal),
            0,
            1);

        return 0.45 * confidence
               + 0.25 * waybillConfidence
               + 0.20 * sizeScore
               + 0.05 * areaScore
               + 0.05 * centerScore;
    }

    private static VisionObbDetection? SelectBestLegacyDetection(
        IReadOnlyList<VisionObbDetection> detections) =>
        detections
            .OrderByDescending(detection =>
                NormalizeConfidence(detection.Score)
                + Math.Clamp(detection.Width * detection.Height / 1_000_000.0, 0, 1))
            .Cast<VisionObbDetection?>()
            .FirstOrDefault();

    private static double NormalizeConfidence(float score)
    {
        double range = Math.Max(0.0001, 1.0 - VisionObbModelPolicy.ScoreThreshold);
        return Math.Clamp(
            (score - VisionObbModelPolicy.ScoreThreshold) / range,
            0,
            1);
    }
}
