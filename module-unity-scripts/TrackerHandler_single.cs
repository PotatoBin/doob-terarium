using System.Collections.Generic;
using UnityEngine;
using Microsoft.Azure.Kinect.BodyTracking;

/// <summary>
/// Single Person Tracker Handler
/// - 가장 처음/가장 가까운 1명을 "힙 위치 기반"으로 Lock 추적
/// - Lock된 사람과 가장 힙 위치가 비슷한 Body를 계속 선택
/// </summary>
public class TrackerHandler_single : MonoBehaviour
{
    [Header("Tracking Data")]
    public Quaternion[] absoluteJointRotations = new Quaternion[(int)JointId.Count];
    public Vector3[] jointPositions = new Vector3[(int)JointId.Count];
    public ulong currentBodyTrackingID = 0; // 현재 추적 중인 body ID (Lock된 사람)

    [Header("Visualization")]
    public bool drawSkeletons = true;

    // 내부 변수
    public Dictionary<JointId, JointId> parentJointMap;
    private Dictionary<JointId, Quaternion> basisJointMap;
    private static readonly Quaternion Y_180_FLIP = new Quaternion(0.0f, 1.0f, 0.0f, 0.0f);

    // 🔥 Lock 관련 상태
    private bool hasLockedPerson = false;
    private int lockedBodyIndex = -1;
    private Vector3 lockedHipPosition = Vector3.zero;

    void Awake()
    {
        InitializeJointMaps();
    }

    /// <summary>
    /// Joint 부모 관계 및 basis 초기화
    /// </summary>
    private void InitializeJointMaps()
    {
        parentJointMap = new Dictionary<JointId, JointId>();
        basisJointMap = new Dictionary<JointId, Quaternion>();

        // 축 단축키
        Vector3 zpositive = Vector3.forward;
        Vector3 xpositive = Vector3.right;
        Vector3 ypositive = Vector3.up;

        // Basis 회전값
        Quaternion leftHipBasis = Quaternion.LookRotation(xpositive, -zpositive);
        Quaternion spineHipBasis = Quaternion.LookRotation(xpositive, -zpositive);
        Quaternion rightHipBasis = Quaternion.LookRotation(xpositive, zpositive);
        Quaternion leftArmBasis = Quaternion.LookRotation(ypositive, -zpositive);
        Quaternion rightArmBasis = Quaternion.LookRotation(-ypositive, zpositive);
        Quaternion leftHandBasis = Quaternion.LookRotation(-zpositive, -ypositive);
        Quaternion rightHandBasis = Quaternion.identity;
        Quaternion leftFootBasis = Quaternion.LookRotation(xpositive, ypositive);
        Quaternion rightFootBasis = Quaternion.LookRotation(xpositive, -ypositive);

        // 부모 관계 설정
        parentJointMap[JointId.Pelvis] = JointId.Count;
        parentJointMap[JointId.SpineNavel] = JointId.Pelvis;
        parentJointMap[JointId.SpineChest] = JointId.SpineNavel;
        parentJointMap[JointId.Neck] = JointId.SpineChest;
        parentJointMap[JointId.Head] = JointId.Neck;

        parentJointMap[JointId.ClavicleLeft] = JointId.SpineChest;
        parentJointMap[JointId.ShoulderLeft] = JointId.ClavicleLeft;
        parentJointMap[JointId.ElbowLeft] = JointId.ShoulderLeft;
        parentJointMap[JointId.WristLeft] = JointId.ElbowLeft;
        parentJointMap[JointId.HandLeft] = JointId.WristLeft;
        parentJointMap[JointId.HandTipLeft] = JointId.HandLeft;
        parentJointMap[JointId.ThumbLeft] = JointId.HandLeft;

        parentJointMap[JointId.ClavicleRight] = JointId.SpineChest;
        parentJointMap[JointId.ShoulderRight] = JointId.ClavicleRight;
        parentJointMap[JointId.ElbowRight] = JointId.ShoulderRight;
        parentJointMap[JointId.WristRight] = JointId.ElbowRight;
        parentJointMap[JointId.HandRight] = JointId.WristRight;
        parentJointMap[JointId.HandTipRight] = JointId.HandRight;
        parentJointMap[JointId.ThumbRight] = JointId.HandRight;

        parentJointMap[JointId.HipLeft] = JointId.SpineNavel;
        parentJointMap[JointId.KneeLeft] = JointId.HipLeft;
        parentJointMap[JointId.AnkleLeft] = JointId.KneeLeft;
        parentJointMap[JointId.FootLeft] = JointId.AnkleLeft;

        parentJointMap[JointId.HipRight] = JointId.SpineNavel;
        parentJointMap[JointId.KneeRight] = JointId.HipRight;
        parentJointMap[JointId.AnkleRight] = JointId.KneeRight;
        parentJointMap[JointId.FootRight] = JointId.AnkleRight;

        parentJointMap[JointId.Nose] = JointId.Head;
        parentJointMap[JointId.EyeLeft] = JointId.Head;
        parentJointMap[JointId.EarLeft] = JointId.Head;
        parentJointMap[JointId.EyeRight] = JointId.Head;
        parentJointMap[JointId.EarRight] = JointId.Head;

        // Basis 매핑
        basisJointMap[JointId.Pelvis] = spineHipBasis;
        basisJointMap[JointId.SpineNavel] = spineHipBasis;
        basisJointMap[JointId.SpineChest] = spineHipBasis;
        basisJointMap[JointId.Neck] = spineHipBasis;
        basisJointMap[JointId.Head] = spineHipBasis;

        basisJointMap[JointId.ClavicleLeft] = leftArmBasis;
        basisJointMap[JointId.ShoulderLeft] = leftArmBasis;
        basisJointMap[JointId.ElbowLeft] = leftArmBasis;
        basisJointMap[JointId.WristLeft] = leftHandBasis;
        basisJointMap[JointId.HandLeft] = leftHandBasis;
        basisJointMap[JointId.HandTipLeft] = leftHandBasis;
        basisJointMap[JointId.ThumbLeft] = leftArmBasis;

        basisJointMap[JointId.ClavicleRight] = rightArmBasis;
        basisJointMap[JointId.ShoulderRight] = rightArmBasis;
        basisJointMap[JointId.ElbowRight] = rightArmBasis;
        basisJointMap[JointId.WristRight] = rightHandBasis;
        basisJointMap[JointId.HandRight] = rightHandBasis;
        basisJointMap[JointId.HandTipRight] = rightHandBasis;
        basisJointMap[JointId.ThumbRight] = rightArmBasis;

        basisJointMap[JointId.HipLeft] = leftHipBasis;
        basisJointMap[JointId.KneeLeft] = leftHipBasis;
        basisJointMap[JointId.AnkleLeft] = leftHipBasis;
        basisJointMap[JointId.FootLeft] = leftFootBasis;

        basisJointMap[JointId.HipRight] = rightHipBasis;
        basisJointMap[JointId.KneeRight] = rightHipBasis;
        basisJointMap[JointId.AnkleRight] = rightHipBasis;
        basisJointMap[JointId.FootRight] = rightFootBasis;

        basisJointMap[JointId.Nose] = spineHipBasis;
        basisJointMap[JointId.EyeLeft] = spineHipBasis;
        basisJointMap[JointId.EarLeft] = spineHipBasis;
        basisJointMap[JointId.EyeRight] = spineHipBasis;
        basisJointMap[JointId.EarRight] = spineHipBasis;
    }

    /// <summary>
    /// BackgroundData에서 Lock된 body를 기준으로 추적 데이터 업데이트
    /// </summary>
    public void updateTracker(BackgroundData trackerFrameData)
    {
        // 아무도 없으면 Lock 해제
        if (trackerFrameData.NumOfBodies == 0)
        {
            currentBodyTrackingID = 0;
            hasLockedPerson = false;
            lockedBodyIndex = -1;
            return;
        }

        // 1) 아직 Lock된 사람이 없으면 → 가장 가까운 사람 Lock
        if (!hasLockedPerson)
        {
            lockedBodyIndex = FindClosestBodyIndex(trackerFrameData);
            lockedHipPosition = GetHipPosition(trackerFrameData, lockedBodyIndex);
            hasLockedPerson = true;

            Debug.Log("[TrackerHandler_single] Lock first person: bodyIndex = " + lockedBodyIndex);
        }
        else
        {
            // 2) Lock된 사람이 있을 때 → 가장 힙 위치가 비슷한 body 선택
            int newIndex = FindMostSimilarBodyIndex(trackerFrameData, lockedHipPosition);

            if (newIndex != lockedBodyIndex)
            {
                Debug.Log($"[TrackerHandler_single] Keeping lock, but best match index changed {lockedBodyIndex} → {newIndex}");
            }

            lockedBodyIndex = newIndex;
            lockedHipPosition = GetHipPosition(trackerFrameData, lockedBodyIndex);
        }

        // 선택된 body로부터 skeleton 정보 추출
        Body skeleton = trackerFrameData.Bodies[lockedBodyIndex];
        currentBodyTrackingID = skeleton.Id;

        // Joint 데이터 처리
        for (int j = 0; j < (int)JointId.Count; j++)
        {
            // 회전
            var q = skeleton.JointRotations[j];
            Quaternion k4aQuat = new Quaternion((float)q.X, (float)q.Y, (float)q.Z, (float)q.W);
            Quaternion basis = basisJointMap[(JointId)j];
            Quaternion jointRot = Y_180_FLIP * k4aQuat * Quaternion.Inverse(basis);
            absoluteJointRotations[j] = jointRot;

            // 위치 (Y축 반전)
            var p = skeleton.JointPositions3D[j];
            jointPositions[j] = new Vector3((float)p.X, (float)-p.Y, (float)p.Z);
        }

        // Scene 뷰 시각화 (선택사항)
        if (drawSkeletons)
        {
            RenderSkeleton(skeleton, 0);
        }
    }

    /// <summary>
    /// 현재 프레임에서 "카메라 기준으로 가장 가까운" 사람의 인덱스
    /// </summary>
    private int FindClosestBodyIndex(BackgroundData trackerFrameData)
    {
        int closestBody = 0;
        float minDist = float.MaxValue;

        for (int i = 0; i < (int)trackerFrameData.NumOfBodies; i++)
        {
            Vector3 hip = GetHipPosition(trackerFrameData, i);
            float d = hip.magnitude;

            if (d < minDist)
            {
                minDist = d;
                closestBody = i;
            }
        }
        return closestBody;
    }

    /// <summary>
    /// 이전 Lock된 힙 위치와 가장 가까운 Body 인덱스
    /// </summary>
    private int FindMostSimilarBodyIndex(BackgroundData trackerFrameData, Vector3 prevHip)
    {
        int bestIndex = 0;
        float minDiff = float.MaxValue;

        for (int i = 0; i < (int)trackerFrameData.NumOfBodies; i++)
        {
            Vector3 hip = GetHipPosition(trackerFrameData, i);
            float diff = Vector3.Distance(hip, prevHip);

            if (diff < minDiff)
            {
                minDiff = diff;
                bestIndex = i;
            }
        }
        return bestIndex;
    }

    /// <summary>
    /// Pelvis(Hip) Joint 3D 위치
    /// </summary>
    private Vector3 GetHipPosition(BackgroundData trackerFrameData, int bodyIndex)
    {
        var pelvis = trackerFrameData.Bodies[bodyIndex].JointPositions3D[(int)JointId.Pelvis];
        return new Vector3((float)pelvis.X, (float)pelvis.Y, (float)pelvis.Z);
    }

    /// <summary>
    /// Scene 뷰에서 skeleton 시각화 (디버깅용)
    /// </summary>
    public void RenderSkeleton(Body skeleton, int skeletonNumber)
    {
        if (transform.childCount == 0)
            return;

        for (int jointNum = 0; jointNum < (int)JointId.Count; jointNum++)
        {
            Vector3 jointPos = new Vector3(
                skeleton.JointPositions3D[jointNum].X,
                -skeleton.JointPositions3D[jointNum].Y,
                skeleton.JointPositions3D[jointNum].Z
            );

            Quaternion jointRot = absoluteJointRotations[jointNum];

            // Joint sphere 위치/회전 업데이트
            if (transform.childCount > skeletonNumber &&
                transform.GetChild(skeletonNumber).childCount > jointNum)
            {
                Transform jointTransform = transform.GetChild(skeletonNumber).GetChild(jointNum);
                jointTransform.localPosition = jointPos;
                jointTransform.localRotation = jointRot;

                // Bone 렌더링 (부모와 연결)
                const int boneChildNum = 0;
                if (parentJointMap[(JointId)jointNum] != JointId.Head &&
                    parentJointMap[(JointId)jointNum] != JointId.Count)
                {
                    Vector3 parentPos = new Vector3(
                        skeleton.JointPositions3D[(int)parentJointMap[(JointId)jointNum]].X,
                        -skeleton.JointPositions3D[(int)parentJointMap[(JointId)jointNum]].Y,
                        skeleton.JointPositions3D[(int)parentJointMap[(JointId)jointNum]].Z
                    );

                    Vector3 boneDirection = jointPos - parentPos;
                    Vector3 boneDirectionWorld = transform.rotation * boneDirection;
                    Vector3 boneDirectionLocal = Quaternion.Inverse(jointTransform.rotation) * Vector3.Normalize(boneDirectionWorld);

                    if (jointTransform.childCount > boneChildNum)
                    {
                        Transform boneTransform = jointTransform.GetChild(boneChildNum);
                        boneTransform.localScale = new Vector3(1, 20.0f * 0.5f * boneDirectionWorld.magnitude, 1);
                        boneTransform.localRotation = Quaternion.FromToRotation(Vector3.up, boneDirectionLocal);
                        boneTransform.position = jointTransform.position - 0.5f * boneDirectionWorld;
                    }
                }
                else
                {
                    if (jointTransform.childCount > boneChildNum)
                        jointTransform.GetChild(boneChildNum).gameObject.SetActive(false);
                }
            }
        }
    }

    /// <summary>
    /// Skeleton 시각화 토글
    /// </summary>
    public void ToggleSkeletonVisualization()
    {
        drawSkeletons = !drawSkeletons;

        if (transform.childCount > 0)
        {
            const int bodyRenderedNum = 0;
            for (int jointNum = 0; jointNum < (int)JointId.Count; jointNum++)
            {
                if (transform.GetChild(bodyRenderedNum).childCount > jointNum)
                {
                    Transform jointTransform = transform.GetChild(bodyRenderedNum).GetChild(jointNum);
                    MeshRenderer mr = jointTransform.GetComponent<MeshRenderer>();
                    if (mr != null) mr.enabled = drawSkeletons;

                    if (jointTransform.childCount > 0)
                    {
                        MeshRenderer bmr = jointTransform.GetChild(0).GetComponent<MeshRenderer>();
                        if (bmr != null) bmr.enabled = drawSkeletons;
                    }
                }
            }
        }
    }

    /// <summary>
    /// Gizmo로 skeleton 표시
    /// </summary>
    void OnDrawGizmos()
    {
        if (currentBodyTrackingID == 0 || jointPositions == null)
            return;

        Gizmos.color = Color.green;

        for (int j = 0; j < (int)JointId.Count; j++)
        {
            Vector3 jp = jointPositions[j];
            Gizmos.DrawSphere(jp, 0.02f);

            JointId jid = (JointId)j;
            if (parentJointMap != null && parentJointMap.TryGetValue(jid, out JointId parent) && parent != JointId.Count)
            {
                Vector3 pp = jointPositions[(int)parent];
                Gizmos.DrawLine(jp, pp);
            }
        }
    }
}
