using System;
using System.Collections;
using System.IO;
using UnityEngine;
using UnityEngine.Networking;
using UnityEngine.Events;

/// <summary>
/// Single Person Entry Detector
/// - Body 감지 시 1회만 얼굴 인식
/// - 등록된 visitor면 승인, 아니면 거부
/// - RGB 이미지 로컬 저장 기능 추가 🆕
/// </summary>
public class EntryDetector_single : MonoBehaviour
{
    [Header("Face Recognition Server")]
    [Tooltip("Set via Inspector or env var FACE_RECOGNITION_URL to avoid hardcoding IPs.")]
    public string faceRecognitionUrl = "";
    [SerializeField] private string faceRecognitionUrlEnvVar = "FACE_RECOGNITION_URL";
    [SerializeField] private string defaultFaceRecognitionUrl = "http://localhost:5000/verify";

    [Header("Retry Settings")]
    public int maxRetries = 3;
    public float retryDelay = 1.0f;

    [Header("🆕 Debug Settings")]
    public bool saveDebugImages = true; // Inspector에서 ON/OFF
    public string debugImagePath = ""; // 비어있으면 Application.dataPath/../DebugImages 사용


    [Header("References")]
    public main_single mainController;

    // 콜백 이벤트
    [HideInInspector] public UnityAction<string> onVerificationSuccess;
    [HideInInspector] public UnityAction onVerificationFailed;

    private bool isProcessing = false;
    private bool hasProcessed = false;
    private string actualDebugPath;
    private string resolvedFaceRecognitionUrl;

    void Awake()
    {
        resolvedFaceRecognitionUrl = SecretsUtility.ResolveSetting(
            faceRecognitionUrl,
            faceRecognitionUrlEnvVar,
            defaultFaceRecognitionUrl,
            "EntryDetector_single/Face recognition URL");
    }

    void Start()
    {
        // Debug 이미지 저장 경로 설정
        if (string.IsNullOrEmpty(debugImagePath))
        {
            actualDebugPath = Path.Combine(Application.dataPath, "..", "DebugImages");
        }
        else
        {
            actualDebugPath = debugImagePath;
        }

        // 디렉토리 생성
        if (saveDebugImages && !Directory.Exists(actualDebugPath))
        {
            Directory.CreateDirectory(actualDebugPath);
            Debug.Log($"[EntryDetector_single] Created debug image directory: {actualDebugPath}");
        }
    }

    /// <summary>
    /// main_single이 새 body 감지 시 호출
    /// </summary>
    public void OnBodyDetected(ulong bodyTrackingID)
    {
        if (isProcessing || hasProcessed)
        {
            Debug.Log($"[EntryDetector_single] Already processing/processed (ID: {bodyTrackingID})");
            return;
        }

        Debug.Log($"[EntryDetector_single] New body detected: {bodyTrackingID}");
        StartCoroutine(RecognizeFaceWithRetry(bodyTrackingID, maxRetries));
    }

    /// <summary>
    /// 재시도 로직 포함 얼굴 인식
    /// </summary>
    IEnumerator RecognizeFaceWithRetry(ulong bodyTrackingID, int retriesLeft)
    {
        isProcessing = true;
        if (string.IsNullOrWhiteSpace(resolvedFaceRecognitionUrl))
        {
            Debug.LogError("[EntryDetector_single] Face recognition URL is not configured. Set via Inspector or env 'FACE_RECOGNITION_URL'.");
            isProcessing = false;
            yield break;
        }

        Debug.Log($"[EntryDetector_single] Starting face recognition (retries left: {retriesLeft})");

        // RGB 이미지 가져오기
        byte[] rgbData = GetRGBImageFromKinect();

        if (rgbData == null || rgbData.Length == 0)
        {
            Debug.LogWarning("[EntryDetector_single] No RGB data available");
            yield return HandleRetryOrFail(bodyTrackingID, retriesLeft);
            yield break;
        }

        // 🆕 이미지 저장 (디버깅용)
        if (saveDebugImages)
        {
            SaveRGBImageToPNG(rgbData,
                mainController.lastFrameData.ColorImageWidth,
                mainController.lastFrameData.ColorImageHeight,
                bodyTrackingID,
                retriesLeft);
        }

        // Base64 인코딩
        string base64Image = Convert.ToBase64String(rgbData);

        // JSON 페이로드
        var payload = new FaceVerificationRequest
        {
            image = base64Image,
            width = mainController.lastFrameData.ColorImageWidth,
            height = mainController.lastFrameData.ColorImageHeight,
            body_tracking_id = bodyTrackingID.ToString()
        };

        string jsonPayload = JsonUtility.ToJson(payload);

        // HTTP POST 요청
        using (UnityWebRequest www = new UnityWebRequest(resolvedFaceRecognitionUrl, "POST"))
        {
            byte[] bodyRaw = System.Text.Encoding.UTF8.GetBytes(jsonPayload);
            www.uploadHandler = new UploadHandlerRaw(bodyRaw);
            www.downloadHandler = new DownloadHandlerBuffer();
            www.SetRequestHeader("Content-Type", "application/json");

            yield return www.SendWebRequest();

            bool shouldRetry = false;

            if (www.result == UnityWebRequest.Result.Success)
            {
                string response = www.downloadHandler.text;
                Debug.Log($"[EntryDetector_single] Response: {response}");

                try
                {
                    FaceVerificationResponse res = JsonUtility.FromJson<FaceVerificationResponse>(response);

                    if (res.is_registered && !string.IsNullOrEmpty(res.visitor_id))
                    {
                        OnRecognitionSuccess(res.visitor_id);
                    }
                    else
                    {
                        Debug.Log("[EntryDetector_single] Unregistered visitor");
                        OnRecognitionFailed();
                    }
                }
                catch (Exception e)
                {
                    Debug.LogError($"[EntryDetector_single] Failed to parse response: {e.Message}");
                    shouldRetry = true;
                }
            }
            else
            {
                Debug.LogError($"[EntryDetector_single] HTTP Error: {www.error}");
                shouldRetry = true;
            }

            if (shouldRetry)
            {
                yield return HandleRetryOrFail(bodyTrackingID, retriesLeft);
            }
        }
    }

    /// <summary>
    /// 재시도 또는 실패 처리
    /// </summary>
    IEnumerator HandleRetryOrFail(ulong bodyTrackingID, int retriesLeft)
    {
        if (retriesLeft > 0)
        {
            Debug.Log($"[EntryDetector_single] Retrying... ({retriesLeft - 1} left)");
            yield return new WaitForSeconds(retryDelay);
            yield return RecognizeFaceWithRetry(bodyTrackingID, retriesLeft - 1);
        }
        else
        {
            Debug.LogWarning("[EntryDetector_single] Max retries reached");
            OnRecognitionFailed();
        }
    }

    /// <summary>
    /// Azure Kinect RGB 이미지 추출
    /// BGRA32 → RGB24 변환 + 🔄 상하 반전
    /// </summary>
    private byte[] GetRGBImageFromKinect()
    {
        if (mainController == null || mainController.lastFrameData == null)
            return null;

        var frameData = mainController.lastFrameData;

        if (frameData.ColorImageSize <= 0)
            return null;

        int width = frameData.ColorImageWidth;
        int height = frameData.ColorImageHeight;
        byte[] colorData = frameData.ColorImage;

        // BGRA32 → RGB24 변환 + 상하 반전
        byte[] rgbData = new byte[width * height * 3];

        // 🔄 아래에서 위로 읽기 (상하 반전)
        for (int y = 0; y < height; y++)
        {
            int srcRow = (height - 1 - y); // 반전된 행
            int srcRowStart = srcRow * width * 4; // BGRA (4 bytes)
            int dstRowStart = y * width * 3;      // RGB (3 bytes)

            for (int x = 0; x < width; x++)
            {
                int srcIdx = srcRowStart + x * 4;
                int dstIdx = dstRowStart + x * 3;

                if (srcIdx + 2 < frameData.ColorImageSize && dstIdx + 2 < rgbData.Length)
                {
                    rgbData[dstIdx] = colorData[srcIdx + 2]; // R
                    rgbData[dstIdx + 1] = colorData[srcIdx + 1]; // G
                    rgbData[dstIdx + 2] = colorData[srcIdx];     // B
                }
            }
        }

        return rgbData;
    }

    /// <summary>
    /// 🆕 RGB 이미지를 PNG로 저장
    /// </summary>
    private void SaveRGBImageToPNG(byte[] rgbData, int width, int height, ulong bodyTrackingID, int retriesLeft)
    {
        try
        {
            // Texture2D 생성 (RGB24 포맷)
            Texture2D texture = new Texture2D(width, height, TextureFormat.RGB24, false);

            // RGB 데이터 로드
            texture.LoadRawTextureData(rgbData);
            texture.Apply();

            // PNG로 인코딩
            byte[] pngData = texture.EncodeToPNG();

            // 파일명 생성 (타임스탬프 + bodyID + retry)
            string timestamp = System.DateTime.Now.ToString("yyyyMMdd_HHmmss_fff");
            string filename = $"rgb_body{bodyTrackingID}_retry{maxRetries - retriesLeft}_{timestamp}.png";
            string fullPath = Path.Combine(actualDebugPath, filename);

            // 파일 저장
            File.WriteAllBytes(fullPath, pngData);

            Debug.Log($"✅ [EntryDetector_single] Saved debug image: {fullPath}");
            Debug.Log($"   → Size: {width}x{height}, Data: {rgbData.Length} bytes, PNG: {pngData.Length} bytes");

            // 메모리 해제
            Destroy(texture);
        }
        catch (Exception e)
        {
            Debug.LogError($"[EntryDetector_single] Failed to save debug image: {e.Message}");
        }
    }

    /// <summary>
    /// 얼굴 인식 성공
    /// </summary>
    private void OnRecognitionSuccess(string visitorId)
    {
        Debug.Log($"✅ [EntryDetector_single] Verification SUCCESS: {visitorId}");

        hasProcessed = true;
        isProcessing = false;

        if (FaceIdManager.Instance != null)
        {
            FaceIdManager.Instance.SetFaceIdDirectly(visitorId);
        }

        onVerificationSuccess?.Invoke(visitorId);
    }

    /// <summary>
    /// 얼굴 인식 실패
    /// </summary>
    private void OnRecognitionFailed()
    {
        Debug.LogWarning("❌ [EntryDetector_single] Verification FAILED");

        hasProcessed = true;
        isProcessing = false;

        onVerificationFailed?.Invoke();
    }

    /// <summary>
    /// 리셋
    /// </summary>
    public void Reset()
    {
        hasProcessed = false;
        isProcessing = false;
        Debug.Log("[EntryDetector_single] Reset for next visitor");
    }

    [Serializable]
    private class FaceVerificationRequest
    {
        public string image;
        public int width;
        public int height;
        public string body_tracking_id;
    }

    [Serializable]
    private class FaceVerificationResponse
    {
        public string visitor_id;
        public bool is_registered;
        public float similarity;
        public string error;
    }
}
