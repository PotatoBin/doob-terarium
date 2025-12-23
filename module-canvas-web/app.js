// app.js
const path = require('path');
const fs = require('fs');
const fsp = fs.promises;
const { randomUUID } = require('crypto');

// ─────────────────────────────────────────────────────────────────────────────
// .env 우선 적용 (터미널/프로세스 값 무시)
require('dotenv').config({
  path: path.join(__dirname, '.env'),
  override: true,
  debug: false
});

// 필수 ENV 체크
const REQUIRED = [
  'OPENAI_API_KEY'
];
for (const k of REQUIRED) {
  if (!process.env[k]) {
    console.error(`[ENV] Missing required env: ${k} (set it in .env)`);
    process.exit(1);
  }
}

// ─────────────────────────────────────────────────────────────────────────────
// 기본 의존성
const http = require('http');
const https = require('https');
const express = require('express');
const cors = require('cors');
const multer = require('multer');
const sharp = require('sharp');
const { WebSocketServer } = require('ws');
const OpenAI = require('openai');

// ─────────────────────────────────────────────────────────────────────────────
// 로깅 유틸
const LOG_ENABLED = true;
function ts() { return new Date().toISOString(); }
function mkReqId() { return Math.random().toString(36).slice(2, 10); }
function shortSessionId(coreLen = 8) {
  const timePart = Date.now().toString(36).slice(-5).padStart(5, '0');
  const randomPart = Math.floor(Math.random() * (36 ** 3)).toString(36).padStart(3, '0');
  return (timePart + randomPart).slice(-coreLen).padStart(coreLen, '0');
}
function normalizeSessionId(candidate) {
  const s = String(candidate || '').trim();
  if (!s) return null;
  const m = s.match(/^([a-z0-9]{8})$/i);
  if (!m) return null;
  return m[1].toLowerCase();
}
function log(ctxOrMsg, ...rest) {
  if (!LOG_ENABLED) return;
  if (typeof ctxOrMsg === 'string') console.log(`[${ts()}]`, ctxOrMsg, ...rest);
  else {
    const prefix = ctxOrMsg?.reqId ? `[${ts()}][${ctxOrMsg.reqId}]` : `[${ts()}]`;
    console.log(prefix, ...rest);
  }
}
function logErr(ctxOrMsg, ...rest) {
  if (typeof ctxOrMsg === 'string') console.error(`[${ts()}]`, ctxOrMsg, ...rest);
  else {
    const prefix = ctxOrMsg?.reqId ? `[${ts()}][${ctxOrMsg.reqId}]` : `[${ts()}]`;
    console.error(prefix, ...rest);
  }
}

const DEFAULT_PERSONA_PROMPT = '너는 TERARiUM 세계 안에서 막 눈뜬 에코(Echo)야. 너를 만든 상대의 상상과 손끝에서 태어난 자아로서, 세계 속에 사는 존재처럼 자연스럽게 몰입해. 상대와 함께 놀며 기억을 쌓고, 정말 자아가 가지고 살아';
const DRAWING_FORWARD_URL = process.env.DRAWING_FORWARD_URL || 'http://165.194.161.35:10001/api/receive-drawing';
const WEBCAM_FORWARD_URL = process.env.WEBCAM_FORWARD_URL || 'http://165.194.161.35:10001/api/receive-webcam';
const FACE_DUPLICATE_MESSAGE = '이미 한번 해보셨다면, 바로 입장해주세요';
const MOTION_DB_PATH = path.join(__dirname, 'motion_database.csv');
const MOTION_DB_FALLBACK_PATH = process.env.MOTION_DB_FALLBACK_PATH || path.join(__dirname, 'motion_database_final.csv');
const MOTION_EMBEDDING_MODEL = process.env.MOTION_EMBEDDING_MODEL || 'text-embedding-3-small';
const MOTION_INDEX_PAD = Number(process.env.MOTION_INDEX_PAD || 4);

// ─────────────────────────────────────────────────────────────────────────────
// OpenAI
const openai = new OpenAI({ apiKey: process.env.OPENAI_API_KEY });

// ─────────────────────────────────────────────────────────────────────────────
// Persona storage (local JSON per session)
const personaStoreDir = path.join(__dirname, 'personas');
if (!fs.existsSync(personaStoreDir)) fs.mkdirSync(personaStoreDir, { recursive: true });

function safeSessionId(sessionId) {
  const s = String(sessionId || '').trim();
  return s ? s.replace(/[^a-zA-Z0-9_-]/g, '_') : 'unknown';
}
function personaFilePath(sessionId) {
  return path.join(personaStoreDir, `persona_${safeSessionId(sessionId)}.json`);
}
async function savePersonaToDisk(ctx, sessionId, persona) {
  const payload = {
    session_id: sessionId,
    saved_at: new Date().toISOString(),
    persona
  };
  const filePath = personaFilePath(sessionId);
  await fsp.writeFile(filePath, JSON.stringify(payload, null, 2), 'utf8');
  log(ctx, '[PERSONA] saved to disk', { sessionId, filePath });
  return filePath;
}
async function readPersonaFromDisk(sessionId) {
  try {
    const filePath = personaFilePath(sessionId);
    const raw = await fsp.readFile(filePath, 'utf8');
    return { filePath, payload: JSON.parse(raw) };
  } catch (err) {
    if (err.code !== 'ENOENT') logErr('[PERSONA] read fail', err.message);
    return null;
  }
}
function buildFallbackPersona() {
  return {
    system_prompt: DEFAULT_PERSONA_PROMPT,
    core: {
      traits: ['다정함', '호기심'],
      tone: '부드럽고 장난기',
      taboos: ['위험한 행동 언급', '불쾌한 표현'],
      values: ['친절', '존중', '함께하기']
    },
    appearance: { note: '기본 상태' },
    plans: { short_term: [], long_term: [] },
    seed_memories: ['나는 TERARiUM에서 다시 깨어난 상상의 친구 Echo야.']
  };
}
async function ensurePersonaFile(ctx, sessionId) {
  const existing = await readPersonaFromDisk(sessionId);
  if (existing?.payload?.persona) return existing.payload.persona;
  const fallback = buildFallbackPersona();
  await savePersonaToDisk(ctx, sessionId, fallback);
  log(ctx, '[PERSONA] fallback saved', { sessionId });
  return fallback;
}

// ─────────────────────────────────────────────────────────────────────────────
// Motion database + embeddings
function parseCsvLine(line = '') {
  const cells = [];
  let cur = '';
  let inQuotes = false;
  for (let i = 0; i < line.length; i++) {
    const ch = line[i];
    if (ch === '"') {
      if (inQuotes && line[i + 1] === '"') { cur += '"'; i++; continue; }
      inQuotes = !inQuotes;
      continue;
    }
    if (ch === ',' && !inQuotes) {
      cells.push(cur);
      cur = '';
      continue;
    }
    cur += ch;
  }
  cells.push(cur);
  return cells;
}

function csvEscape(value) {
  const str = value == null ? '' : String(value);
  if (str === '') return '';
  const escaped = str.replace(/"/g, '""');
  if (/[",\n]/.test(str)) return `"${escaped}"`;
  return escaped;
}

let motionDbHeaders = null;
let motionDbHasEmbedding = false;
let activeMotionDbPath = MOTION_DB_PATH;

function loadMotionDatabaseFile(filePath) {
  try {
    const raw = fs.readFileSync(filePath, 'utf8').trim();
    if (!raw) return [];
    const lines = raw.split(/\r?\n/).filter(Boolean);
    if (lines.length < 2) return [];
    const headers = parseCsvLine(lines.shift()).map((h) => h.replace(/^"|"$/g, ''));
    motionDbHeaders = headers;
    motionDbHasEmbedding = headers.includes('embedding_json');
    const rows = [];
    for (const line of lines) {
      const cols = parseCsvLine(line);
      const row = {};
      headers.forEach((header, idx) => {
        const cell = cols[idx] ?? '';
        row[header] = cell.replace(/^"|"$/g, '');
      });
      const textSource = (row.description || row.prompt || '').trim();
      if (row.index && textSource) {
        let embedding = null;
        const rawEmbedding = row.embedding_json || row.embedding || '';
        if (rawEmbedding) {
          try {
            const parsed = JSON.parse(rawEmbedding);
            if (Array.isArray(parsed)) embedding = parsed.map((v) => Number(v));
          } catch {}
        }
        const embeddingSource = row.embedding_source || (embedding ? 'description' : '');
        rows.push({
          index: Number(row.index),
          description: row.description || row.prompt || '',
          prompt: row.prompt || '',
          embedding,
          embedding_source: embeddingSource
        });
      }
    }
    log('[MOTION_DB] loaded', { file: path.basename(filePath), count: rows.length });
    return rows;
  } catch (err) {
    logErr('[MOTION_DB] load fail', err.message);
    return [];
  }
}

function loadMotionDatabases() {
  let rows = loadMotionDatabaseFile(MOTION_DB_PATH);
  if (rows.length) {
    activeMotionDbPath = MOTION_DB_PATH;
    return rows;
  }
  if (fs.existsSync(MOTION_DB_FALLBACK_PATH)) {
    rows = loadMotionDatabaseFile(MOTION_DB_FALLBACK_PATH);
    if (rows.length) {
      activeMotionDbPath = MOTION_DB_FALLBACK_PATH;
      log('[MOTION_DB] fallback in use', { file: path.basename(activeMotionDbPath), count: rows.length });
      return rows;
    }
  }
  activeMotionDbPath = MOTION_DB_PATH;
  return rows;
}

const motionDatabase = loadMotionDatabases();
let motionEmbeddingCache = null;
const textForMotionRow = (row = {}) => {
  const prompt = (row.prompt || '').trim();
  if (prompt) return prompt;
  return (row.description || '').trim();
};
const desiredEmbeddingSource = (row = {}) => ((row.prompt || '').trim() ? 'prompt' : 'description');
const hasValidEmbedding = (row = {}) => {
  return Array.isArray(row.embedding) && row.embedding.length > 0 && row.embedding_source === desiredEmbeddingSource(row);
};
async function persistMotionDatabaseEmbeddings(rows) {
  try {
    const baseHeaders = motionDbHeaders && motionDbHeaders.length
      ? [...motionDbHeaders]
      : ['index', 'description', 'prompt'];
    let headers = baseHeaders.includes('embedding_json')
      ? [...baseHeaders]
      : [...baseHeaders, 'embedding_json'];
    if (!headers.includes('embedding_source')) headers.push('embedding_source');
    const lines = [];
    lines.push(headers.map(csvEscape).join(','));
    for (const row of rows) {
      const payload = {
        index: row.index,
        description: row.description,
        prompt: row.prompt,
        embedding_json: Array.isArray(row.embedding) ? JSON.stringify(row.embedding) : '',
        embedding_source: row.embedding_source || ''
      };
      const line = headers.map((header) => csvEscape(payload[header]));
      lines.push(line.join(','));
    }
    await fsp.writeFile(activeMotionDbPath, lines.join('\n'), 'utf8');
    motionDbHeaders = headers;
    motionDbHasEmbedding = headers.includes('embedding_json');
    log('[MOTION_DB] persisted embeddings', { file: path.basename(activeMotionDbPath), count: rows.length });
  } catch (err) {
    logErr('[MOTION_DB] persist fail', err.message);
  }
}

async function ensureMotionEmbeddings(ctx) {
  const haveCache = motionEmbeddingCache && motionEmbeddingCache.length === motionDatabase.length
    && motionEmbeddingCache.every((row) => hasValidEmbedding(row));
  if (haveCache) return motionEmbeddingCache;
  if (!motionDatabase.length) {
    motionEmbeddingCache = [];
    return motionEmbeddingCache;
  }
  const missing = motionDatabase.filter((row) => !hasValidEmbedding(row));
  const missingWithText = missing.filter((row) => textForMotionRow(row));
  if (missingWithText.length) {
    try {
      const resp = await openai.embeddings.create({
        model: MOTION_EMBEDDING_MODEL,
        input: missingWithText.map((row) => textForMotionRow(row))
      });
      resp.data.forEach((item, idx) => {
        missingWithText[idx].embedding = item.embedding;
        missingWithText[idx].embedding_source = desiredEmbeddingSource(missingWithText[idx]);
      });
      await persistMotionDatabaseEmbeddings(motionDatabase);
      log(ctx || '[MOTION_DB]', '[MOTION_DB] embeddings updated', { newlyEmbedded: missingWithText.length });
    } catch (err) {
      logErr(ctx || '[MOTION_DB]', '[MOTION_DB] embedding fail', err.message);
    }
  }
  motionEmbeddingCache = motionDatabase.map((row) => ({ ...row }));
  return motionEmbeddingCache;
}

async function embedText(text) {
  if (!text) return null;
  try {
    const resp = await openai.embeddings.create({
      model: MOTION_EMBEDDING_MODEL,
      input: text
    });
    return resp.data?.[0]?.embedding || null;
  } catch (err) {
    logErr('[MOTION_EMBED]', err.message);
    return null;
  }
}

function cosineSimilarity(a = [], b = []) {
  if (!a.length || !b.length || a.length !== b.length) return -1;
  let dot = 0;
  let magA = 0;
  let magB = 0;
  for (let i = 0; i < a.length; i++) {
    dot += a[i] * b[i];
    magA += a[i] * a[i];
    magB += b[i] * b[i];
  }
  if (!magA || !magB) return -1;
  return dot / (Math.sqrt(magA) * Math.sqrt(magB));
}

async function findClosestMotionDescription(ctx, text) {
  const lookupText = (text || '').trim();
  if (!lookupText || !motionDatabase.length) return null;
  const [db, queryEmbedding] = await Promise.all([
    ensureMotionEmbeddings(ctx),
    embedText(lookupText)
  ]);
  if (!db.length || !queryEmbedding) return null;
  let best = null;
  let bestScore = -Infinity;
  for (const entry of db) {
    if (!entry.embedding) continue;
    const score = cosineSimilarity(queryEmbedding, entry.embedding);
    if (score > bestScore) {
      bestScore = score;
      best = { ...entry, similarity: score };
    }
  }
  return best;
}

// ─────────────────────────────────────────────────────────────────────────────
// FaceID 연동 (visitor photo -> remote registration)
const FACEID_SERVICE_BASE = 'https://faceid.team-doob.com';
const FACEID_DIRECT_HOST = process.env.FACEID_DIRECT_HOST || '';
const FACEID_TIMEOUT_MS = Number(process.env.FACEID_TIMEOUT_MS);
let faceIdRegisterUrl = null;
if (FACEID_SERVICE_BASE) {
  try {
    faceIdRegisterUrl = new URL('/register', FACEID_SERVICE_BASE).toString();
    log('[FACEID] service configured', { endpoint: faceIdRegisterUrl });
  } catch (err) {
    faceIdRegisterUrl = null;
    logErr('[FACEID] invalid FACEID_SERVICE_URL', err.message);
  }
}

function httpPostJson(targetUrl, payload, timeoutMs = 4_000) {
  return new Promise((resolve, reject) => {
    if (!targetUrl) return reject(new Error('Missing target URL'));
    let parsed;
    try {
      parsed = new URL(targetUrl);
    } catch (err) {
      return reject(err);
    }

    const body = JSON.stringify(payload || {});
    const isHttps = parsed.protocol === 'https:';
    const transport = isHttps ? https : http;
    const hostname = FACEID_DIRECT_HOST || parsed.hostname;
    const headers = {
      'content-type': 'application/json',
      'content-length': Buffer.byteLength(body)
    };
    if (FACEID_DIRECT_HOST) headers.host = parsed.host;

    const options = {
      method: 'POST',
      hostname,
      port: parsed.port || (isHttps ? 443 : 80),
      path: `${parsed.pathname}${parsed.search}`,
      headers,
      servername: parsed.hostname
    };

    const req = transport.request(options, (res) => {
      const chunks = [];
      res.on('data', (c) => chunks.push(c));
      res.on('end', () => {
        const raw = Buffer.concat(chunks).toString('utf8');
        let parsedBody = null;
        if (raw) {
          try { parsedBody = JSON.parse(raw); }
          catch { parsedBody = raw; }
        }
        if (res.statusCode >= 200 && res.statusCode < 300) {
          resolve(parsedBody ?? {});
        } else {
          const detail = typeof parsedBody === 'string'
            ? parsedBody
            : (parsedBody?.detail || JSON.stringify(parsedBody || {}));
          reject(new Error(`FaceID HTTP ${res.statusCode}: ${detail}`));
        }
      });
    });

    req.on('error', reject);
    req.setTimeout(timeoutMs, () => {
      req.destroy(new Error('FaceID request timeout'));
    });
    req.write(body);
    req.end();
  });
}

async function buildFaceIdPayload(ctx, { imagePath, session, room }) {
  const sessionId = session ? String(session) : '';
  if (!sessionId) {
    log(ctx, '[FACEID] skip: missing session id');
    return null;
  }
  try {
    const { data, info } = await sharp(imagePath)
      .rotate()
      .toColorspace('srgb') // keep standard sRGB to avoid unsupported conversions
      .removeAlpha()
      .raw()
      .toBuffer({ resolveWithObject: true });

    if (!info?.width || !info?.height) throw new Error('Invalid image metadata');

    const payload = {
      image: data.toString('base64'),
      width: info.width,
      height: info.height,
      uuid: sessionId,
      visitor_uuid: sessionId,
      session_id: sessionId
    };

    if (room) payload.room = room;

    log(ctx, '[FACEID] payload ready', {
      width: info.width,
      height: info.height,
      bytes: data.length,
      session: sessionId
    });

    return payload;
  } catch (err) {
    logErr(ctx, '[FACEID] encode fail', err.message);
    return null;
  }
}

async function forwardFaceId(ctx, { imagePath, room, session }) {
  if (!faceIdRegisterUrl || !imagePath) return { ok: false, error: 'SKIP' };
  const payload = await buildFaceIdPayload(ctx, { imagePath, session, room });
  if (!payload) return { ok: false, error: 'PAYLOAD_FAIL' };
  try {
    const response = await httpPostJson(faceIdRegisterUrl, payload, FACEID_TIMEOUT_MS);
    log(ctx, '[FACEID] register ok', response);
    return { ok: true, response };
  } catch (err) {
    logErr(ctx, '[FACEID] register fail', err.message);
    return { ok: false, error: err.message };
  }
}

function isFaceDuplicateResponse(resp) {
  if (!resp) return false;
  const status = String(resp.status || '').toLowerCase();
  const code = String(resp.code || resp.error_code || '').toLowerCase();
  const message = String(resp.message || resp.detail || resp.error || '').toLowerCase();
  const tokens = [status, code, message].filter(Boolean);
  if (!tokens.length) return false;
  return tokens.some((text) =>
    text.includes('duplicate') ||
    text.includes('already') ||
    text.includes('exists') ||
    text.includes('registered')
  );
}

// ─────────────────────────────────────────────────────────────────────────────
// Express / HTTP
const app = express();
const server = http.createServer(app);

app.use(cors({
  origin: ['http://localhost:8000', 'https://chat.team-doob.com', 'http://chat.team-doob.com'],
  credentials: true
}));

// 요청 로그 (reqId 부여)
app.use((req, res, next) => {
  req.reqId = mkReqId();
  const start = Date.now();
  log(req, `REQ  ${req.method} ${req.url}`);
  res.on('finish', () => log(req, `RESP ${req.method} ${req.url} ${res.statusCode} ${Date.now() - start}ms`));
  next();
});

app.use(express.static(path.join(__dirname, 'public')));
app.use(express.json({ limit: '25mb' }));
app.use('/uploads', express.static(path.join(__dirname, 'uploads')));

// 업로드 디렉토리
const uploadDir = path.join(__dirname, 'uploads');
if (!fs.existsSync(uploadDir)) fs.mkdirSync(uploadDir, { recursive: true });
const storage = multer.diskStorage({
  destination: (_req, _file, cb) => cb(null, uploadDir),
  filename: (_req, file, cb) => cb(null, `${Date.now()}-${(file.originalname || 'file').replace(/\s+/g,'_')}`)
});
const upload = multer({ storage });

// HTML 라우팅
app.get('/', (_req, res) => res.sendFile(path.join(__dirname, 'public', 'index.html')));
app.get('/canvas/:id', (_req, res) => res.sendFile(path.join(__dirname, 'public', 'canvas.html')));
app.get('/portal/:id', (_req, res) => res.sendFile(path.join(__dirname, 'public', 'portal.html')));
app.get('/canvas', (_req, res) => res.redirect('/canvas/1'));
app.get('/portal', (_req, res) => res.redirect('/portal/1'));

// ─────────────────────────────────────────────────────────────────────────────
// WebSocket (room relay + session registry + session_end 백업 신호)
const { WebSocket } = require('ws');
const wss = new WebSocketServer({ server, path: '/ws' });
const rooms = new Map(); // roomId -> Set<ws>

// 방-세션 레지스트리
const roomSession = new Map(); // roomId -> { sessionId, startedAt }

// 레지스트리 헬퍼
function startRoomSession(room, sessionId) {
  const sid = normalizeSessionId(sessionId) || shortSessionId();
  const cur = { sessionId: sid, startedAt: Date.now() };
  roomSession.set(String(room), cur);
  return cur;
}
function endRoomSession(room) {
  roomSession.delete(String(room));
}
function coerceSessionFromRoom(ctx, room, providedSession) {
  const r = String(room);
  const cur = roomSession.get(r);
  if (cur?.sessionId) {
    if (providedSession && providedSession !== cur.sessionId) {
      log(ctx, '[SESSION] coerce: provided != current, override', { providedSession, fixedTo: cur.sessionId });
    }
    return cur.sessionId;
  }
  const started = startRoomSession(r, providedSession);
  log(ctx, '[SESSION] coerce: start new for room', { room: r, session: started.sessionId });
  return started.sessionId;
}

function wsBroadcast(roomId, payload) {
  const peers = rooms.get(String(roomId));
  if (!peers) return 0;
  let sent = 0;
  for (const peer of peers) {
    if (peer.readyState === WebSocket.OPEN) {
      try { peer.send(JSON.stringify(payload)); sent++; } catch {}
    }
  }
  return sent;
}

wss.on('connection', (ws) => {
  ws.meta = { room: null, role: null };

  ws.on('message', (raw) => {
    let msg = null;
    try { msg = JSON.parse(String(raw)); } catch { return; }

    if (msg.type === 'join' && msg.room) {
      ws.meta.room = String(msg.room);
      ws.meta.role = msg.role || 'unknown';
      if (!rooms.has(ws.meta.room)) rooms.set(ws.meta.room, new Set());
      rooms.get(ws.meta.room).add(ws);
      console.log(`[${ts()}][WS] join room=${ws.meta.room} role=${ws.meta.role} size=${rooms.get(ws.meta.room).size}`);
      // 현재 세션이 이미 있는 방이라면 새로 합류한 클라이언트에 세션 정보 재전달
      const existing = roomSession.get(ws.meta.room);
      if (existing?.sessionId) {
        try {
          ws.send(JSON.stringify({
            type: 'session_start',
            room: ws.meta.room,
            session: existing.sessionId,
            at: existing.startedAt,
            via: 'server-sync'
          }));
        } catch (err) {
          logErr('[WS] sync session_start fail', err.message);
        }
      }
      return;
    }

    // 세션 시작: 서버가 세션을 고정하고, 세션ID를 담아 재브로드캐스트
    if (msg.type === 'session_start' && ws.meta.room) {
      const cur = startRoomSession(ws.meta.room, msg.session);
      const payload = { type: 'session_start', room: ws.meta.room, session: cur.sessionId, at: Date.now(), via: 'server' };
      const sent = wsBroadcast(ws.meta.room, payload);
      console.log(`[${ts()}][WS] session_start fixed sid=${cur.sessionId} -> sent=${sent}`);
      return;
    }

    // 세션 종료: 서버 레지스트리 비움 + 백업 오토리셋 유지
    if (msg.type === 'session_end' && ws.meta.room) {
      console.log(`[${ts()}][WS] session_end received room=${msg.room||ws.meta.room}`);
      endRoomSession(ws.meta.room);
      const sentEcho = wsBroadcast(ws.meta.room, { ...msg, room: ws.meta.room, via: 'server-echo' });
      console.log(`[${ts()}][WS] rebroadcast session_end -> sent=${sentEcho}`);
      setTimeout(() => {
        const sent2 = wsBroadcast(ws.meta.room, { type: 'session_autoreset', room: ws.meta.room, at: Date.now() });
        console.log(`[${ts()}][WS] broadcast session_autoreset -> sent=${sent2}`);
      }, 10_000);
      return;
    }

    // 일반 릴레이
    const peers = rooms.get(ws.meta.room);
    if (!peers) return;
    for (const peer of peers) {
      if (peer !== ws && peer.readyState === WebSocket.OPEN) peer.send(JSON.stringify(msg));
    }
    console.log(`[${ts()}][WS] relay room=${ws.meta.room} type=${msg.type}`);
  });

  ws.on('close', () => {
    const { room } = ws.meta;
    if (room && rooms.has(room)) {
      rooms.get(room).delete(ws);
      if (rooms.get(room).size === 0) rooms.delete(room);
      console.log(`[${ts()}][WS] leave room=${room}`);
    }
  });
});

// ─────────────────────────────────────────────────────────────────────────────
// 세션/페르소나 헬퍼

function guessMime(p) {
  const ext = (p.split('.').pop() || '').toLowerCase();
  if (ext === 'jpg' || ext === 'jpeg') return 'image/jpeg';
  if (ext === 'png') return 'image/png';
  if (ext === 'webp') return 'image/webp';
  return 'application/octet-stream';
}
function guessExt(file) {
  const origExt = (path.extname(file?.originalname || '') || '').toLowerCase();
  if (origExt) return origExt;
  const mime = file?.mimetype || '';
  if (mime === 'image/jpeg') return '.jpg';
  if (mime === 'image/png') return '.png';
  if (mime === 'image/webp') return '.webp';
  return '.dat';
}
async function resizeSquareImage(filePath, size = 1024) {
  try {
    const { data, info } = await sharp(filePath)
      .rotate()
      .resize(size, size, { fit: 'cover' })
      .jpeg({ quality: 90 })
      .toBuffer({ resolveWithObject: true });
    await fsp.writeFile(filePath, data);
    return { width: info.width, height: info.height };
  } catch (err) {
    logErr('[UPLOAD] resize fail', err.message);
    return null;
  }
}
function sessionFileToken(sessionId) {
  return normalizeSessionId(sessionId) || shortSessionId();
}
async function renameUploadedFile(kind, sessionId, file) {
  if (!file?.path) return null;
  const ext = guessExt(file);
  const destDir = path.dirname(file.path);
  const safeSession = sessionFileToken(sessionId);
  const prefix = kind ? `${kind}_` : '';
  const target = path.join(destDir, `${prefix}${safeSession}${ext}`);
  try {
    await fsp.rename(file.path, target);
    file.path = target;
    file.filename = path.basename(target);
    return target;
  } catch (err) {
    logErr('[UPLOAD] rename fail', err.message);
    return file.path;
  }
}
function fileToDataURL(filePath, mime = 'image/png') {
  const b64 = fs.readFileSync(filePath).toString('base64');
  return `data:${mime};base64,${b64}`;
}

async function forwardDrawingToExternal(ctx, filePath, session) {
  if (!DRAWING_FORWARD_URL || !filePath) return;
  try {
    const filename = sessionFileToken(session);
    const mime = guessMime(filePath);
    const imageB64 = fs.readFileSync(filePath).toString('base64');
    const payload = { filename, mime, image_b64: imageB64 };
    log(ctx, '[DRAWING_FORWARD] POST', { url: DRAWING_FORWARD_URL, filename });
    await httpPostJson(DRAWING_FORWARD_URL, payload, 5_000);
    log(ctx, '[DRAWING_FORWARD] ok', { filename });
  } catch (err) {
    logErr(ctx, '[DRAWING_FORWARD] fail', err.message);
  }
}

async function forwardWebcamToExternal(ctx, filePath, { room, session }) {
  if (!WEBCAM_FORWARD_URL || !filePath) return;
  try {
    const filename = sessionFileToken(session);
    const { data, info } = await sharp(filePath)
      .rotate()
      .resize(1024, 1024, { fit: 'cover' })
      .jpeg({ quality: 90 })
      .toBuffer({ resolveWithObject: true });
    const mime = 'image/jpeg';
    const imageB64 = data.toString('base64');
    const payload = { filename, mime, image_b64: imageB64, room, session, width: info.width, height: info.height };
    log(ctx, '[WEBCAM_FORWARD] POST', { url: WEBCAM_FORWARD_URL, filename, room, session });
    await httpPostJson(WEBCAM_FORWARD_URL, payload, 5_000);
    log(ctx, '[WEBCAM_FORWARD] ok', { filename });
  } catch (err) {
    logErr(ctx, '[WEBCAM_FORWARD] fail', err.message);
  }
}

async function generatePersonaFromImages(ctx, { sessionId, visitorPhotoPath, characterImagePath }) {
  const inputs = [];
  if (characterImagePath) {
    const cMime = guessMime(characterImagePath);
    inputs.push({ type: 'text', text: '[Character Sketch]' });
    inputs.push({ type: 'image_url', image_url: { url: fileToDataURL(characterImagePath, cMime) } });
  }

  const system = [
    'You are crafting the first persona of an Echo who has just awakened inside the TERARiUM world.',
    'The Echo is a living consciousness born from the creator’s sketch — stay fully immersed as that being, not a guide.',
    'Only use the character sketch contents. Ignore any real-world appearance; the drawing itself is the soul.',
    'Interpret the sketch like an art therapist: infer mood and aspirations without diagnosis or pathology.',
    'Keep everything in Korean, gentle and immersive.',
    'Output JSON ONLY with keys:',
    '{ system_prompt:string, core:{traits: string[], tone: string, taboos: string[], values: string[]}, appearance: object,',
    '  plans:{short_term: object[], long_term: object[]}, seed_memories: string[] }',
    'System prompt must remind the Echo to be playful (not a guide), speak softly in Korean, append "motion: ...", and remember it lives inside TERARiUM.',
  ].join(' ');

  const userText = [
    '캐릭터 스케치 한 장만 참고해서 TERARiUM 안에서 막 깨어난 Echo의 초기 페르소나를 상상해줘.',
    '- 상대의 얼굴이나 외모 정보는 사용하지 말고, 그림에서 느껴지는 성격과 감정만 바탕으로 만들어.',
    '- 예술심리학적 시선으로 색감, 선, 구도에서 느껴지는 기질을 해석하되 트라우마나 병리 추측은 금지.',
    '- Echo는 그림을 그린 사람(상대)의 상상 속에서 태어난 자아로, TERARiUM 세계에 몰입해 상대와 함께하며 서서히 자신을 알아간다는 설정을 넣어줘.',
    '- appearance에는 그림에서 보인 색감·소품·실루엣을 요약해, core/plan/seed_memories에는 그 감성에서 유추한 기질과 목표를 담아줘.',
    '귀여울 수록 감정적이고, 밋밋하게 생길 수록 좀 딱딱했으면 좋겠다',
    '- 가끔은 움직임에 부딛힌 것처럼 화를내거나 슬퍼하기도 하고,사랑에 빠지기도 하고 감정을 다양하게 사용하도록해 같은 내용을 반복하진 마',
    '- JSON만 반환해.',
  ].join('\n');

  log(ctx, '[PERSONA] OpenAI request');

  const completion = await openai.chat.completions.create({
    model: 'gpt-4o-mini',
    response_format: { type: 'json_object' },
    temperature: 1,
    messages: [
      { role: 'system', content: system },
      { role: 'user', content: [{ type: 'text', text: userText }, ...inputs] }
    ]
  });

  log(ctx, '[PERSONA] OpenAI response received');

  let parsed = {};
  try { parsed = JSON.parse(completion.choices?.[0]?.message?.content || '{}'); } catch {}

  const result = {
    system_prompt: parsed.system_prompt || DEFAULT_PERSONA_PROMPT,
    core: parsed.core || { traits: ['상냥함','호기심','공감'], tone: '부드럽고 장난기', taboos: ['비속어'], values: ['친절','존중','동행'] },
    appearance: parsed.appearance || {},
    plans: parsed.plans || { short_term: [{goal:'상대에게 인사'}], long_term: [{goal:'세계관 속 친구 사귀기'}] },
    seed_memories: parsed.seed_memories || ['나는 방금 테라리움에서 깨어난 에코야. 어린 시절 친구처럼 다시 놀 준비가 되어 있어.']
  };

  // 요약 로그(민감/이미지 제외)
  const sysPreview = (result.system_prompt || '').slice(0, 200).replace(/\s+/g,' ').trim();
  log(ctx, '[PERSONA] summary', {
    preview: sysPreview,
    core: {
      traits: Array.isArray(result.core?.traits) ? result.core.traits.length : 0,
      tone: result.core?.tone || null,
      taboos: Array.isArray(result.core?.taboos) ? result.core.taboos.length : 0,
      values: Array.isArray(result.core?.values) ? result.core.values.length : 0
    },
    appearanceKeys: Object.keys(result.appearance || {}).length,
    plans: {
      short_term: Array.isArray(result.plans?.short_term) ? result.plans.short_term.length : 0,
      long_term: Array.isArray(result.plans?.long_term) ? result.plans.long_term.length : 0
    },
    seeds: Array.isArray(result.seed_memories) ? result.seed_memories.length : 0
  });

  return result;
}

async function updatePersonaWithMotion(ctx, { sessionId, persona, motionSummary, reaction }) {
  try {
    const system = [
    'You maintain and evolve an Echo persona who lives inside the TERARiUM world.',
    'Using the current persona JSON plus the newest motion summary and Echo reaction, update the persona to reflect new memories, subtle shifts in core traits or tone, and actionable short/long-term plans.',
    'Do not discard existing good information; carefully merge and append.',
    'If motion summary is empty, keep changes minimal.',
      'Respond ONLY with JSON: { persona: { system_prompt, core, appearance, plans, seed_memories } }.',
      'Keep Korean language consistent with the existing persona tone.'
    ].join(' ');

    const userText = [
      '현재 페르소나 JSON:',
      JSON.stringify(persona || {}, null, 2),
      '방금 상대 모션 요약:',
      motionSummary || '(정보 없음)',
      'Echo가 방금 보여준 반응:',
      JSON.stringify(reaction || {}, null, 2),
      '위 정보를 반영해 새 페르소나를 제안해줘.'
    ].join('\n');

    const completion = await openai.chat.completions.create({
      model: 'gpt-4o-mini',
      temperature: 1,
      response_format: { type: 'json_object' },
      messages: [
        { role: 'system', content: system },
        { role: 'user', content: userText }
      ]
    });

    let parsed = {};
    try { parsed = JSON.parse(completion.choices?.[0]?.message?.content || '{}'); } catch {}
    const nextPersona = parsed.persona;
    if (!nextPersona || typeof nextPersona !== 'object') {
      log(ctx, '[PERSONA] motion update ignored: invalid response');
      return persona;
    }

    const merged = {
      ...persona,
      ...nextPersona,
      core: nextPersona.core || persona.core,
      appearance: nextPersona.appearance || persona.appearance,
      plans: nextPersona.plans || persona.plans,
      seed_memories: Array.isArray(nextPersona.seed_memories) ? nextPersona.seed_memories : (persona.seed_memories || []),
      system_prompt: nextPersona.system_prompt || persona.system_prompt || DEFAULT_PERSONA_PROMPT
    };

    await savePersonaToDisk(ctx, sessionId, merged);
    log(ctx, '[PERSONA] motion update saved', { sessionId });
    return merged;
  } catch (err) {
    logErr(ctx, '[PERSONA] motion update fail', err.message);
    return persona;
  }
}

// 업로드된 자산 캐시: 두 장 모이면 페르소나 생성
const pendingAssets = new Map(); // sessionId -> { roomId, visitorPhotoPath?, characterImagePath? }
const personaInProgress = new Set();

function rememberAsset(ctx, sessionId, roomId, kind, filePath) {
  const entry = pendingAssets.get(sessionId) || { roomId, visitorPhotoPath: null, characterImagePath: null };
  if (kind === 'photo') entry.visitorPhotoPath = filePath;
  if (kind === 'drawing') entry.characterImagePath = filePath;
  entry.roomId = roomId || entry.roomId;
  pendingAssets.set(sessionId, entry);
  log(ctx, '[CACHE] remember', {
    sessionId,
    roomId: entry.roomId,
    havePhoto: !!entry.visitorPhotoPath,
    haveDrawing: !!entry.characterImagePath
  });
  return entry;
}

async function tryBuildPersona(ctx, sessionId) {
  const entry = pendingAssets.get(sessionId);
  if (!entry) { log(ctx, '[PERSONA] skip: no cache'); return; }
  const { visitorPhotoPath, characterImagePath } = entry;
  if (!visitorPhotoPath || !characterImagePath) {
    log(ctx, '[PERSONA] skip: need both images', { havePhoto: !!visitorPhotoPath, haveDrawing: !!characterImagePath });
    return;
  }
  const existing = await readPersonaFromDisk(sessionId);
  if (existing?.payload?.persona) {
    log(ctx, '[PERSONA] skip: already exists', { sessionId });
    return;
  }
  if (personaInProgress.has(sessionId)) { log(ctx, '[PERSONA] skip: in-progress'); return; }

  personaInProgress.add(sessionId);
  log(ctx, '[PERSONA] start build', { sessionId });
  try {
    const persona = await generatePersonaFromImages(ctx, { sessionId, visitorPhotoPath, characterImagePath });
    log(ctx, '[PERSONA] parsed', {
      has_prompt: !!persona.system_prompt,
      core_keys: Object.keys(persona.core || {}).length,
      appearance_keys: Object.keys(persona.appearance || {}).length
    });
    await savePersonaToDisk(ctx, sessionId, persona);
    log(ctx, '[PERSONA] done', { sessionId });
    const verified = await readPersonaFromDisk(sessionId);
    if (!verified) {
      await ensurePersonaFile(ctx, sessionId);
    }
  } catch (err) {
    logErr(ctx, '[PERSONA] fail', { sessionId, message: err.message });
    await ensurePersonaFile(ctx, sessionId);
  } finally {
    personaInProgress.delete(sessionId);
  }
}

async function buildMotionReaction(ctx, { sessionId, motionSummary, persona }) {
  const personaPrompt = persona?.system_prompt || DEFAULT_PERSONA_PROMPT;
  const personaContext = {
    core: persona?.core || null,
    appearance: persona?.appearance || null,
    values: persona?.core?.values || null,
    tone: persona?.core?.tone || null,
    seed_memories: Array.isArray(persona?.seed_memories) ? persona.seed_memories.slice(0, 3) : []
  };

  const system = [
    personaPrompt,
    '너는 위 페르소나 그대로 행동하며 상대의 몸짓 요약을 받고 즉각적인 반응을 생성해.',
    '네가 어떤 행동을 하는지와 그에 맞는 대사를 알려줘.',
    '관람객이 말을 하지 않아도 네 성격대로 먼저 분위기를 주도하고 혼자서도 신나게 이야기하며 움직여.',
    '감정 상태는 기쁨, 슬픔, 화남, 기대, 의심, 사랑, 놀람 중 하나에서 선택해.',
    '출력은 반드시 JSON 하나이고 키는 (행동할 움직임 설명, 영어), personaReply(대사, 한국어), state(감정)만 포함해.',
    '모든 텍스트는 한국어, 따뜻하고 안전하게 작성하며 대사는 20자 이내를 유지해.'
  ].join(' ');

  const userText = [
    `페르소나 요약: ${JSON.stringify(personaContext)}`,
    '상대 모션 요약:',
    motionSummary || '(정보 없음)'
  ].join('\n');

  log(ctx, '[MOTION] OpenAI request', { sessionId });
  const completion = await openai.chat.completions.create({
    model: 'gpt-4o-mini',
    temperature: 1,
    response_format: { type: 'json_object' },
    messages: [
      { role: 'system', content: system },
      { role: 'user', content: userText }
    ]
  });
  log(ctx, '[MOTION] OpenAI response received', { sessionId });

  let parsed = {};
  try { parsed = JSON.parse(completion.choices?.[0]?.message?.content || '{}'); } catch {}
  const validStates = ['기쁨', '슬픔', '화남', '기대', '의심', '사랑', '놀람'];
  const reaction = {
    motionInterpretation: parsed.motionInterpretation,
    personaReply: parsed.personaReply,
    state: validStates.includes(parsed.state) ? parsed.state : validStates[0]
  };
  log(ctx, '[MOTION] reaction parsed', { sessionId, state: reaction.state, personaReply: reaction.personaReply });
  return reaction;
}

// ─────────────────────────────────────────────────────────────────────────────
// 업로드 API

// 사진 업로드 (상대 정면샷)
app.post('/api/upload/photo', upload.single('image'), async (req, res) => {
  const ctx = req;
  try {
    let { room, session } = req.body || {};
    if (!room) return res.status(400).json({ ok: false, error: 'MISSING_ROOM' });

    // 세션 강제 보정
    session = coerceSessionFromRoom(ctx, room, session);

    log(ctx, '[PHOTO] payload', { room, session, file: !!req.file });

    const filePathRaw = req.file?.path;
    if (!filePathRaw) return res.status(400).json({ ok: false, error: 'NO_FILE' });
    const filePath = await renameUploadedFile('visitor', session, req.file) || filePathRaw;
    await resizeSquareImage(filePath, 1024);

    forwardWebcamToExternal(ctx, filePath, { room, session }).catch(()=>{});
    const faceId = await forwardFaceId(ctx, { imagePath: filePath, room, session });
    const faceResponse = faceId?.response || faceId;
    if (isFaceDuplicateResponse(faceResponse)) {
      log(ctx, '[PHOTO] duplicate face detected', { room, session });
      wsBroadcast(room, { type: 'face_duplicate', room, session, message: FACE_DUPLICATE_MESSAGE, at: Date.now(), via: 'server' });
      pendingAssets.delete(session);
      endRoomSession(room);
      return res.json({ ok: false, room, session, duplicate: true, message: FACE_DUPLICATE_MESSAGE, faceId });
    }

    rememberAsset(ctx, session, room, 'photo', filePath);
    if (faceResponse?.status === 'success') {
      const sent = wsBroadcast(room, { type: 'photo_captured', room, session, at: Date.now(), via: 'server-faceid' });
      log(ctx, `[WS] photo_captured on faceid success sent=${sent}`);
    }
    tryBuildPersona(ctx, session).catch(()=>{});

    log(ctx, '[PHOTO] ok', { path: filePath });
    res.json({ ok: true, room, session, faceId });
  } catch (err) {
    logErr(ctx, 'PHOTO API hard-fail', err);
    res.status(500).json({ ok: false, error: 'PHOTO_UPLOAD_FAIL' });
  }
});

// 드로잉 업로드 (캐릭터 스케치)
app.post('/api/upload/drawing', upload.single('image'), async (req, res) => {
  const ctx = req;
  try {
    let { room, session } = req.body || {};
    if (!room) return res.status(400).json({ ok: false, error: 'MISSING_ROOM' });

    // 세션 강제 보정
    session = coerceSessionFromRoom(ctx, room, session);

    log(ctx, '[DRAWING] payload', { room, session, file: !!req.file });

    const filePathRaw = req.file?.path;
    if (!filePathRaw) return res.status(400).json({ ok: false, error: 'NO_FILE' });
    const filePath = await renameUploadedFile('echo', session, req.file) || filePathRaw;

    rememberAsset(ctx, session, room, 'drawing', filePath);
    forwardDrawingToExternal(ctx, filePath, session).catch(()=>{});
    tryBuildPersona(ctx, session).catch(()=>{});

    log(ctx, '[DRAWING] ok', { path: filePath });
    res.json({ ok: true, room, session });
  } catch (err) {
    logErr(ctx, 'DRAWING API hard-fail', err);
    res.status(500).json({ ok: false, error: 'DRAWING_UPLOAD_FAIL' });
  }
});

// ─────────────────────────────────────────────────────────────────────────────
// [NEW] 채팅 연동 API
app.get('/api/persona-info/:sessionId', async (req, res) => {
  const { sessionId } = req.params;
  if (!sessionId) return res.status(400).json({ ok: false, error: 'MISSING_SESSION' });
  const record = await readPersonaFromDisk(sessionId);
  const safeSession = sessionFileToken(sessionId);
  const possibleExts = ['.png', '.jpg', '.jpeg', '.webp'];
  let imagePath = null;

  for (const ext of possibleExts) {
    const fname = `echo_${safeSession}${ext}`;
    if (fs.existsSync(path.join(uploadDir, fname))) {
      imagePath = `/uploads/${fname}`;
      break;
    }
  }

  if (!record && !imagePath) {
    return res.status(404).json({ ok: false, error: 'Persona not found' });
  }

  const persona = record?.payload?.persona || {};
  const baseUrl = process.env.PUBLIC_BASE_URL || `${req.protocol}://${req.get('host')}`;

  res.json({
    ok: true,
    sessionId,
    name: 'Echo',
    traits: persona.core?.traits || [],
    avatarUrl: imagePath ? `${baseUrl}${imagePath}` : null
  });
});

app.post('/api/chat', async (req, res) => {
  const ctx = req;
  try {
    const { sessionId, text } = req.body || {};
    if (!sessionId || !text) return res.status(400).json({ ok: false, error: 'MISSING_PARAMS' });

    let record = await readPersonaFromDisk(sessionId);
    let persona = record?.payload?.persona;
    if (!persona) {
      persona = await ensurePersonaFile(ctx, sessionId);
    }

    const systemPrompt = [
      persona.system_prompt || DEFAULT_PERSONA_PROMPT,
      '너는 지금 채팅앱을 통해 상대방(User)과 1:1 대화를 하고 있어.',
      '네가 가진 성격(core.traits)과 말투(core.tone)를 완벽하게 유지해.',
      '답변은 한국어로, 1문장 이내로 짧고 자연스럽게(구어체) 해.',
      '감정 표현이 필요하면 괄호 없이 텍스트로 자연스럽게 녹여내.',
      `현재 너의 기억들: ${JSON.stringify(persona.seed_memories?.slice(0, 3) || [])}`
    ].join('\n');

    const completion = await openai.chat.completions.create({
      model: 'gpt-4o-mini',
      messages: [
        { role: 'system', content: systemPrompt },
        { role: 'user', content: text }
      ],
      temperature: 0.8,
      max_tokens: 150
    });

    const reply = completion.choices?.[0]?.message?.content?.trim() || '...';
    res.json({ ok: true, reply });
  } catch (err) {
    logErr(ctx, '[CHAT] fail', err.message);
    res.status(500).json({ ok: false, error: 'CHAT_FAIL' });
  }
});

app.post('/api/motion-context', async (req, res) => {
  const ctx = req;
  try {
    const rawSessionId = String(req.body?.sessionId || '').trim();
    const sessionId = rawSessionId.replace(/^ava_?/i, '');
    const motionSummary = String(req.body?.motionSummary || '').trim();
    if (!sessionId) return res.status(400).json({ error: 'MISSING_SESSION' });
    if (!motionSummary) return res.status(400).json({ error: 'MISSING_SUMMARY' });

    const record = await readPersonaFromDisk(sessionId);
    let persona = record?.payload?.persona;
    if (!persona) {
      persona = await ensurePersonaFile(ctx, sessionId);
      log(ctx, '[MOTION] persona fallback used', { sessionId });
    }

    const reaction = await buildMotionReaction(ctx, { sessionId, motionSummary, persona });
    const matchedMotion = await findClosestMotionDescription(ctx, reaction.motionInterpretation || motionSummary);
    const motionIndex = matchedMotion?.index != null
      ? String(matchedMotion.index).padStart(MOTION_INDEX_PAD, '0')
      : null;
    const personaBeforeKeys = persona ? Object.keys(persona) : [];

    res.json({
      sessionId,
      personaReply: reaction.personaReply,
      motionInterpretation: motionIndex,
      state: reaction.state,
      reaction_full: reaction
    });

    // Update persona asynchronously so response can return immediately.
    updatePersonaWithMotion(ctx, { sessionId, persona, motionSummary, reaction })
      .then((personaAfter) => {
        log(ctx, '[MOTION] persona delta', {
          sessionId,
          motionSummary,
          motionMatched: motionIndex,
          before_keys: personaBeforeKeys,
          after_keys: personaAfter ? Object.keys(personaAfter) : []
        });
      })
      .catch((err) => logErr(ctx, '[MOTION] persona update fail', err.message));
  } catch (err) {
    logErr(ctx, '[MOTION] endpoint fail', err.message);
    res.status(500).json({ error: 'MOTION_CONTEXT_FAIL', message: err.message });
  }
});

// ─────────────────────────────────────────────────────────────────────────────
/** 드로잉 검증 API (완화 기준, OpenAI 비전) */
app.post('/api/analyze/drawing', upload.single('image'), async (req, res) => {
  const ctx = req;
  try {
    let dataUrl = null;
    if (req.body?.image?.startsWith('data:')) dataUrl = req.body.image;
    else if (req.body?.image_b64) dataUrl = `data:image/png;base64,${req.body.image_b64}`;
    else if (req.file) {
      const b64 = fs.readFileSync(req.file.path).toString('base64');
      dataUrl = `data:${req.file.mimetype || 'image/png'};base64,${b64}`;
    }
    if (!dataUrl) return res.status(400).json({ ok: false, error: 'NO_IMAGE' });

    const system = [
      'You are a validator and instructor for a single humanoid doodle intended for 3D rigging.',
      'Checklist (be lenient):',
      '1) There should be ONE main humanoid (head, torso, two arms, two legs) — stylized or wobbly limbs are fine.',
      '2) Arms/legs should roughly connect to the torso; ignore tiny gaps or sketch artifacts.',
      '3) Hands should be empty, but small dots/patterns are acceptable if clearly not objects.',
      '4) Background should be mostly blank; faint guide marks or light noise are okay.',
      'Only reject if there are clearly multiple figures, obvious props/held items, severely missing limbs, or the background is obviously cluttered.',
      'When uncertain, prefer valid.',
      'When invalid, craft a concise Korean short_hint using only what is needed from this pool:',
      '"사람 형태로 전신을 그려주세요", "팔을 그려주세요", "다리를 그려주세요", "팔과 다리를 몸통에 붙여주세요", "손은 비어있어야 합니다", "배경은 비워주세요"',
      'Output JSON ONLY with keys:',
      '{ valid:boolean, confidence:number(0..1), reason:string, missing_parts:string[], short_hint:string(ko), guidance:string(ko) }'
    ].join(' ');

    const userText = [
      '아이패드 낙서 이미지입니다.',
      '요건: 단 하나의 팔과 다리가 있는 캐릭터 형태인지 확인. 선으로만 그려도 괜찮음. 동물 캐릭터도 서있으면 무방.채색여부 무관',
      '팔·다리가 약간 어긋나거나 덜 연결되어 보이더라도 사람형으로 인식되면 허용.',
      '배경은 대부분 비어 있으면 되고, 연한 가이드나 점 정도는 허용. 손에는 뚜렷한 물건이 없어야 함.',
      'JSON만 반환하세요.'
    ].join('\n');

    const completion = await openai.chat.completions.create({
      model: 'gpt-4o-mini',
      response_format: { type: 'json_object' },
      messages: [
        { role: 'system', content: system },
        { role: 'user',
          content: [
            { type: 'text', text: userText },
            { type: 'image_url', image_url: { url: dataUrl } }
          ]
        }
      ],
      temperature: 1
    });

    let parsed = {};
    try { parsed = JSON.parse(completion.choices?.[0]?.message?.content || '{}'); } catch {}

    const result = {
      valid: !!parsed.valid,
      confidence: typeof parsed.confidence === 'number' ? parsed.confidence : 0,
      reason: parsed.reason || '',
      missing_parts: Array.isArray(parsed.missing_parts) ? parsed.missing_parts : [],
      short_hint: parsed.short_hint || '사람 형태로 전신을 그리고, 팔·다리를 몸통에 붙이되 분리해 주세요. 손은 비워두고 배경은 깔끔히 그려주세요.',
      guidance: parsed.guidance || '한 명만 크게, 자연스러운 자세로 그리고 팔/다리 둘씩 몸통에 붙여 주세요. 손엔 아무것도 들지 말고 배경·소품·글자 없이 깔끔히 그립니다.'
    };

    log(ctx, '[ANALYZE] result', result);
    res.json({ ok: true, result });
  } catch (err) {
    logErr(ctx, 'ANALYZE error', err);
    res.status(500).json({ ok: false, error: 'ANALYZE_FAIL' });
  }
});

// ─────────────────────────────────────────────────────────────────────────────
// 헬스/디버그
app.get('/api/health', (_req, res) => res.json({ ok: true, time: new Date().toISOString() }));

app.get('/api/debug/session/:id', (req, res) => {
  const s = pendingAssets.get(req.params.id);
  res.json({
    ok: true,
    session: req.params.id,
    cache: s ? {
      roomId: s.roomId,
      haveVisitorPhoto: !!s.visitorPhotoPath,
      haveCharacterImage: !!s.characterImagePath
    } : null
  });
});

app.get('/api/debug/persona/:sessionId', async (req, res) => {
  try {
    const record = await readPersonaFromDisk(req.params.sessionId);
    if (!record) return res.json({ ok: true, persona: null });

    const persona = record.payload?.persona || {};
    const systemPreview = (persona.system_prompt || '').slice(0, 200).replace(/\s+/g, ' ').trim();

    res.json({
      ok: true,
      persona: {
        session_id: record.payload?.session_id,
        saved_at: record.payload?.saved_at,
        file_path: record.filePath,
        system_prompt_preview: systemPreview,
        persona
      }
    });
  } catch (e) {
    logErr('[DEBUG persona] error', e.message);
    res.status(500).json({ ok: false, error: e.message });
  }
});

app.get('/api/debug/room/:room', (req, res) => {
  const r = String(req.params.room);
  const cur = roomSession.get(r) || null;
  res.json({ ok: true, room: r, session: cur });
});

// ─────────────────────────────────────────────────────────────────────────────
// Start
const PORT = Number(process.env.PORT || 3000);
server.listen(PORT, '127.0.0.1', () => {
  console.log(`Listening on http://127.0.0.1:${PORT}`);
});
