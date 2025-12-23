using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using Microsoft.Azure.Kinect.BodyTracking;

/// <summary>
/// 키넥트 데이터를 실시간으로 수집하고 MotionToCanvasPipeline에 전달
/// </summary>
public class RealtimeMotionCollector : MonoBehaviour
{
    [Header("References")]
    [Tooltip("main_single 스크립트")]
    public main_single mainSingleScript;

    [Tooltip("main 스크립트 (legacy)")]
    public main mainScript;

    [Tooltip("MotionToCanvasPipeline 스크립트")]
    public MotionToCanvasPipeline pipeline;

    [Header("Collection Settings")]
    [Tooltip("분석 주기 (초)")]
    public float analysisInterval = 5f;

    [Tooltip("한 번에 분석할 프레임 수")]
    public int framesToCollect = 50;

    [Tooltip("프레임 수집 간격 (1=모든 프레임, 2=2프레임마다)")]
    public int collectEveryNFrames = 2;

    [Header("Status")]
    public bool isCollecting = true;
    public int currentFrameCount = 0;
    public float nextAnalysisIn = 0f;
    public string lastSessionId = "";

    // 내부 데이터
    private List<MotionToCanvasPipeline.FrameData> collectedFrames = new List<MotionToCanvasPipeline.FrameData>();
    private float timeSinceLastAnalysis = 0f;
    private int frameCounter = 0;
    private string sessionId;

    void Start()
    {
        sessionId = DateTime.Now.ToString("yyyyMMdd_HHmmss");
        lastSessionId = sessionId;

        // 자동 검색 - main_single 우선
        if (mainSingleScript == null)
        {
            mainSingleScript = FindObjectOfType<main_single>();
        }

        // main_single이 없으면 main 검색
        if (mainSingleScript == null && mainScript == null)
        {
            mainScript = FindObjectOfType<main>();
        }

        if (mainSingleScript == null && mainScript == null)
        {
            Debug.LogError("[RealtimeCollector] Neither main_single nor main script found!");
            enabled = false;
            return;
        }

        // 어떤 스크립트를 사용하는지 로그
        if (mainSingleScript != null)
        {
            Debug.Log("[RealtimeCollector] Using main_single script");
        }
        else
        {
            Debug.Log("[RealtimeCollector] Using main script");
        }

        if (pipeline == null)
        {
            pipeline = GetComponent<MotionToCanvasPipeline>();
            if (pipeline == null)
            {
                pipeline = FindObjectOfType<MotionToCanvasPipeline>();
            }
        }

        if (pipeline == null)
        {
            Debug.LogError("[RealtimeCollector] MotionToCanvasPipeline not found!");
            enabled = false;
            return;
        }

        Debug.Log($"[RealtimeCollector] Started. Session: {sessionId}");
        Debug.Log($"Will analyze every {analysisInterval}s with {framesToCollect} frames");
    }

    void Update()
    {
        if (!isCollecting)
        {
            if (frameCounter % 300 == 0) // 10초마다 한 번만 출력
                Debug.LogWarning("[RealtimeCollector] Collection is paused");
            frameCounter++;
            return;
        }

        if (mainSingleScript == null && mainScript == null)
        {
            Debug.LogError("[RealtimeCollector] No main script reference!");
            return;
        }

        // 프레임 수집
        if (frameCounter % collectEveryNFrames == 0)
        {
            CollectFrame();
        }
        frameCounter++;

        // 분석 타이밍
        timeSinceLastAnalysis += Time.deltaTime;
        nextAnalysisIn = Mathf.Max(0, analysisInterval - timeSinceLastAnalysis);

        if (timeSinceLastAnalysis >= analysisInterval && collectedFrames.Count >= 10)
        {
            TriggerAnalysis();
            timeSinceLastAnalysis = 0f;
        }
    }

    /// <summary>
    /// 현재 프레임 수집
    /// </summary>
    void CollectFrame()
    {
        // main_single 우선, 없으면 main 사용
        BackgroundData data = mainSingleScript != null ?
                              mainSingleScript.lastFrameData :
                              mainScript.m_lastFrameData;

        if (data == null)
        {
            Debug.LogWarning($"[RealtimeCollector] Frame {frameCounter}: BackgroundData is null");
            return;
        }

        if (data.NumOfBodies == 0)
        {
            if (frameCounter % 100 == 0) // 가끔만 출력
                Debug.Log($"[RealtimeCollector] Frame {frameCounter}: No bodies detected");
            return;
        }

        Debug.Log($"[RealtimeCollector] Frame {frameCounter}: Collecting data with {data.NumOfBodies} body(ies)");

        // MotionToCanvasPipeline의 FrameData 구조로 변환
        MotionToCanvasPipeline.FrameData frame = ConvertToFrameData(data);

        collectedFrames.Add(frame);
        currentFrameCount = collectedFrames.Count;

        Debug.Log($"[RealtimeCollector] ✓ Frame added. Total frames in buffer: {currentFrameCount}");

        // 버퍼 크기 제한
        if (collectedFrames.Count > framesToCollect * 2)
        {
            collectedFrames.RemoveAt(0);
            Debug.Log($"[RealtimeCollector] Buffer limit reached. Removed oldest frame.");
        }
    }

    /// <summary>
    /// BackgroundData를 MotionToCanvasPipeline.FrameData로 변환
    /// </summary>
    MotionToCanvasPipeline.FrameData ConvertToFrameData(BackgroundData data)
    {
        Debug.Log($"[RealtimeCollector] Converting frame: timestamp={data.TimestampInMs}ms, bodies={data.NumOfBodies}");

        var frame = new MotionToCanvasPipeline.FrameData
        {
            timestamp = data.TimestampInMs,
            frameNumber = frameCounter,
            numberOfBodies = (int)data.NumOfBodies,
            bodies = new MotionToCanvasPipeline.BodyData[data.NumOfBodies]
        };

        for (ulong i = 0; i < data.NumOfBodies; i++)
        {
            frame.bodies[i] = ConvertToBodyData(data.Bodies[i]);
            Debug.Log($"[RealtimeCollector]   Body {i}: ID={data.Bodies[i].Id}, Joints={data.Bodies[i].Length}");
        }

        Debug.Log($"[RealtimeCollector] ✓ Frame converted successfully");
        return frame;
    }

    /// <summary>
    /// Body를 MotionToCanvasPipeline.BodyData로 변환
    /// </summary>
    MotionToCanvasPipeline.BodyData ConvertToBodyData(Body body)
    {
        // 주요 관절만 추출 (토큰 절약)
        JointId[] keyJointIds = new JointId[]
        {
            JointId.Head, JointId.Neck, JointId.SpineChest, JointId.Pelvis,
            JointId.ShoulderLeft, JointId.ShoulderRight,
            JointId.ElbowLeft, JointId.ElbowRight,
            JointId.WristLeft, JointId.WristRight,
            JointId.HipLeft, JointId.HipRight,
            JointId.KneeLeft, JointId.KneeRight
        };

        var bodyData = new MotionToCanvasPipeline.BodyData
        {
            bodyId = (int)body.Id,
            joints = new MotionToCanvasPipeline.JointData[keyJointIds.Length]
        };

        Debug.Log($"[RealtimeCollector]     Converting {keyJointIds.Length} key joints...");

        for (int i = 0; i < keyJointIds.Length; i++)
        {
            int jointIdx = (int)keyJointIds[i];
            bodyData.joints[i] = new MotionToCanvasPipeline.JointData
            {
                name = keyJointIds[i].ToString(),
                x = body.JointPositions3D[jointIdx].X,
                y = body.JointPositions3D[jointIdx].Y,
                z = body.JointPositions3D[jointIdx].Z,
                confidence = body.JointPrecisions[jointIdx].ToString()
            };

            // 샘플로 첫 번째 관절 데이터 출력
            if (i == 0)
            {
                Debug.Log($"[RealtimeCollector]       Sample Joint [{bodyData.joints[i].name}]: " +
                         $"pos=({bodyData.joints[i].x:F2}, {bodyData.joints[i].y:F2}, {bodyData.joints[i].z:F2}), " +
                         $"confidence={bodyData.joints[i].confidence}");
            }
        }

        return bodyData;
    }

    /// <summary>
    /// MotionToCanvasPipeline에 분석 요청
    /// </summary>
    void TriggerAnalysis()
    {
        Debug.Log("========================================");
        Debug.Log("[RealtimeCollector] 🚀 TRIGGERING ANALYSIS");
        Debug.Log("========================================");

        if (collectedFrames.Count == 0)
        {
            Debug.LogWarning("[RealtimeCollector] ❌ No frames to analyze");
            return;
        }

        Debug.Log($"[RealtimeCollector] Total frames collected: {collectedFrames.Count}");

        // 최근 N개 프레임만 추출
        var framesToAnalyze = collectedFrames
            .Skip(Math.Max(0, collectedFrames.Count - framesToCollect))
            .ToArray();

        Debug.Log($"[RealtimeCollector] Frames to analyze: {framesToAnalyze.Length}");
        Debug.Log($"[RealtimeCollector] Time range: {framesToAnalyze[0].timestamp:F0}ms ~ {framesToAnalyze[framesToAnalyze.Length - 1].timestamp:F0}ms");

        // MotionSequence 구조 생성
        var sequence = new MotionToCanvasPipeline.MotionSequence
        {
            sessionId = sessionId,
            totalFrames = framesToAnalyze.Length,
            duration = framesToAnalyze.Length > 0 ?
                       framesToAnalyze[framesToAnalyze.Length - 1].timestamp - framesToAnalyze[0].timestamp : 0,
            frames = framesToAnalyze
        };

        Debug.Log($"[RealtimeCollector] MotionSequence created:");
        Debug.Log($"  - Session ID: {sequence.sessionId}");
        Debug.Log($"  - Total Frames: {sequence.totalFrames}");
        Debug.Log($"  - Duration: {sequence.duration:F0}ms");

        // Pipeline의 메서드 직접 호출
        Debug.Log("[RealtimeCollector] Starting ProcessMotionSequence coroutine...");
        StartCoroutine(ProcessMotionSequence(sequence));
    }

    /// <summary>
    /// MotionToCanvasPipeline의 로직 실행
    /// </summary>
    System.Collections.IEnumerator ProcessMotionSequence(MotionToCanvasPipeline.MotionSequence seq)
    {
        Debug.Log("[RealtimeCollector] ========================================");
        Debug.Log("[RealtimeCollector] 📊 PROCESSING MOTION SEQUENCE");
        Debug.Log("[RealtimeCollector] ========================================");

        // 프레임 샘플링 (10프레임마다)
        Debug.Log("[RealtimeCollector] Step 1: Sampling frames (every 10th frame)...");
        var sampled = pipeline.SampleFrames(seq, 10);
        Debug.Log($"[RealtimeCollector] ✓ Sampled {sampled.Length} frames from {seq.totalFrames} total frames");

        // 프롬프트 생성
        Debug.Log("[RealtimeCollector] Step 2: Building analysis prompt...");
        string prompt = pipeline.BuildSummaryPrompt(sampled);
        Debug.Log($"[RealtimeCollector] ✓ Prompt generated ({prompt.Length} characters)");
        Debug.Log("[RealtimeCollector] Prompt preview (first 200 chars):");
        Debug.Log(prompt.Substring(0, Mathf.Min(200, prompt.Length)) + "...");

        // OpenAI 분석 및 Canvas 전송
        Debug.Log("[RealtimeCollector] Step 3: Calling OpenAI and sending to Canvas...");
        yield return StartCoroutine(pipeline.GenerateBehaviorSummary(prompt, seq.sessionId));

        Debug.Log("[RealtimeCollector] ========================================");
        Debug.Log("[RealtimeCollector] ✅ ANALYSIS COMPLETE");
        Debug.Log("[RealtimeCollector] ========================================");
    }

    // 공개 메서드
    public void PauseCollection() => isCollecting = false;
    public void ResumeCollection() => isCollecting = true;

    public void ClearBuffer()
    {
        collectedFrames.Clear();
        currentFrameCount = 0;
        Debug.Log("[RealtimeCollector] Buffer cleared");
    }

    public void StartNewSession()
    {
        sessionId = DateTime.Now.ToString("yyyyMMdd_HHmmss");
        lastSessionId = sessionId;
        ClearBuffer();
        Debug.Log($"[RealtimeCollector] New session: {sessionId}");
    }
}