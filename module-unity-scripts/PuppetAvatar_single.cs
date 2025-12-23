using System.Collections.Generic;
using UnityEngine;
using Microsoft.Azure.Kinect.BodyTracking;
using System.Text;

/// <summary>
/// Single Person Puppet Avatar
/// - ���� ����� 1���� ���� (���� ���� ���)
/// - T-Pose offset �� ��Ȱȭ �Ϻ� ����
/// - visitor_id ���� (���û���)
/// </summary>
public class PuppetAvatar_single : MonoBehaviour
{
    [Header("Tracking References")]
    public TrackerHandler_single KinectDevice;

    [Header("Visitor Identity (Optional)")]
    public string visitorId = ""; // �� �ν� ���� �� ���

    [Header("Root Transform Settings")]
    public GameObject RootPosition; // Pelvis ��ġ ������ (����)
    public Transform CharacterRootTransform; // ĳ���� ��Ʈ ��
    public float OffsetY = 0f; // Pelvis ���� ����
    public float OffsetZ = 0f; // Pelvis ���� ����

    [Header("Smoothing Configuration")]
    private const int bufferSize = 20; // ȸ�� ��Ȱȭ ���� ũ��

    // ���� ����
    private Animator puppetAnimator;
    private Dictionary<JointId, Quaternion> absoluteOffsetMap;
    private Dictionary<JointId, Queue<Quaternion>> rotationBuffer = new Dictionary<JointId, Queue<Quaternion>>();

    /// <summary>
    /// Azure Kinect JointId�� Unity HumanBodyBones�� ����
    /// </summary>
    private static HumanBodyBones MapKinectJoint(JointId joint)
    {
        switch (joint)
        {
            case JointId.Pelvis: return HumanBodyBones.Hips;
            case JointId.SpineNavel: return HumanBodyBones.Spine;
            case JointId.SpineChest: return HumanBodyBones.Chest;
            case JointId.Neck: return HumanBodyBones.Neck;
            case JointId.Head: return HumanBodyBones.Head;

            case JointId.HipLeft: return HumanBodyBones.LeftUpperLeg;
            case JointId.KneeLeft: return HumanBodyBones.LeftLowerLeg;
            case JointId.AnkleLeft: return HumanBodyBones.LeftFoot;
            case JointId.FootLeft: return HumanBodyBones.LeftToes;

            case JointId.HipRight: return HumanBodyBones.RightUpperLeg;
            case JointId.KneeRight: return HumanBodyBones.RightLowerLeg;
            case JointId.AnkleRight: return HumanBodyBones.RightFoot;
            case JointId.FootRight: return HumanBodyBones.RightToes;

            case JointId.ClavicleLeft: return HumanBodyBones.LeftShoulder;
            case JointId.ShoulderLeft: return HumanBodyBones.LeftUpperArm;
            case JointId.ElbowLeft: return HumanBodyBones.LeftLowerArm;
            case JointId.WristLeft: return HumanBodyBones.LeftHand;

            case JointId.ClavicleRight: return HumanBodyBones.RightShoulder;
            case JointId.ShoulderRight: return HumanBodyBones.RightUpperArm;
            case JointId.ElbowRight: return HumanBodyBones.RightLowerArm;
            case JointId.WristRight: return HumanBodyBones.RightHand;

            default: return HumanBodyBones.LastBone;
        }
    }

    void Start()
    {
        puppetAnimator = GetComponent<Animator>();

        if (puppetAnimator == null)
        {
            Debug.LogError($"[PuppetAvatar_single] {name}: Animator component required!");
            enabled = false;
            return;
        }

        // T-Pose absolute offset ���
        BuildAbsoluteOffsetMap();

        Debug.Log($"[PuppetAvatar_single] {name} initialized");
    }

    /// <summary>
    /// T-Pose ���� ���� offset ���
    /// </summary>
    private void BuildAbsoluteOffsetMap()
    {
        Transform rootJointTransform = CharacterRootTransform != null ? CharacterRootTransform : transform;
        absoluteOffsetMap = new Dictionary<JointId, Quaternion>();

        for (int i = 0; i < (int)JointId.Count; i++)
        {
            HumanBodyBones hbb = MapKinectJoint((JointId)i);
            if (hbb == HumanBodyBones.LastBone)
                continue;

            Transform boneTransform = puppetAnimator.GetBoneTransform(hbb);
            if (boneTransform == null)
                continue;

            Quaternion absOffset = GetSkeletonBone(puppetAnimator, boneTransform.name).rotation;

            // ��Ʈ���� �θ� ���� ȸ���� ����
            Transform current = boneTransform;
            while (!ReferenceEquals(current, rootJointTransform) && current != null)
            {
                current = current.parent;
                if (current == null) break;
                absOffset = GetSkeletonBone(puppetAnimator, current.name).rotation * absOffset;
            }

            absoluteOffsetMap[(JointId)i] = absOffset;

            // ��Ȱȭ ���� �ʱ�ȭ
            rotationBuffer[(JointId)i] = new Queue<Quaternion>(bufferSize);
        }
    }

    /// <summary>
    /// Animator�� Skeleton ���� ��������
    /// </summary>
    private static SkeletonBone GetSkeletonBone(Animator animator, string boneName)
    {
        int count = 0;
        StringBuilder cloneName = new StringBuilder(boneName);
        cloneName.Append("(Clone)");

        foreach (SkeletonBone sb in animator.avatar.humanDescription.skeleton)
        {
            if (sb.name == boneName || sb.name == cloneName.ToString())
                return animator.avatar.humanDescription.skeleton[count];
            count++;
        }
        return new SkeletonBone();
    }

    /// <summary>
    /// Visitor ID ���� (�� �ν� ���� ��)
    /// </summary>
    public void SetVisitorId(string id)
    {
        visitorId = id;
        Debug.Log($"[PuppetAvatar_single] Visitor ID set: {id}");
    }

    /// <summary>
    /// LateUpdate���� Skeleton �����͸� Animator�� ����
    /// </summary>
    void LateUpdate()
    {
        if (KinectDevice == null || puppetAnimator == null)
            return;

        // TrackerHandler�� absoluteJointRotations ��� (���� �迭)
        for (int j = 0; j < (int)JointId.Count; j++)
        {
            JointId joint = (JointId)j;
            HumanBodyBones hbb = MapKinectJoint(joint);

            if (hbb == HumanBodyBones.LastBone || !absoluteOffsetMap.ContainsKey(joint))
                continue;

            Transform finalJoint = puppetAnimator.GetBoneTransform(hbb);
            if (finalJoint == null)
                continue;

            // ���� ȸ���� ��������
            Quaternion currentRot = KinectDevice.absoluteJointRotations[j];

            // ��Ȱȭ ó��
            Queue<Quaternion> buffer = rotationBuffer[joint];
            if (buffer.Count >= bufferSize)
                buffer.Dequeue();
            buffer.Enqueue(currentRot);

            Quaternion smoothedRot = AverageQuaternion(buffer);

            // T-Pose offset ����
            Quaternion absOffset = absoluteOffsetMap[joint];
            finalJoint.rotation = absOffset * Quaternion.Inverse(absOffset) * smoothedRot * absOffset;

            // Pelvis ��ġ ����
            if (j == 0) // JointId.Pelvis
            {
                Vector3 basePosition = CharacterRootTransform != null
                    ? CharacterRootTransform.position
                    : transform.position;

                if (RootPosition != null)
                {
                    finalJoint.position = basePosition + new Vector3(
                        RootPosition.transform.localPosition.x,
                        RootPosition.transform.localPosition.y + OffsetY,
                        RootPosition.transform.localPosition.z - OffsetZ
                    );
                }
                else
                {
                    finalJoint.position = basePosition + new Vector3(0, OffsetY, -OffsetZ);
                }
            }
        }
    }

    /// <summary>
    /// ���� Quaternion�� ��� ��� (Slerp ���)
    /// </summary>
    private static Quaternion AverageQuaternion(IEnumerable<Quaternion> quaternions)
    {
        Quaternion[] qArray = new List<Quaternion>(quaternions).ToArray();
        if (qArray.Length == 0) return Quaternion.identity;
        if (qArray.Length == 1) return qArray[0];

        Quaternion avg = qArray[0];
        for (int i = 1; i < qArray.Length; i++)
            avg = Quaternion.Slerp(avg, qArray[i], 1f / (i + 1f));
        return avg;
    }
}