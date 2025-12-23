using System;
using System.IO;
using UnityEngine;

public class FaceIdManager : MonoBehaviour
{
    [Header("Face ID File Path (Optional)")]
    public string faceIdFilePath;

    public string CurrentFaceId { get; private set; } = "";

    private DateTime lastWriteTime = DateTime.MinValue;
    public static FaceIdManager Instance;

    void Awake()
    {
        if (Instance != null)
        {
            Destroy(gameObject);
            return;
        }

        Instance = this;
        DontDestroyOnLoad(gameObject);

        // �⺻ ��� ����
        if (string.IsNullOrEmpty(faceIdFilePath))
        {
            string projectRoot = Directory.GetParent(Application.dataPath).FullName;
            faceIdFilePath = Path.Combine(projectRoot, "Assets/incoming/latest_face.txt");
        }

        Debug.Log($"[FaceIdManager] Watching file: {faceIdFilePath}");
    }

    void Update()
    {
        // ���� ��� visitor_id ���� (legacy ����)
        if (!File.Exists(faceIdFilePath))
            return;

        DateTime writeTime = File.GetLastWriteTime(faceIdFilePath);
        if (writeTime <= lastWriteTime)
            return;

        lastWriteTime = writeTime;

        try
        {
            string newId = File.ReadAllText(faceIdFilePath).Trim();

            if (!string.IsNullOrEmpty(newId) && newId != CurrentFaceId)
            {
                CurrentFaceId = newId;
                Debug.Log($"[FaceIdManager] File updated: visitor_id = {CurrentFaceId}");
            }
        }
        catch (Exception e)
        {
            Debug.LogWarning($"[FaceIdManager] Failed to read latest_face.txt: {e.Message}");
        }
    }

    /// <summary>
    /// HTTP �������� ���� visitor_id�� ���� ����
    /// </summary>
    public void SetFaceIdDirectly(string visitorId)
    {
        if (!string.IsNullOrEmpty(visitorId) && visitorId != CurrentFaceId)
        {
            CurrentFaceId = visitorId;
            Debug.Log($"[FaceIdManager] Direct update: visitor_id = {CurrentFaceId}");
        }
    }

    /// <summary>
    /// ���� visitor_id �ʱ�ȭ (����� ������� �� ��)
    /// </summary>
    public void ClearFaceId()
    {
        CurrentFaceId = "";
        Debug.Log("[FaceIdManager] Cleared visitor_id");
    }
}
