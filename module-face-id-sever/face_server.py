"""
Face Recognition Server for Unity Integration
- Environment: Ubuntu 24.04 x86_64
- Features: Image Enhancement, Smart Resize, No Flip
- Storage: Saves processed images to './captured_images/'
"""
import os
import sys
import json
import time
import base64
import datetime
import numpy as np
import cv2
from numpy.linalg import norm
from insightface.app import FaceAnalysis
from flask import Flask, request, jsonify
from flask_cors import CORS

# ====================
# 환경 및 이미지 처리 설정
# ====================
if sys.platform == "win32":
    cuda_bin = r"C:\Program Files\NVIDIA GPU Computing Toolkit\CUDA\v12.6\bin"
    if os.path.isdir(cuda_bin): os.add_dll_directory(cuda_bin)

DB_PATH = "face_db.json"
SAVE_DIR = "captured_images"  # 이미지 저장 루트 폴더

IMG_CONFIG = {
    "FLIP_VERTICAL": False,     # 상하 반전 (Unity/Kinect 설정에 따라 변경)
    "TARGET_SIZE": 640,         # 모델 최적화 사이즈
    "CROP_CENTER": True,        # 중앙 중심 크롭
    "GAMMA": 1.2,               # 감마 보정
    "CONTRAST": 1.1,            # 대비
    "BRIGHTNESS": 10,           # 밝기
    "DEBUG_SAVE": True          # [True] 보정된 이미지를 디스크에 저장
}

SIMILARITY_THRESHOLD = 0.5

# ====================
# 앱 및 모델 초기화
# ====================
app = Flask(__name__)
CORS(app)

print(" Initializing Face Server on Ubuntu...")
face_app = FaceAnalysis(name='buffalo_l', providers=['CUDAExecutionProvider'])
face_app.prepare(ctx_id=0)
print("✅ Model Loaded")

# 저장 폴더 생성
os.makedirs(os.path.join(SAVE_DIR, "register"), exist_ok=True)
os.makedirs(os.path.join(SAVE_DIR, "verify"), exist_ok=True)

try:
    with open(DB_PATH, "r") as f: face_db = json.load(f)
    print(f" DB Loaded: {len(face_db)} visitors")
except:
    face_db = {}
    print("⚠️ New DB Created")

# ====================
# 이미지 처리 파이프라인
# ====================
def adjust_lut(image, gamma=1.0):
    if gamma == 1.0: return image
    inv_gamma = 1.0 / gamma
    table = np.array([((i / 255.0) ** inv_gamma) * 255 for i in np.arange(0, 256)]).astype("uint8")
    return cv2.LUT(image, table)

def save_processed_image(img, mode, tag="unknown"):
    """이미지 저장 (captured_images/mode/timestamp_tag.jpg)"""
    if not IMG_CONFIG["DEBUG_SAVE"]: return
    
    timestamp = datetime.datetime.now().strftime("%Y%m%d_%H%M%S")
    filename = f"{timestamp}_{tag}.jpg"
    path = os.path.join(SAVE_DIR, mode, filename)
    
    cv2.imwrite(path, img)
    # print(f" Saved: {path}") # 로그 너무 많으면 주석 처리

def process_image(image_base64, width, height):
    """Base64 -> Image -> Resize/Crop -> Enhance"""
    # 1. Decode
    img_bytes = base64.b64decode(image_base64)
    img_np = np.frombuffer(img_bytes, dtype=np.uint8)
    
    if img_np.size != (width * height * 3):
        eff_size = (img_np.size // 3) * 3
        img_np = img_np[:eff_size]
        h_eff = eff_size // (width * 3)
        img = img_np.reshape((h_eff, width, 3))
    else:
        img = img_np.reshape((height, width, 3))

    img = img[:, :, ::-1] # RGB to BGR

    # 2. Flip
    if IMG_CONFIG["FLIP_VERTICAL"]:
        img = cv2.flip(img, 0)

    # 3. Resize & Crop
    h, w = img.shape[:2]
    scale = IMG_CONFIG["TARGET_SIZE"] / min(h, w)
    new_w, new_h = int(w * scale), int(h * scale)
    img = cv2.resize(img, (new_w, new_h))

    if IMG_CONFIG["CROP_CENTER"]:
        start_x = (new_w - IMG_CONFIG["TARGET_SIZE"]) // 2
        start_y = (new_h - IMG_CONFIG["TARGET_SIZE"]) // 2
        img = img[start_y:start_y+IMG_CONFIG["TARGET_SIZE"], start_x:start_x+IMG_CONFIG["TARGET_SIZE"]]

    # 4. Enhance
    img = adjust_lut(img, IMG_CONFIG["GAMMA"])
    img = cv2.convertScaleAbs(img, alpha=IMG_CONFIG["CONTRAST"], beta=IMG_CONFIG["BRIGHTNESS"])

    return img

def find_match(embedding):
    best_id, best_sim = None, 0.0
    for vid, data in face_db.items():
        sim = np.dot(embedding, data) / (norm(embedding) * norm(data))
        if sim > best_sim:
            best_id, best_sim = vid, sim
    return best_id, best_sim

# ====================
# API 엔드포인트
# ====================
@app.route("/register", methods=["POST"])
def register():
    """ Visitor 등록 """
    global face_db
    try:
        d = request.json
        img = process_image(d["image"], d["width"], d["height"])
        
        # UUID 추출
        vid = str(d.get("uuid", "unknown"))
        if not vid.startswith("visitor_"): vid = f"visitor_{vid}"

        # 얼굴 인식 전에 이미지 저장 (디버깅용) 또는 성공 후에 저장 가능
        # 여기서는 인식 시도한 이미지를 무조건 저장
        save_processed_image(img, "register", tag=vid)

        faces = face_app.get(img)
        if not faces:
            print("❌ Register: No face")
            return jsonify({"status": "failed", "error": "No face"}), 200

        face = max(faces, key=lambda f: (f.bbox[2]-f.bbox[0]) * (f.bbox[3]-f.bbox[1]))
        
        face_db[vid] = face.embedding.tolist()
        with open(DB_PATH, "w") as f: json.dump(face_db, f, indent=2)
        
        print(f"✅ Registered: {vid}")
        return jsonify({"status": "success", "visitor_id": vid}), 200

    except Exception as e:
        print(f"❌ Error: {e}")
        return jsonify({"status": "failed", "error": str(e)}), 500

@app.route("/verify", methods=["POST"])
def verify():
    """ Visitor 인증 """
    try:
        d = request.json
        img = process_image(d.get("image"), d.get("width"), d.get("height"))
        
        faces = face_app.get(img)

        if not faces:
            save_processed_image(img, "verify", tag="noface") # 얼굴 못 찾은 이미지 저장
            return jsonify({"is_registered": False, "error": "No face"}), 200

        face = max(faces, key=lambda f: (f.bbox[2]-f.bbox[0]) * (f.bbox[3]-f.bbox[1]))
        vid, sim = find_match(face.embedding)

        # 결과에 따라 파일명 태그 설정
        tag = f"{vid}_ok" if (sim > SIMILARITY_THRESHOLD) else "unknown"
        save_processed_image(img, "verify", tag=tag)

        if sim > SIMILARITY_THRESHOLD:
            print(f"✅ Verified: {vid} ({sim:.2f})")
            return jsonify({"visitor_id": vid, "is_registered": True, "similarity": float(sim)})
        else:
            print(f"❌ Unknown ({sim:.2f})")
            return jsonify({"visitor_id": "", "is_registered": False, "similarity": float(sim)})

    except Exception as e:
        print(f"❌ Verify Error: {e}")
        return jsonify({"error": str(e)}), 500

@app.route("/status", methods=["GET"])
def status():
    return jsonify({"visitors": len(face_db), "threshold": SIMILARITY_THRESHOLD, "config": IMG_CONFIG})

if __name__ == "__main__":
    print(f"\n Server Started on 5000 | Thresh: {SIMILARITY_THRESHOLD} | Visitors: {len(face_db)}")
    app.run(host="0.0.0.0", port=5000, debug=False, threaded=True)
