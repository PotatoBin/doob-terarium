using System;
using System.Runtime.Serialization;

/// <summary>
/// Background thread에서 Main thread로 전달되는 데이터
/// RGB 이미지 포함 버전
/// </summary>
[Serializable]
public class BackgroundData : ISerializable
{
    // Timestamp
    public float TimestampInMs { get; set; }

    // Depth image
    public byte[] DepthImage { get; set; }
    public int DepthImageWidth { get; set; }
    public int DepthImageHeight { get; set; }
    public int DepthImageSize { get; set; }

    // RGB image
    public byte[] ColorImage { get; set; }
    public int ColorImageWidth { get; set; }
    public int ColorImageHeight { get; set; }
    public int ColorImageSize { get; set; }

    // Bodies
    public ulong NumOfBodies { get; set; }
    public Body[] Bodies { get; set; }

    /// <summary>
    /// 기본 생성자 - Azure Kinect용 최적 크기
    /// </summary>
    public BackgroundData()
    {
        // Depth: 640x576 NFOV Unbinned 기준
        int maxDepthImageSize = 640 * 576 * 3; // RGB 형식

        // Color: 1280x720 기준
        int maxColorImageSize = 1280 * 720 * 4; // BGRA 형식

        // Bodies: 최대 6명
        int maxBodiesCount = 6;

        // Joints: Azure Kinect는 32개 Joint
        int maxJointsSize = 32;

        DepthImage = new byte[maxDepthImageSize];
        ColorImage = new byte[maxColorImageSize];

        Bodies = new Body[maxBodiesCount];
        for (int i = 0; i < maxBodiesCount; i++)
        {
            Bodies[i] = new Body(maxJointsSize);
        }

        UnityEngine.Debug.Log($"[BackgroundData] Initialized: {maxBodiesCount} bodies, {maxJointsSize} joints each");
    }

    /// <summary>
    /// 커스텀 크기 생성자
    /// </summary>
    public BackgroundData(int maxDepthImageSize, int maxColorImageSize, int maxBodiesCount, int maxJointsSize)
    {
        DepthImage = new byte[maxDepthImageSize];
        ColorImage = new byte[maxColorImageSize];

        Bodies = new Body[maxBodiesCount];
        for (int i = 0; i < maxBodiesCount; i++)
        {
            Bodies[i] = new Body(maxJointsSize);
        }
    }

    // Serialization
    public BackgroundData(SerializationInfo info, StreamingContext context)
    {
        TimestampInMs = (float)info.GetValue("TimestampInMs", typeof(float));
        DepthImageWidth = (int)info.GetValue("DepthImageWidth", typeof(int));
        DepthImageHeight = (int)info.GetValue("DepthImageHeight", typeof(int));
        DepthImageSize = (int)info.GetValue("DepthImageSize", typeof(int));

        ColorImageWidth = (int)info.GetValue("ColorImageWidth", typeof(int));
        ColorImageHeight = (int)info.GetValue("ColorImageHeight", typeof(int));
        ColorImageSize = (int)info.GetValue("ColorImageSize", typeof(int));

        NumOfBodies = (ulong)info.GetValue("NumOfBodies", typeof(ulong));
        Bodies = (Body[])info.GetValue("Bodies", typeof(Body[]));
        DepthImage = (byte[])info.GetValue("DepthImage", typeof(byte[]));
        ColorImage = (byte[])info.GetValue("ColorImage", typeof(byte[]));
    }

    public void GetObjectData(SerializationInfo info, StreamingContext context)
    {
        info.AddValue("TimestampInMs", TimestampInMs, typeof(float));
        info.AddValue("DepthImageWidth", DepthImageWidth, typeof(int));
        info.AddValue("DepthImageHeight", DepthImageHeight, typeof(int));
        info.AddValue("DepthImageSize", DepthImageSize, typeof(int));

        info.AddValue("ColorImageWidth", ColorImageWidth, typeof(int));
        info.AddValue("ColorImageHeight", ColorImageHeight, typeof(int));
        info.AddValue("ColorImageSize", ColorImageSize, typeof(int));

        info.AddValue("NumOfBodies", NumOfBodies, typeof(ulong));

        // Valid bodies만 저장
        Body[] ValidBodies = new Body[NumOfBodies];
        for (int i = 0; i < (int)NumOfBodies; i++)
        {
            ValidBodies[i] = Bodies[i];
        }
        info.AddValue("Bodies", ValidBodies, typeof(Body[]));

        // Valid depth image만 저장
        byte[] ValidDepthImage = new byte[DepthImageSize];
        for (int i = 0; i < DepthImageSize; i++)
        {
            ValidDepthImage[i] = DepthImage[i];
        }
        info.AddValue("DepthImage", ValidDepthImage, typeof(byte[]));

        // Valid color image만 저장
        byte[] ValidColorImage = new byte[ColorImageSize];
        for (int i = 0; i < ColorImageSize; i++)
        {
            ValidColorImage[i] = ColorImage[i];
        }
        info.AddValue("ColorImage", ValidColorImage, typeof(byte[]));
    }
}