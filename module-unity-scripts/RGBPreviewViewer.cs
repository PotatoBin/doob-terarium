using UnityEngine;
using UnityEngine.UI;

/// <summary>
/// Azure Kinect RGB를 Unity UI에 실시간 표시
/// Canvas > RawImage에 연결
/// </summary>
public class RGBPreviewViewer : MonoBehaviour
{
    [Header("References")]
    public main_single mainController;
    public RawImage previewImage; // Canvas의 RawImage

    [Header("Settings")]
    public bool enablePreview = true;
    public float updateInterval = 0.1f; // 초당 10프레임

    private Texture2D rgbTexture;
    private float lastUpdateTime = 0f;

    void Start()
    {
        if (mainController == null)
        {
            mainController = FindObjectOfType<main_single>();
        }

        if (previewImage == null)
        {
            Debug.LogWarning("[RGBPreviewViewer] No RawImage assigned!");
        }
    }

    void Update()
    {
        if (!enablePreview || previewImage == null || mainController == null)
            return;

        // 업데이트 간격 체크
        if (Time.time - lastUpdateTime < updateInterval)
            return;

        lastUpdateTime = Time.time;

        // RGB 데이터 가져오기
        var frameData = mainController.lastFrameData;
        if (frameData == null || frameData.ColorImageSize <= 0)
            return;

        int width = frameData.ColorImageWidth;
        int height = frameData.ColorImageHeight;
        byte[] colorData = frameData.ColorImage;

        // Texture 생성 (첫 프레임만)
        if (rgbTexture == null || rgbTexture.width != width || rgbTexture.height != height)
        {
            if (rgbTexture != null)
                Destroy(rgbTexture);

            rgbTexture = new Texture2D(width, height, TextureFormat.RGB24, false);
            previewImage.texture = rgbTexture;
        }
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

        // Texture 업데이트
        rgbTexture.LoadRawTextureData(rgbData);
        rgbTexture.Apply();
    }

    void OnDestroy()
    {
        if (rgbTexture != null)
        {
            Destroy(rgbTexture);
        }
    }
}