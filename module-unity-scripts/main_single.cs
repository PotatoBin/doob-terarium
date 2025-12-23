using UnityEngine;

/// <summary>
/// Single Person Main Controller
/// - 가장 처음 감지된 1명을 고정하여 추적
/// - enableFaceRecognition = false 인 경우 Lock Tracking 활성화
/// </summary>
public class main_single : MonoBehaviour
{
    [Header("Tracker")]
    public GameObject trackerPrefab; // TrackerHandler_single (선택사항)

    [Header("Face Recognition (Optional)")]
    public bool enableFaceRecognition = false; // 얼굴 인식 활성화 여부

    // 내부 변수
    private SkeletalTrackingProvider skeletalProvider;
    public BackgroundData lastFrameData = new BackgroundData(); // EntryDetector_single 접근용
    public TrackerHandler_single trackerHandler;
    private PuppetAvatar_single puppet;
    private EntryDetector_single entryDetector;

    private bool visitorVerified = false; // 얼굴 인식 완료 여부
    private bool bodyDetected = false; // Body 감지 여부

    // 🔥 Normal Mode에서 "처음 발견된 1명"을 고정하기 위한 변수
    private ulong lockedTrackingID = 0;


    void Start()
    {
        Debug.Log("=== [main_single] Initializing Single Person Tracking ===");

        // 1. Kinect Skeletal Tracking Provider 시작
        skeletalProvider = new SkeletalTrackingProvider(0);
        Debug.Log("[main_single] SkeletalTrackingProvider started");

        // 2. TrackerHandler 준비
        InitializeTrackerHandler();

        // 3. Puppet 준비
        InitializePuppet();

        // 4. EntryDetector 준비 (얼굴 인식 사용 시)
        if (enableFaceRecognition)
        {
            InitializeEntryDetector();
        }

        Debug.Log($"[main_single] Initialization complete (Face Recognition: {enableFaceRecognition})");
    }

    /// <summary>
    /// TrackerHandler 초기화
    /// </summary>
    private void InitializeTrackerHandler()
    {
        if (trackerPrefab != null)
        {
            GameObject trackerObj = Instantiate(trackerPrefab);
            trackerObj.name = "TrackerHandler_single";
            trackerHandler = trackerObj.GetComponent<TrackerHandler_single>();

            if (trackerHandler == null)
                trackerHandler = trackerObj.AddComponent<TrackerHandler_single>();
        }
        else
        {
            trackerHandler = FindObjectOfType<TrackerHandler_single>();

            if (trackerHandler == null)
            {
                GameObject trackerObj = new GameObject("TrackerHandler_single");
                trackerHandler = trackerObj.AddComponent<TrackerHandler_single>();
            }
        }

        Debug.Log("[main_single] TrackerHandler initialized");
    }

    /// <summary>
    /// Puppet 초기화 (씬에서 자동 검색)
    /// </summary>
    private void InitializePuppet()
    {
        puppet = FindObjectOfType<PuppetAvatar_single>();

        if (puppet == null)
        {
            Debug.LogError("[main_single] No PuppetAvatar_single found in scene! Please add to character.");
            return;
        }

        puppet.KinectDevice = trackerHandler;

        // 얼굴 인식 사용 시 초기 비활성화
        if (enableFaceRecognition)
        {
            puppet.gameObject.SetActive(false);
            Debug.Log("[main_single] Puppet initialized (waiting for face verification)");
        }
        else
        {
            puppet.gameObject.SetActive(true);
            Debug.Log("[main_single] Puppet initialized (active)");
        }
    }

    /// <summary>
    /// EntryDetector 초기화 (얼굴 인식 활성화 시)
    /// </summary>
    private void InitializeEntryDetector()
    {
        entryDetector = FindObjectOfType<EntryDetector_single>();

        if (entryDetector == null)
        {
            GameObject detectorObj = new GameObject("EntryDetector_single");
            entryDetector = detectorObj.AddComponent<EntryDetector_single>();
        }

        entryDetector.mainController = this;
        entryDetector.onVerificationSuccess += OnVisitorVerified;
        entryDetector.onVerificationFailed += OnVisitorRejected;

        Debug.Log("[main_single] EntryDetector initialized");
    }

    void Update()
    {
        // Skeletal Provider 준비 대기
        if (skeletalProvider == null || !skeletalProvider.IsRunning)
            return;

        // 최신 프레임 데이터 가져오기
        if (!skeletalProvider.GetCurrentFrameData(ref lastFrameData))
            return;

        // TrackerHandler 업데이트
        trackerHandler.updateTracker(lastFrameData);

        // Body 감지 여부 확인
        bool currentBodyDetected = lastFrameData.NumOfBodies > 0;

        // 얼굴 인식 모드
        if (enableFaceRecognition)
        {
            HandleFaceRecognitionMode(currentBodyDetected);
        }
        // 🔥 일반 모드 (Lock Tracking 활성화)
        else
        {
            HandleNormalModeWithLock(currentBodyDetected);
        }

        bodyDetected = currentBodyDetected;
    }

    // ============================================================
    //  🔥 "Lock Tracking" Normal Mode
    //     - 처음 감지된 사람의 TrackingID를 저장하고 끝까지 유지
    // ============================================================
    private void HandleNormalModeWithLock(bool currentBodyDetected)
    {
        if (puppet == null)
            return;

        ulong currentID = trackerHandler.currentBodyTrackingID;

        // 아무도 없는 경우 → reset
        if (!currentBodyDetected)
        {
            if (lockedTrackingID != 0)
                Debug.Log("[main_single] Locked person lost → Reset");

            lockedTrackingID = 0;
            puppet.gameObject.SetActive(false);
            return;
        }

        // 처음 Body 등장 → lock
        if (lockedTrackingID == 0)
        {
            lockedTrackingID = currentID;
            Debug.Log("[main_single] First person detected → Locking trackingID = " + lockedTrackingID);

            puppet.gameObject.SetActive(true);
            return;
        }

        // 현재 추적 중인 사람과 다르면 무시
        if (currentID != lockedTrackingID)
        {
            Debug.Log("[main_single] New person ignored. Locked = " + lockedTrackingID);
            return;
        }

        // 정상 추적 중
        if (!puppet.gameObject.activeSelf)
        {
            puppet.gameObject.SetActive(true);
            Debug.Log("[main_single] Tracking locked person");
        }
    }

    // ============================================================


    /// <summary>
    /// 얼굴 인식 모드 처리
    /// </summary>
    private void HandleFaceRecognitionMode(bool currentBodyDetected)
    {
        if (currentBodyDetected && !bodyDetected && !visitorVerified)
        {
            Debug.Log("[main_single] New body detected, requesting face verification...");

            if (entryDetector != null)
            {
                entryDetector.OnBodyDetected(trackerHandler.currentBodyTrackingID);
            }
        }

        if (visitorVerified && currentBodyDetected)
        {
            if (puppet != null && !puppet.gameObject.activeSelf)
            {
                puppet.gameObject.SetActive(true);
            }
        }

        if (!currentBodyDetected && bodyDetected)
        {
            Debug.Log("[main_single] Body lost, resetting...");
            visitorVerified = false;

            if (puppet != null)
            {
                puppet.gameObject.SetActive(false);
                puppet.SetVisitorId("");
            }
        }
    }

    /// <summary>
    /// 얼굴 인식 성공 콜백
    /// </summary>
    private void OnVisitorVerified(string visitorId)
    {
        visitorVerified = true;

        if (puppet != null)
        {
            puppet.SetVisitorId(visitorId);
            puppet.gameObject.SetActive(true);
        }

        Debug.Log($"[main_single] Visitor verified: {visitorId}");
    }

    /// <summary>
    /// 얼굴 인식 실패 콜백
    /// </summary>
    private void OnVisitorRejected()
    {
        Debug.LogWarning("[main_single] Visitor verification failed (unregistered)");

        if (puppet != null)
        {
            puppet.gameObject.SetActive(false);
        }
    }

    void OnApplicationQuit()
    {
        if (skeletalProvider != null)
        {
            skeletalProvider.Dispose();
            Debug.Log("[main_single] SkeletalTrackingProvider disposed");
        }
    }

    void OnGUI()
    {
        if (!Debug.isDebugBuild)
            return;

        GUILayout.BeginArea(new Rect(10, 10, 300, 150));

        GUILayout.Label($"Mode: {(enableFaceRecognition ? "Face Recognition" : "Normal (Locked)")}");
        GUILayout.Label($"Body Detected: {bodyDetected}");

        if (!enableFaceRecognition)
        {
            GUILayout.Label($"Locked ID: {lockedTrackingID}");
        }

        if (trackerHandler != null)
        {
            GUILayout.Label($"Current ID: {trackerHandler.currentBodyTrackingID}");
        }

        GUILayout.EndArea();
    }
}
