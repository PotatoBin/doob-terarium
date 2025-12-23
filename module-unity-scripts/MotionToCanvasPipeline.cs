using System;
using System.Collections;
using System.Linq;
using System.Text;
using UnityEngine;
using UnityEngine.Networking;

public class MotionToCanvasPipeline : MonoBehaviour
{
    [Header("Raw Motion JSON (Captured Frames)")]
    public TextAsset motionJson;

    [Header("OpenAI API Key")]
    [Tooltip("Set via Inspector or env var OPENAI_API_KEY to avoid hardcoding secrets.")]
    public string apiKey = "";
    [SerializeField] private string apiKeyEnvVar = "OPENAI_API_KEY";

    [Header("Canvas API Endpoint")]
    [Tooltip("Set via Inspector or env var CANVAS_ENDPOINT. Defaults to production Canvas API.")]
    public string canvasEndpoint = "";
    [SerializeField] private string canvasEndpointEnvVar = "CANVAS_ENDPOINT";

    [Header("Runtime Mode")]
    [Tooltip("true = JSON 파일 사용, false = 실시간 수집 모드")]
    public bool useJsonFile = false;

    private string resolvedApiKey;
    private string resolvedCanvasEndpoint;

    // ============================
    // JSON 구조 정의
    // ============================

    [Serializable]
    public class JointData { public string name; public float x, y, z; public string confidence; }

    [Serializable]
    public class BodyData { public int bodyId; public JointData[] joints; }

    [Serializable]
    public class FrameData
    {
        public float timestamp;
        public int frameNumber;
        public int numberOfBodies;
        public BodyData[] bodies;
    }

    [Serializable]
    public class MotionSequence
    {
        public string sessionId;
        public int totalFrames;
        public float duration;
        public FrameData[] frames;
    }

    // ============================
    // OpenAI 구조
    // ============================

    [Serializable]
    public class ChatMessage { public string role; public string content; }

    [Serializable]
    public class ChatRequest
    {
        public string model;
        public ChatMessage[] messages;
    }

    [Serializable]
    public class ChatResponse
    {
        public Choice[] choices;
        [Serializable]
        public class Choice
        {
            public Message message;
        }
        [Serializable]
        public class Message
        {
            public string role;
            public string content;
        }
    }

    // ============================
    // Canvas 응답 구조
    // ============================

    [Serializable]
    public class CanvasResponse
    {
        public string sessionId;
        public string personaReply;   // 페르소나 기반 응답
        public string rawAnalysis;    // 우리가 보낸 거 처리한 내용(선택)
    }

    void Awake()
    {
        resolvedApiKey = SecretsUtility.ResolveSetting(
            apiKey,
            apiKeyEnvVar,
            string.Empty,
            "MotionToCanvasPipeline/OpenAI API key");

        resolvedCanvasEndpoint = SecretsUtility.ResolveSetting(
            canvasEndpoint,
            canvasEndpointEnvVar,
            "https://canvas.team-doob.com/api/motion-context",
            "MotionToCanvasPipeline/Canvas endpoint",
            warnWhenMissing: false);

        if (!string.IsNullOrWhiteSpace(resolvedCanvasEndpoint))
        {
            canvasEndpoint = resolvedCanvasEndpoint;
        }
    }

    void Start()
    {
        // JSON 파일 모드일 때만 자동 실행
        if (useJsonFile && motionJson != null)
        {
            MotionSequence seq = JsonUtility.FromJson<MotionSequence>(motionJson.text);
            FrameData[] sampled = SampleFrames(seq, 10);
            string behaviorSummaryPrompt = BuildSummaryPrompt(sampled);
            StartCoroutine(GenerateBehaviorSummary(behaviorSummaryPrompt, seq.sessionId));
        }
        else
        {
            Debug.Log("[MotionToCanvasPipeline] Waiting for realtime data from RealtimeMotionCollector");
        }
    }

    // ============================
    // Frame Sampling (public으로 변경)
    // ============================

    public FrameData[] SampleFrames(MotionSequence seq, int everyN)
    {
        return seq.frames.Where((f, i) => i % everyN == 0).ToArray();
    }

    // ============================
    // Summary Prompt (public으로 변경)
    // ============================

    public string BuildSummaryPrompt(FrameData[] frames)
    {
        StringBuilder sb = new StringBuilder();
        sb.AppendLine("You are a motion analysis system for a gallery exhibition.");
        sb.AppendLine("Analyze the visitor's behavior and summarize it in ONE natural sentence in Korean.");
        sb.AppendLine("");
        sb.AppendLine("Focus on these behaviors:");
        sb.AppendLine("- 걷기 (walking)");
        sb.AppendLine("- 서있기 (standing)");
        sb.AppendLine("- 앉기 (sitting)");
        sb.AppendLine("- 손/팔 움직임 (hand/arm movement)");
        sb.AppendLine("- 몸 숙이기 (bending)");
        sb.AppendLine("- 전시물 관람 (observing)");
        sb.AppendLine("");
        sb.AppendLine("Motion data:");
        sb.AppendLine("");

        foreach (var f in frames)
        {
            sb.AppendLine($"Frame {f.frameNumber} ({f.timestamp:F0}ms):");
            foreach (var body in f.bodies)
            {
                foreach (var j in body.joints)
                    sb.Append($"{j.name}({j.x:F2},{j.y:F2},{j.z:F2}) ");
            }
            sb.AppendLine();
        }

        sb.AppendLine("");
        sb.AppendLine("Respond with ONLY ONE sentence in Korean.");

        return sb.ToString();
    }

    // ============================
    // Step 1: OpenAI → 행동 요약 한 문장 생성 (public으로 변경)
    // ============================

    public IEnumerator GenerateBehaviorSummary(string prompt, string sessionId)
    {
        if (string.IsNullOrWhiteSpace(resolvedApiKey))
        {
            Debug.LogError("[MotionToCanvasPipeline] OpenAI API key is not set. Provide it in the Inspector or via env 'OPENAI_API_KEY'.");
            yield break;
        }

        Debug.Log("[Pipeline] ========================================");
        Debug.Log("[Pipeline] 🤖 CALLING OPENAI API");
        Debug.Log("[Pipeline] ========================================");

        ChatRequest req = new ChatRequest
        {
            model = "gpt-4o-mini",
            messages = new ChatMessage[] {
                new ChatMessage { role = "user", content = prompt }
            }
        };

        string json = JsonUtility.ToJson(req);
        Debug.Log($"[Pipeline] Request model: {req.model}");
        Debug.Log($"[Pipeline] Request size: {json.Length} bytes");

        using var www = new UnityWebRequest("https://api.openai.com/v1/chat/completions", "POST");

        www.uploadHandler = new UploadHandlerRaw(Encoding.UTF8.GetBytes(json));
        www.downloadHandler = new DownloadHandlerBuffer();

        www.SetRequestHeader("Content-Type", "application/json");
        www.SetRequestHeader("Authorization", "Bearer " + resolvedApiKey);

        Debug.Log("[Pipeline] Sending request to OpenAI...");
        yield return www.SendWebRequest();

        if (www.result != UnityWebRequest.Result.Success)
        {
            Debug.LogError($"[Pipeline] ❌ OpenAI Error: {www.error}");
            Debug.LogError($"[Pipeline] Response Code: {www.responseCode}");
            Debug.LogError($"[Pipeline] Response: {www.downloadHandler.text}");
            yield break;
        }

        Debug.Log($"[Pipeline] ✓ OpenAI Response received ({www.downloadHandler.text.Length} bytes)");
        Debug.Log($"[Pipeline] Raw response: {www.downloadHandler.text}");

        ChatResponse res = JsonUtility.FromJson<ChatResponse>(www.downloadHandler.text);
        string summary = res.choices[0].message.content.Trim();

        Debug.Log("[Pipeline] ========================================");
        Debug.Log($"[Pipeline] 📝 BEHAVIOR SUMMARY: {summary}");
        Debug.Log("[Pipeline] ========================================");

        // 다음 스텝 → Canvas 서버로 전달
        StartCoroutine(SendToCanvas(sessionId, summary));
    }

    // ============================
    // Step 2: canvas.team-doob.com으로 전송
    // ============================

    [Serializable]
    public class CanvasPayload
    {
        public string sessionId;
        public string motionSummary;
    }

    IEnumerator SendToCanvas(string sessionId, string summary)
    {
        if (string.IsNullOrWhiteSpace(resolvedCanvasEndpoint))
        {
            Debug.LogError("[MotionToCanvasPipeline] Canvas endpoint is not set. Provide it in the Inspector or via env 'CANVAS_ENDPOINT'.");
            yield break;
        }

        Debug.Log("[Pipeline] ========================================");
        Debug.Log("[Pipeline] 🌐 SENDING TO CANVAS API");
        Debug.Log("[Pipeline] ========================================");

        CanvasPayload payload = new CanvasPayload
        {
            sessionId = sessionId,
            motionSummary = summary
        };

        string json = JsonUtility.ToJson(payload);
        Debug.Log($"[Pipeline] Canvas endpoint: {resolvedCanvasEndpoint}");
        Debug.Log($"[Pipeline] Payload: {json}");

        using var www = new UnityWebRequest(resolvedCanvasEndpoint, "POST");
        www.uploadHandler = new UploadHandlerRaw(Encoding.UTF8.GetBytes(json));
        www.downloadHandler = new DownloadHandlerBuffer();

        www.SetRequestHeader("Content-Type", "application/json");

        Debug.Log("[Pipeline] Sending request to Canvas...");
        yield return www.SendWebRequest();

        if (www.result != UnityWebRequest.Result.Success)
        {
            Debug.LogError($"[Pipeline] ❌ Canvas API Error: {www.error}");
            Debug.LogError($"[Pipeline] Response Code: {www.responseCode}");
            Debug.LogError($"[Pipeline] Response: {www.downloadHandler.text}");
            yield break;
        }

        string resStr = www.downloadHandler.text;
        Debug.Log($"[Pipeline] ✓ Canvas Response received ({resStr.Length} bytes)");
        Debug.Log($"[Pipeline] Canvas Response Raw: {resStr}");

        CanvasResponse canvasRes = JsonUtility.FromJson<CanvasResponse>(resStr);

        Debug.Log("[Pipeline] ========================================");
        Debug.Log($"[Pipeline] 🎭 PERSONA REPLY: {canvasRes.personaReply}");
        Debug.Log("[Pipeline] ========================================");
    }
}
