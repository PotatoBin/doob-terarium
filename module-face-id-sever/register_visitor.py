import cv2
import json
import uuid
from insightface.app import FaceAnalysis

DB_PATH = "face_db.json"

# ArcFace 모델 로드
app = FaceAnalysis(name='buffalo_l', providers=['CUDAExecutionProvider'])
app.prepare(ctx_id=0)

# 기존 DB 로드
try:
    with open(DB_PATH, "r") as f:
        face_db = json.load(f)
    print(f"📂 Loaded {len(face_db)} existing visitors")
except:
    face_db = {}
    print("📂 Creating new database")

cap = cv2.VideoCapture(0)
print("\n" + "="*50)
print("📸 Visitor Registration")
print("="*50)
print("Press [SPACE] to capture and register")
print("Press [ESC] to exit")
print("="*50 + "\n")

while True:
    ret, frame = cap.read()
    if not ret:
        break
    
    cv2.putText(frame, "Press SPACE to register", (10, 30), 
                cv2.FONT_HERSHEY_SIMPLEX, 1, (0, 255, 0), 2)
    cv2.imshow("Register Visitors", frame)
    
    key = cv2.waitKey(1)
    
    if key == 32:  # Space
        faces = app.get(frame)
        if len(faces) > 0:
            visitor_id = f"visitor_{uuid.uuid4().hex[:8]}"
            face_db[visitor_id] = faces[0].embedding.tolist()
            
            with open(DB_PATH, "w") as f:
                json.dump(face_db, f, indent=2)
            
            print(f"✅ Registered: {visitor_id}")
            
            cv2.putText(frame, f"Registered: {visitor_id}", (10, 70), 
                        cv2.FONT_HERSHEY_SIMPLEX, 0.7, (0, 255, 0), 2)
            cv2.imshow("Register Visitors", frame)
            cv2.waitKey(1000)
        else:
            print("❌ No face detected")
            cv2.putText(frame, "No face detected!", (10, 70), 
                        cv2.FONT_HERSHEY_SIMPLEX, 1, (0, 0, 255), 2)
            cv2.imshow("Register Visitors", frame)
            cv2.waitKey(1000)
    
    elif key == 27:  # ESC
        break

cap.release()
cv2.destroyAllWindows()

print("\n" + "="*50)
print(f"🎉 Registration Complete")
print(f"Total visitors: {len(face_db)}")
for vid in face_db.keys():
    print(f"  - {vid}")
print("="*50 + "\n")