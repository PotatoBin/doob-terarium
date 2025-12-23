import bpy
import numpy as np
import os
import math
import csv
import gc
from mathutils import Vector

# ================= [경로 설정] =================
# 1. 원본 CSV 파일 (참고용 명부)
ORIGINAL_CSV_PATH = "/home/ubuntu/Documents/HumanML/HumanML3D/HumanML3D/motion_database_final.csv"

# 2. 원본 모션 데이터 폴더 (XYZ 좌표 - new_joints)
INPUT_DIR = "/home/ubuntu/Documents/HumanML/HumanML3D/HumanML3D/new_joints"

# 3. 결과물 저장 폴더
OUTPUT_DIR = "/home/ubuntu/Documents/HumanML/HumanML3D/HumanML3D/final_motions_fbx_clean"

# 4. 새로 생성될 깨끗한 CSV 파일명
NEW_CSV_PATH = os.path.join(OUTPUT_DIR, "clean_motion_database.csv")

# ================= [옵션] =================
SCALE = 1.0 
FIX_ROTATION = -90 
MAKE_LOOP = True
LOOP_MIN_FRAMES = 15   
LOOP_MAX_FRAMES = 60
STOP_THRESHOLD = 0.002
SMOOTH_WINDOW = 5

MIXAMO_JOINT_NAMES = [
    "Mixamorig:Hips", "Mixamorig:LeftUpLeg", "Mixamorig:RightUpLeg", "Mixamorig:Spine",
    "Mixamorig:LeftLeg", "Mixamorig:RightLeg", "Mixamorig:Spine1", "Mixamorig:LeftFoot",
    "Mixamorig:RightFoot", "Mixamorig:Spine2", "Mixamorig:LeftToeBase", "Mixamorig:RightToeBase",
    "Mixamorig:Neck", "Mixamorig:LeftShoulder", "Mixamorig:RightShoulder", "Mixamorig:Head",
    "Mixamorig:LeftArm", "Mixamorig:RightArm", "Mixamorig:LeftForeArm", "Mixamorig:RightForeArm",
    "Mixamorig:LeftHand", "Mixamorig:RightHand"
]
SMPL_PARENTS = [-1, 0, 0, 0, 1, 2, 3, 4, 5, 6, 7, 8, 9, 9, 9, 12, 13, 14, 16, 17, 18, 19]

def process_motion_data(data):
    processed = data.copy()
    if FIX_ROTATION != 0:
        rad = math.radians(FIX_ROTATION)
        cos_r = math.cos(rad)
        sin_r = math.sin(rad)
        x = processed[:, :, 0].copy()
        z = processed[:, :, 2].copy()
        processed[:, :, 0] = x * cos_r - z * sin_r
        processed[:, :, 2] = x * sin_r + z * cos_r
    return processed

def detect_active_frames(data, threshold=0.002):
    total_frames = data.shape[0]
    diff = np.linalg.norm(data[1:] - data[:-1], axis=2)
    motion_energy = np.mean(diff, axis=1)
    real_end = total_frames
    for i in range(total_frames - 2, 0, -1):
        if motion_energy[i] > threshold:
            real_end = i + 2
            break
    return max(10, min(real_end, total_frames))

def apply_smoothing(data, window_size=5):
    if window_size <= 1: return data
    frames, joints, coords = data.shape
    smoothed_data = data.copy()
    kernel = np.ones(window_size) / window_size
    for j in range(joints):
        for c in range(coords):
            smoothed_data[:, j, c] = np.convolve(data[:, j, c], kernel, mode='same')
    return smoothed_data

def append_loop_frames(data):
    start_pose = data[0]
    end_pose = data[-1]
    distances = np.linalg.norm(start_pose - end_pose, axis=1)
    max_dist = np.max(distances)
    transition_len = int(max_dist * 40)
    transition_len = max(LOOP_MIN_FRAMES, min(LOOP_MAX_FRAMES, transition_len))
    t = np.linspace(0, 1, transition_len + 1)[1:]
    smooth_t = t * t * (3 - 2 * t)
    smooth_t = smooth_t[:, np.newaxis, np.newaxis]
    loop_frames = end_pose[np.newaxis, :, :] * (1 - smooth_t) + start_pose[np.newaxis, :, :] * smooth_t
    return np.concatenate([data, loop_frames], axis=0)

def convert_single_file(npy_path, output_path, clip_name):
    # 씬 완전 초기화 (메모리 누수 방지)
    bpy.ops.wm.read_homefile(use_empty=True)
    bpy.context.scene.render.fps = 20
    
    try:
        raw_data = np.load(npy_path)
    except Exception as e:
        # print(f"❌ 읽기 실패: {e}")
        return False

    # 데이터 차원 체크
    if len(raw_data.shape) == 2:
        frames, dims = raw_data.shape
        if dims == 263: return False # 263차원은 Skip
        if dims % 3 == 0:
            n_joints = dims // 3
            data_3d = raw_data.reshape(frames, n_joints, 3)
        else: return False
    elif len(raw_data.shape) == 3:
        data_3d = raw_data
    else: return False

    # 관절 수 체크
    frames_total, n_joints, _ = data_3d.shape
    if n_joints != 22:
        if n_joints > 22: data_3d = data_3d[:, :22, :]
        else: return False

    # 모션 가공
    try:
        corrected_data = process_motion_data(data_3d)
        active_len = detect_active_frames(corrected_data, threshold=STOP_THRESHOLD)
        trimmed_data = corrected_data[:active_len]
        smoothed_data = apply_smoothing(trimmed_data, window_size=SMOOTH_WINDOW)
        
        if MAKE_LOOP: final_data = append_loop_frames(smoothed_data)
        else: final_data = smoothed_data
        frames = final_data.shape[0]

        # 뼈대 생성
        bpy.ops.object.armature_add(enter_editmode=True, location=(0, 0, 0))
        rig = bpy.context.object; rig.name = "Armature"; amt = rig.data; amt.name = "ArmatureData"
        bpy.ops.armature.select_all(action='SELECT'); bpy.ops.armature.delete()

        bones = [None] * 22
        initial_pose = final_data[0] * SCALE
        
        for i, name in enumerate(MIXAMO_JOINT_NAMES):
            bone = amt.edit_bones.new(name)
            x, y, z = initial_pose[i]; bone.head = Vector((x, -z, y)); bone.tail = bone.head + Vector((0, 0, 0.1))
            bones[i] = bone

        for i, bone in enumerate(bones):
            if i >= len(SMPL_PARENTS): break
            parent_idx = SMPL_PARENTS[i]
            if parent_idx != -1:
                parent_bone = bones[parent_idx]; bone.parent = parent_bone; should_connect = True
                if parent_idx == 0 and i in [1, 2]: should_connect = False
                if parent_idx == 9 and i in [13, 14]: should_connect = False
                if should_connect: parent_bone.tail = bone.head; bone.use_connect = True
                else: bone.use_connect = False
            is_leaf = True
            for p in SMPL_PARENTS:
                if p == i: is_leaf = False; break
            if is_leaf and bone.parent:
                direction = (bone.head - bone.parent.head).normalized(); bone.tail = bone.head + (direction * 0.15 * SCALE)

        bpy.ops.object.mode_set(mode='OBJECT')
        
        # 애니메이션 키프레임
        empty_map = {}
        for i, name in enumerate(MIXAMO_JOINT_NAMES):
            bpy.ops.object.empty_add(type='PLAIN_AXES', radius=0.05)
            empty = bpy.context.object; empty.name = f"Loc_{name}"; empty_map[name] = empty
            for frame in range(frames):
                x, y, z = final_data[frame][i] * SCALE
                empty.location = Vector((x, -z, y)); empty.keyframe_insert(data_path="location", frame=frame)

        bpy.context.view_layer.objects.active = rig
        bpy.ops.object.mode_set(mode='POSE')
        bpy.ops.pose.select_all(action='SELECT') 
        for i, name in enumerate(MIXAMO_JOINT_NAMES):
            p_bone = rig.pose.bones.get(name); target = empty_map.get(name)
            if p_bone and target:
                const = p_bone.constraints.new(type='COPY_LOCATION'); const.target = target
                my_children = [idx for idx, p_idx in enumerate(SMPL_PARENTS) if p_idx == i]; target_child_idx = -1
                if len(my_children) == 1: target_child_idx = my_children[0]
                elif len(my_children) > 1:
                    if i == 0: target_child_idx = 3    
                    elif i == 9: target_child_idx = 12 
                if target_child_idx != -1 and target_child_idx < len(MIXAMO_JOINT_NAMES):
                    child_name = MIXAMO_JOINT_NAMES[target_child_idx]; child_empty = empty_map.get(child_name)
                    if child_empty: const_s = p_bone.constraints.new(type='STRETCH_TO'); const_s.target = child_empty; const_s.volume = 'NO_VOLUME'

        # 베이킹
        bpy.context.scene.frame_start = 0; bpy.context.scene.frame_end = frames - 1
        bpy.ops.nla.bake(frame_start=0, frame_end=frames-1, visual_keying=True, clear_constraints=True, bake_types={'POSE'}, use_current_action=True)
        
        # [중요] 클립 이름 고정
        if rig.animation_data and rig.animation_data.action:
            action = rig.animation_data.action; action.name = clip_name  
            for track in rig.animation_data.nla_tracks: rig.animation_data.nla_tracks.remove(track)
            track = rig.animation_data.nla_tracks.new(); track.name = clip_name   
            strip = track.strips.new(clip_name, int(action.frame_range[0]), action); strip.name = clip_name
            rig.animation_data.action = None

        bpy.ops.object.mode_set(mode='OBJECT'); bpy.ops.object.select_all(action='DESELECT')
        for e in empty_map.values(): e.select_set(True)
        bpy.ops.object.delete()
        
        rig.select_set(True)
        if not os.path.exists(os.path.dirname(output_path)): os.makedirs(os.path.dirname(output_path))
        
        # FBX 내보내기
        bpy.ops.export_scene.fbx(
            filepath=output_path, use_selection=True, add_leaf_bones=False, bake_anim=True,
            bake_anim_use_nla_strips=True, bake_anim_use_all_actions=False, bake_anim_force_startend_keying=False,
            bake_anim_simplify_factor=1.0, axis_forward='-Z', axis_up='Y'
        )
        return True # 성공
    except Exception:
        return False # 실패
    finally:
        gc.collect()

if __name__ == "__main__":
    bpy.context.preferences.edit.use_global_undo = False
    
    if not os.path.exists(OUTPUT_DIR): os.makedirs(OUTPUT_DIR)
    
    # 1. 원본 CSV 읽기
    print(f"📖 원본 CSV 로드 중: {ORIGINAL_CSV_PATH}")
    
    tasks = []
    try:
        with open(ORIGINAL_CSV_PATH, mode='r', encoding='utf-8') as f:
            reader = csv.DictReader(f)
            for row in reader:
                tasks.append(row)
    except FileNotFoundError:
        print("❌ CSV 파일이 없습니다.")
        import sys; sys.exit()

    print(f"🚀 총 {len(tasks)}개 데이터 처리 시작...")
    
    # 2. 새로운 CSV 준비
    csv_header = ['index', 'prompt', 'original_id', 'duration_sec', 'fbx_filename']
    
    # 파일을 쓰기 모드로 열어 헤더 미리 작성
    with open(NEW_CSV_PATH, mode='w', encoding='utf-8', newline='') as f:
        writer = csv.DictWriter(f, fieldnames=csv_header)
        writer.writeheader()

    # 3. 변환 루프 (성공 시에만 인덱스 증가)
    new_index = 1
    success_count = 0

    for row in tasks:
        original_id = row['original_id'].strip()
        npy_filename = f"{original_id}.npy"
        source_path = os.path.join(INPUT_DIR, npy_filename)
        
        # 파일이 없으면 -> 스킵 (인덱스 증가 X)
        if not os.path.exists(source_path):
            print(f"❌ [MISSING] 원본 없음: {npy_filename} -> Skip")
            continue
            
        # 변환될 파일명 (항상 순차적: 0001, 0002...)
        clip_name = f"{new_index:04d}"
        fbx_filename = f"{clip_name}.fbx"
        output_fbx_path = os.path.join(OUTPUT_DIR, fbx_filename)
        
        # 변환 시도
        is_success = convert_single_file(source_path, output_fbx_path, clip_name)
        
        if is_success:
            print(f"✅ [SUCCESS] {clip_name}.fbx (Orig: {original_id}) 생성 완료")
            
            # 새로운 CSV에 기록 (인덱스를 새로 부여)
            new_row = {
                'index': new_index,
                'prompt': row['prompt'],
                'original_id': original_id,
                'duration_sec': row['duration_sec'],
                'fbx_filename': fbx_filename
            }
            
            with open(NEW_CSV_PATH, mode='a', encoding='utf-8', newline='') as f:
                writer = csv.DictWriter(f, fieldnames=csv_header)
                writer.writerow(new_row)
            
            new_index += 1 # 성공했으므로 다음 번호로 이동
            success_count += 1
        else:
            print(f"⚠️ [FAIL] 변환 실패: {npy_filename} -> Skip")
            # 실패했으므로 new_index는 증가하지 않음 (다음 파일이 이 번호를 가져감)

        # 주기적 메모리 청소
        if success_count % 20 == 0:
            gc.collect()

    print(f"\n🎉 작업 완료!")
    print(f"📊 총 요청: {len(tasks)} / 성공: {success_count}")
    print(f"💾 결과물 폴더: {OUTPUT_DIR}")
    print(f"💾 새 CSV 명부: {NEW_CSV_PATH}")