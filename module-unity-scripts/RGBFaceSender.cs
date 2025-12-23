using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Networking;

/// <summary>
/// RGB Face Sender
/// 
/// 주기적 얼굴 인식 전송 (EntryDetector_single과 별도):
/// - 설정된 간격(sendIntervalSeconds)마다 RGB 이미지 전송
/// - EntryDetector_single은 진입 시 1회만, 이 컴포넌트는 지속적 전송
/// - BGRA32 → RGB24 변환 + 상하 반전 처리
/// - FaceIdManager에 결과 저장
/// </summary>
public class RGBFaceSender : MonoBehaviour
{
    [Header("Face Recognition Server")]
    [Tooltip("Set via Inspector or env var FACE_SERVER_URL to avoid hardcoding IPs.")]
    public string faceServerUrl = "";
    [SerializeField] private string faceServerUrlEnvVar = "FACE_SERVER_URL";
    [SerializeField] private string defaultFaceServerUrl = "http://localhost:5000/verify";

    [Header("Send Interval (seconds)")]
    public float sendIntervalSeconds = 2.0f;

    [Header("References")]
    public main_single mainController;

    private float lastSendTime = 0f;
    private bool isSending = false;
    private string resolvedFaceServerUrl;

    void Awake()
    {
        resolvedFaceServerUrl = SecretsUtility.ResolveSetting(
            faceServerUrl,
            faceServerUrlEnvVar,
            defaultFaceServerUrl,
            "RGBFaceSender/Face server URL");
    }

    void Update()
    {
        if (mainController == null)
            return;

        // RGB 이미지가 없는 경우 전송 안 함
        if (mainController.lastFrameData == null || mainController.lastFrameData.ColorImageSize <= 0)
            return;

        // 전송 간격 체크
        if (!isSending && Time.time - lastSendTime >= sendIntervalSeconds)
        {
            StartCoroutine(SendRGBFrame());
        }
    }

    IEnumerator SendRGBFrame()
    {
        isSending = true;
        lastSendTime = Time.time;

        if (string.IsNullOrWhiteSpace(resolvedFaceServerUrl))
        {
            Debug.LogError("[RGBFaceSender] Face server URL is not configured. Set via Inspector or env 'FACE_SERVER_URL'.");
            isSending = false;
            yield break;
        }

        byte[] rgbData = null;
        string jsonPayload = null;
        int width = 0, height = 0;
        string bodyTrackingId = "unknown";

        try
        {
            var frame = mainController.lastFrameData;
            width = frame.ColorImageWidth;
            height = frame.ColorImageHeight;

            if (frame.ColorImageSize <= 0 || frame.ColorImage == null)
            {
                isSending = false;
                yield break;
            }

            // 🔄 BGRA32 → RGB24 변환 + 상하 반전
            rgbData = new byte[width * height * 3];

            for (int y = 0; y < height; y++)
            {
                int srcRow = (height - 1 - y); // 반전된 행
                int srcRowStart = srcRow * width * 4; // BGRA (4 bytes)
                int dstRowStart = y * width * 3;      // RGB (3 bytes)

                for (int x = 0; x < width; x++)
                {
                    int srcIdx = srcRowStart + x * 4;
                    int dstIdx = dstRowStart + x * 3;

                    if (srcIdx + 2 < frame.ColorImageSize && dstIdx + 2 < rgbData.Length)
                    {
                        rgbData[dstIdx] = frame.ColorImage[srcIdx + 2]; // R
                        rgbData[dstIdx + 1] = frame.ColorImage[srcIdx + 1]; // G
                        rgbData[dstIdx + 2] = frame.ColorImage[srcIdx];     // B
                    }
                }
            }

            // Base64 인코딩
            string base64Image = Convert.ToBase64String(rgbData);

            // Body tracking ID 가져오기
            if (mainController != null &&
                mainController.GetComponent<main_single>() != null)
            {
                bodyTrackingId = mainController
                    .GetComponent<main_single>()
                    .trackerHandler
                    .currentBodyTrackingID
                    .ToString();
            }

            // JSON payload 구성
            var payload = new VerifyRequest
            {
                image = base64Image,
                width = width,
                height = height,
                body_tracking_id = bodyTrackingId
            };

            jsonPayload = JsonUtility.ToJson(payload);
        }
        catch (Exception e)
        {
            Debug.LogError($"[RGBFaceSender] Exception while preparing data: {e.Message}");
            isSending = false;
            yield break;
        }

        // 서버로 POST
        using (UnityWebRequest www = new UnityWebRequest(resolvedFaceServerUrl, "POST"))
        {
            byte[] bodyRaw = System.Text.Encoding.UTF8.GetBytes(jsonPayload);
            www.uploadHandler = new UploadHandlerRaw(bodyRaw);
            www.downloadHandler = new DownloadHandlerBuffer();
            www.SetRequestHeader("Content-Type", "application/json");

            yield return www.SendWebRequest();

            if (www.result != UnityWebRequest.Result.Success)
            {
                Debug.LogError($"[RGBFaceSender] HTTP Error: {www.error}");
                isSending = false;
                yield break;
            }

            string response = www.downloadHandler.text;
            Debug.Log($"[RGBFaceSender] Response: {response}");

            try
            {
                VerifyResponse res = JsonUtility.FromJson<VerifyResponse>(response);

                if (res.is_registered && !string.IsNullOrEmpty(res.visitor_id))
                {
                    Debug.Log($"[RGBFaceSender] Visitor verified: {res.visitor_id} (sim={res.similarity})");

                    // FaceIdManager 연동
                    if (FaceIdManager.Instance != null)
                    {
                        FaceIdManager.Instance.SetFaceIdDirectly(res.visitor_id);
                    }
                }
                else
                {
                    Debug.Log($"[RGBFaceSender] Unregistered (sim={res.similarity})");
                }
            }
            catch (Exception e)
            {
                Debug.LogError($"[RGBFaceSender] JSON Parse Error: {e.Message}");
            }
        }

        isSending = false;
    }

    [Serializable]
    private class VerifyRequest
    {
        public string image;
        public int width;
        public int height;
        public string body_tracking_id;
    }

    [Serializable]
    private class VerifyResponse
    {
        public string visitor_id;
        public bool is_registered;
        public float similarity;
        public string error;
    }
}
