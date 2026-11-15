namespace ExpressPackingMonitoring.Services;

/// <summary>
/// 识别状态到界面状态的纯映射。拆出识别服务，避免服务文件继续膨胀。
/// </summary>
internal static class CameraBarcodeStatusFactory
{
    internal static CameraBarcodeRecognitionStatus CreateStatus(
        CameraBarcodeObservation observation,
        bool reportVisibleCodes,
        CameraBarcodeGeometry? geometry = null)
    {
        if (observation.ConfirmedCode.Length > 0)
        {
            return new CameraBarcodeRecognitionStatus(
                CameraBarcodeRecognitionState.Confirmed,
                observation.ConfirmedCode,
                geometry);
        }

        if (reportVisibleCodes && observation.VisibleCode.Length > 0)
        {
            return new CameraBarcodeRecognitionStatus(
                CameraBarcodeRecognitionState.Visible,
                observation.VisibleCode,
                geometry);
        }

        if (observation.CandidateCode.Length > 0)
        {
            return new CameraBarcodeRecognitionStatus(
                CameraBarcodeRecognitionState.Candidate,
                observation.CandidateCode,
                geometry);
        }

        return new CameraBarcodeRecognitionStatus(CameraBarcodeRecognitionState.Idle);
    }
}
