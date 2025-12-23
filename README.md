# TERARiUM
Interactive media-art installation where visitors create a 3D avatar and an “Echo” companion, then interact with them in real time via full-body motion capture and LLM-driven behaviors.

## What’s Inside
- `module-unity-scripts/`: Unity runtime scripts for Azure Kinect tracking, face verification, OpenAI summarization, and Canvas API handoff.
- `module-canvas-web/`: Node.js web service (Express + WebSocket) for photo/doodle intake, persona info, chat, and motion-context API.
- `module-face-id-sever/`: Python/Flask face recognition server using InsightFace; stores embeddings and verification images.
- `module-motion-generation/`: Blender Python helper (`npy_to_fbx.py`) to convert HumanML-style `.npy` motions to FBX with loop smoothing.

## High-Level Pipeline
1) Visitor provides photo + doodle.  
2) Backend processes assets (generation + persona context) and forwards to Unity.  
3) Azure Kinect streams skeleton; Unity retargets to avatar + Echo.  
4) Motion sequence → OpenAI summary → Canvas API → persona response → drives emotes/animations.  
5) Face verification (optional) gates entry and links session to visitor_id.

## Prerequisites
- Hardware: Azure Kinect DK; camera for face capture; GPU recommended for diffusion/InsightFace.
- Software: Unity (matching your project), Node.js 18+, Python 3.10+ with CUDA/cuDNN for InsightFace, Blender 3.x for motion conversion.
- Keys/Endpoints: OpenAI API key; Canvas API endpoint; face server URL(s).

## Setup by Module

### Unity Runtime (`module-unity-scripts`)
- Place scripts into your Unity project and wire components:
  - `main_single.cs`: Single-user tracking pipeline; set `enableFaceRecognition` as needed.
  - `TrackerHandler_single`, `PuppetAvatar_single`: Kinect skeleton handling + Mecanim retargeting.
  - `MotionToCanvasPipeline.cs`: Samples motion frames, builds summary prompt, calls OpenAI, then posts to Canvas API.
  - `EntryDetector_single.cs` / `RGBFaceSender.cs`: Face verification helpers.
  - `FaceIdManager.cs`: Shares `visitor_id` across systems.
- Environment/Inspector configuration (avoids hardcoded secrets/IPs):
  - `OPENAI_API_KEY` → OpenAI calls (also settable in Inspector).
  - `CANVAS_ENDPOINT` → Canvas API base (default: `https://canvas.team-doob.com/api/motion-context`).
  - `FACE_RECOGNITION_URL` → EntryDetector POST target (default fallback: `http://localhost:5000/verify`).
  - `FACE_SERVER_URL` → RGBFaceSender POST target (default fallback: `http://localhost:5000/verify`).
- Run: Open the Unity project, assign scene objects, ensure Azure Kinect SDK drivers are installed, set env vars or Inspector fields, then press Play.

### Canvas Web Service (`module-canvas-web`)
- Install deps: `npm install`
- Env file `.env` (examples):
  - `OPENAI_API_KEY` (required)
  - `DRAWING_FORWARD_URL`, `WEBCAM_FORWARD_URL` (image forwarders; defaults point to LAN IPs—override for your setup)
  - `MOTION_DB_FALLBACK_PATH` (CSV fallback for motion DB)
  - `MOTION_EMBEDDING_MODEL` (default `text-embedding-3-small`)
  - `FACEID_DIRECT_HOST` (optional; face ID proxy)
  - `PUBLIC_BASE_URL`, `PORT` (optional)
- Run: `node app.js` (serves `public/`, APIs under `/api/*`, health at `/api/health`).

### Face Recognition Server (`module-face-id-sever`)
- Install deps: `pip install -r requirements.txt`
- Run: `python face_server.py` (saves processed images to `captured_images/`, DB at `face_db.json`).
- Register visitors: `python register_visitor.py --image <path> --visitor_id <id>`
- Notes: Uses InsightFace `buffalo_l`; on Windows adds CUDA bin path if present; adjust thresholds/paths inside `face_server.py` as needed.

### Motion Generation Helper (`module-motion-generation`)
- Script: `npy_to_fbx.py` (run inside Blender Python).
- Configure paths at top of the script:
  - `ORIGINAL_CSV_PATH` (metadata), `INPUT_DIR` (npy with XYZ joints), `OUTPUT_DIR` (FBX output), `NEW_CSV_PATH` (clean DB export).
- Features: rotation fix, smoothing, loop padding, active-frame trimming, Mixamo joint mapping.
- Run (example): `blender --background --python npy_to_fbx.py`

## End-to-End Run (Typical Exhibition)
1) Start face server: `python module-face-id-sever/face_server.py`
2) Start Canvas web service: `npm --prefix module-canvas-web start` (or `node app.js`)
3) Set env vars for Unity (`OPENAI_API_KEY`, `CANVAS_ENDPOINT`, `FACE_RECOGNITION_URL`, `FACE_SERVER_URL`) and launch the Unity scene.
4) Verify health: Canvas at `/api/health`; face server logs should show model load.
5) Walk through the in-app flow: photo → doodle → avatar/Echo generation → enter tracking zone → interact.

## Security & Ops
- Keep API keys in environment variables; avoid committing `.env` or Inspector secrets.
- Default URLs in code target localhost; override for production.
- Face server stores verification images in `captured_images/`; rotate or clean regularly.
- Monitor logs: Unity console for pipeline/LLM errors, Node stdout for upload/chat issues, Flask logs for face verification.
