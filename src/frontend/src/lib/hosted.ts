import { createHmac, randomBytes, scryptSync, timingSafeEqual } from "node:crypto";
import { sql } from "@/lib/db";

const maxUpload = 8 * 1024 * 1024;

type UserRow = { id: string; email: string; display_name: string; password_hash: string };

export async function handleStudio(request: Request, path: string[]): Promise<Response> {
  try {
    const route = path.join("/");
    const url = new URL(request.url);
    const ip = clientAddress(request);
    if (request.method === "POST" && route === "auth/register") {
      await limit(`register:${ip}`, 5, 600);
      return await register(await request.json());
    }
    if (request.method === "POST" && route === "auth/login") {
      await limit(`login:${ip}`, 10, 600);
      return await login(await request.json());
    }
    if (request.method === "GET" && route === "system") return Response.json(systemInfo());
    if (request.method === "GET" && path[0] === "media" && path[1]) return await media(path[1], url);

    const user = await userFrom(request);
    if (!user) return fail(401, "unauthorized", "Sign in again.");
    if (request.method === "GET" && route === "brand-kits") return Response.json(await kits(user.id));
    if (request.method === "GET" && route === "projects") return Response.json(await listProjects(user.id, url.searchParams.get("q") || ""));
    if (request.method === "POST" && route === "projects") return Response.json(await createProject(user.id, await request.json()));

    const projectId = path[1];
    if (path[0] === "projects" && projectId) {
      if (request.method === "GET" && path.length === 2) return Response.json(await projectDetail(user.id, projectId));
      if (request.method === "POST" && path[2] === "assets") return Response.json(await saveAsset(user.id, projectId, request));
      if (request.method === "POST" && path[2] === "generate") return Response.json(await markGenerate(user.id, projectId));
      if (request.method === "POST" && path[2] === "corrections") return Response.json(await noteCorrection(user.id, projectId, await request.json()));
      if (request.method === "POST" && path[2] === "export") return Response.json(await projectDetail(user.id, projectId));
      if (request.method === "POST" && path[2] === "revisions") return Response.json(await projectDetail(user.id, projectId));
      if (request.method === "GET" && path[2] === "events") return Response.json([]);
    }
    if (request.method === "POST" && path[0] === "jobs" && path[2] === "cancel") return Response.json({ cancelled: true });
    return fail(404, "not_found", "That studio route is not available here.");
  } catch (error) {
    const known = error as { status?: number; code?: string; message?: string };
    if (known.status && known.code) return fail(known.status, known.code, known.message || "Request failed.");
    console.error("hosted studio", error instanceof Error ? error.message : "failed");
    return fail(500, "studio_error", "The studio could not finish that request.");
  }
}

function systemInfo() {
  return {
    provider: "hosted",
    model: "",
    providerConfigured: false,
    tools: false,
    imageGenerationConfigured: false,
    transcriptionProvider: "unavailable",
    storageProvider: "postgres",
    engineConfigured: false,
    engineVersion: null,
    notes: ["Accounts and projects are stored with this hosted studio. Video rendering runs on the self-hosted API and worker."],
  };
}

async function register(body: { email?: string; password?: string; displayName?: string }) {
  const email = normalize(body.email);
  const password = body.password || "";
  const name = (body.displayName || email).trim();
  if (!email.includes("@") || password.length < 8) throw studio(400, "invalid_account", "Use an email and a password of at least 8 characters.");
  const existing = await sql<UserRow>("select id from users where email = $1", [email]);
  if (existing.length) throw studio(409, "email_taken", "An account with that email already exists.");
  const id = crypto.randomUUID();
  await sql(
    "insert into users (id, email, display_name, password_hash) values ($1, $2, $3, $4)",
    [id, email, name || email, hashPassword(password)],
  );
  return Response.json(await session(id, email, name || email));
}

async function login(body: { email?: string; password?: string }) {
  const email = normalize(body.email);
  const rows = await sql<UserRow>("select id, email, display_name, password_hash from users where email = $1", [email]);
  const user = rows[0];
  if (!user || !verifyPassword(body.password || "", user.password_hash)) throw studio(401, "invalid_login", "Email or password is wrong.");
  return Response.json(await session(user.id, user.email, user.display_name));
}

async function kits(userId: string) {
  return sql(
    `select id, name, theme_key as "themeKey", definition_json as "definitionJson", is_system as "isSystem"
     from brand_kits where is_system or owner_id = $1 order by is_system desc, name`,
    [userId],
  );
}

async function listProjects(userId: string, query: string) {
  return sql(
    `select id, title, status, status_detail as "statusDetail", platform, language, updated_at as "updatedAt", revision
     from projects
     where owner_id = $1 and ($2 = '' or title ilike $3 or subject ilike $3)
     order by updated_at desc`,
    [userId, query, `%${query}%`],
  );
}

async function createProject(userId: string, body: Record<string, unknown>) {
  const title = String(body.title || "").trim();
  const subject = String(body.subject || "").trim();
  const goal = String(body.goal || "").trim();
  if (title.length < 2 || subject.length < 2 || goal.length < 2) throw studio(400, "invalid_project", "Title, subject and goal are required.");
  const kitRows = await sql<{ id: string; definition_json: string }>(
    "select id, definition_json from brand_kits where id = $1 and (is_system or owner_id = $2)",
    [body.brandKitId || null, userId],
  );
  const kit = kitRows[0];
  const id = crypto.randomUUID();
  const now = new Date().toISOString();
  await sql(
    `insert into projects (
      id, owner_id, title, status, status_detail, brand_kit_id, theme_snapshot_json,
      subject, goal, audience, tone, language, platform, target_duration, call_to_action, updated_at
    ) values ($1,$2,$3,'Draft','Saved on the hosted studio.',$4,$5,$6,$7,$8,$9,$10,$11,$12,$13,$14)`,
    [
      id, userId, title, kit?.id ?? null, kit?.definition_json ?? "{}",
      subject, goal, String(body.audience || ""), String(body.tone || "cinematic"),
      String(body.language || "en"), String(body.platform || "reel"),
      Number(body.targetDurationSeconds || 15), body.callToAction ? String(body.callToAction) : null, now,
    ],
  );
  return projectDetail(userId, id);
}

async function saveAsset(userId: string, projectId: string, request: Request) {
  await limit(`upload:${userId}`, 6, 600);
  await owned(userId, projectId);
  const form = await request.formData();
  const file = form.get("file");
  if (!(file instanceof File)) throw studio(400, "invalid_asset", "Choose a file to upload.");
  if (file.size > maxUpload) throw studio(400, "invalid_asset", "Hosted uploads are limited to 8 MB.");
  const bytes = Buffer.from(await file.arrayBuffer());
  const detected = classify(bytes);
  if (!detected) throw studio(400, "invalid_asset", "Upload a video, image, audio file, or plain-text caption. HTML and SVG are not accepted.");
  const id = crypto.randomUUID();
  const kind = String(form.get("kind") || "SourceVideo").slice(0, 40);
  await sql(
    "insert into assets (id, project_id, kind, file_name, content_type, size_bytes, bytes) values ($1,$2,$3,$4,$5,$6,$7)",
    [id, projectId, kind, `${id}.${detected.extension}`, detected.contentType, bytes.length, bytes],
  );
  return { id, kind, fileName: file.name, sizeBytes: bytes.length, contentType: detected.contentType };
}

async function markGenerate(userId: string, projectId: string) {
  await owned(userId, projectId);
  const videos = await sql("select id from assets where project_id = $1 and kind = 'SourceVideo'", [projectId]);
  if (!videos.length) throw studio(400, "missing_video", "Add at least one raw video.");
  const message = "The project is saved. This public host does not run FFmpeg or the video engine; render it with the self-hosted API and worker.";
  await sql(
    "update projects set status = 'Failed', status_detail = $2, updated_at = now(), concurrency_token = concurrency_token + 1 where id = $1",
    [projectId, message],
  );
  await sql(
    "insert into jobs (id, project_id, kind, status, stage, message) values ($1,$2,'Generate','Failed','engine',$3)",
    [crypto.randomUUID(), projectId, message],
  );
  return projectDetail(userId, projectId);
}

async function noteCorrection(userId: string, projectId: string, body: { instruction?: string; selection?: unknown; baseRevision?: number }) {
  await owned(userId, projectId);
  const instruction = String(body.instruction || "").trim();
  if (instruction.length < 3) throw studio(400, "invalid_correction", "Describe the change you want.");
  await sql(
    "insert into messages (id, project_id, role, content, selection_json, revision_number) values ($1,$2,'user',$3,$4,$5)",
    [crypto.randomUUID(), projectId, instruction, JSON.stringify(body.selection ?? {}), body.baseRevision ?? null],
  );
  await sql(
    "update projects set status_detail = $2, updated_at = now() where id = $1",
    [projectId, "The note is saved. Applying it to the picture requires the self-hosted video worker."],
  );
  return projectDetail(userId, projectId);
}

async function projectDetail(userId: string, projectId: string) {
  const rows = await sql<Record<string, unknown>>(
    `select p.*, k.name as kit_name, k.theme_key, k.definition_json as kit_json, k.is_system
     from projects p left join brand_kits k on k.id = p.brand_kit_id
     where p.id = $1 and p.owner_id = $2`,
    [projectId, userId],
  );
  const project = rows[0];
  if (!project) throw studio(404, "not_found", "That project is not on this account.");
  const secret = await jwtSecret();
  const assets = await sql<Record<string, unknown>>("select * from assets where project_id = $1 order by created_at", [projectId]);
  const revisions = await sql("select * from revisions where project_id = $1 order by number", [projectId]);
  const covers = await sql("select * from covers where project_id = $1", [projectId]);
  const messages = await sql("select * from messages where project_id = $1 order by created_at", [projectId]);
  const jobs = await sql("select * from jobs where project_id = $1 order by updated_at desc", [projectId]);
  return {
    id: project.id,
    title: project.title,
    status: project.status,
    statusDetail: project.status_detail,
    concurrencyToken: project.concurrency_token,
    currentRevisionNumber: project.revision,
    currentVideoAssetId: project.current_video_asset_id,
    selectedCoverId: project.selected_cover_id,
    brief: {
      subject: project.subject,
      goal: project.goal,
      audience: project.audience,
      tone: project.tone,
      language: project.language,
      platform: project.platform,
      targetDurationSeconds: project.target_duration,
      callToAction: project.call_to_action,
    },
    brand: project.brand_kit_id ? {
      id: project.brand_kit_id,
      name: project.kit_name,
      themeKey: project.theme_key,
      definitionJson: project.kit_json,
      isSystem: project.is_system,
    } : null,
    themeSnapshotJson: project.theme_snapshot_json,
    warnings: JSON.parse(String(project.warnings_json || "[]")),
    publishPackJson: project.publish_pack_json,
    assets: assets.map((asset) => ({
      id: asset.id,
      kind: asset.kind,
      fileName: asset.file_name,
      contentType: asset.content_type,
      sizeBytes: Number(asset.size_bytes),
      downloadUrl: signedPath(String(asset.id), secret),
      createdAt: asset.created_at,
    })),
    revisions,
    covers,
    messages: messages.map((message) => ({
      id: (message as { id: string }).id,
      role: (message as { role: string }).role,
      content: (message as { content: string }).content,
      selectionJson: (message as { selection_json: string | null }).selection_json,
      revisionNumber: (message as { revision_number: number | null }).revision_number,
      createdAt: (message as { created_at: string }).created_at,
    })),
    jobs: jobs.map((job) => ({
      id: (job as { id: string }).id,
      kind: (job as { kind: string }).kind,
      status: (job as { status: string }).status,
      stage: (job as { stage: string }).stage,
      message: (job as { message: string | null }).message,
      attempt: (job as { attempt: number }).attempt,
      cancelRequested: (job as { cancel_requested: boolean }).cancel_requested,
      updatedAt: (job as { updated_at: string }).updated_at,
    })),
    timeline: null,
    transcript: null,
  };
}

async function media(id: string, url: URL) {
  const secret = await jwtSecret();
  const exp = Number(url.searchParams.get("exp") || 0);
  const sig = url.searchParams.get("sig") || "";
  const expected = createHmac("sha256", secret).update(`${id}.${exp}`).digest("base64url");
  if (exp < Date.now() || sig.length !== expected.length || !timingSafeEqual(Buffer.from(sig), Buffer.from(expected))) {
    return fail(401, "unauthorized", "This download link has expired.");
  }
  const rows = await sql<{ bytes: Uint8Array | null; content_type: string; file_name: string }>(
    "select bytes, content_type, file_name from assets where id = $1",
    [id],
  );
  const asset = rows[0];
  if (!asset?.bytes) return fail(404, "not_found", "That file is not stored.");
  const bytes = Buffer.from(asset.bytes);
  const detected = classify(bytes);
  if (!detected) return fail(415, "unsupported_media", "This file cannot be downloaded.");
  return new Response(bytes, {
    headers: {
      "content-type": detected.contentType,
      "content-disposition": `attachment; filename="${id}.${detected.extension}"`,
      "x-content-type-options": "nosniff",
      "content-security-policy": "default-src 'none'; sandbox",
      "x-frame-options": "DENY",
      "cache-control": "private, max-age=60",
    },
  });
}

async function session(id: string, email: string, displayName: string) {
  const secret = await jwtSecret();
  const exp = Math.floor(Date.now() / 1000) + 60 * 60 * 24 * 7;
  const token = sign({ sub: id, exp }, secret);
  return { token, user: { id, email, displayName } };
}

async function userFrom(request: Request) {
  const header = request.headers.get("authorization") || "";
  const token = header.startsWith("Bearer ") ? header.slice(7) : "";
  if (!token) return null;
  const payload = verify(token, await jwtSecret());
  if (!payload) return null;
  const rows = await sql<UserRow>("select id, email, display_name, password_hash from users where id = $1", [payload.sub]);
  return rows[0] ?? null;
}

async function owned(userId: string, projectId: string) {
  const rows = await sql("select id from projects where id = $1 and owner_id = $2", [projectId, userId]);
  if (!rows.length) throw studio(404, "not_found", "That project is not on this account.");
}

async function limit(bucket: string, max: number, windowSeconds: number) {
  const rows = await sql<{ hits: number }>(
    `insert into rate_limits (bucket, hits, window_start) values ($1, 1, now())
     on conflict (bucket) do update set
       hits = case when rate_limits.window_start < now() - ($2::int * interval '1 second') then 1 else rate_limits.hits + 1 end,
       window_start = case when rate_limits.window_start < now() - ($2::int * interval '1 second') then now() else rate_limits.window_start end
     returning hits`,
    [bucket, windowSeconds],
  );
  if (Number(rows[0]?.hits ?? 0) > max) throw studio(429, "rate_limited", "Too many requests. Wait a few minutes and try again.");
}

function clientAddress(request: Request) {
  const forwarded = request.headers.get("x-forwarded-for")?.split(",")[0] ?? "";
  const raw = (forwarded || request.headers.get("x-real-ip") || "unknown").trim().slice(0, 80);
  return raw.replace(/[^0-9a-fA-F:.]/g, "") || "unknown";
}

function classify(bytes: Buffer): { contentType: string; extension: string } | null {
  if (bytes.length < 12) return null;
  if (bytes[0] === 0x89 && bytes[1] === 0x50 && bytes[2] === 0x4e && bytes[3] === 0x47) return { contentType: "image/png", extension: "png" };
  if (bytes[0] === 0xff && bytes[1] === 0xd8 && bytes[2] === 0xff) return { contentType: "image/jpeg", extension: "jpg" };
  if (bytes.toString("ascii", 0, 4) === "RIFF" && bytes.toString("ascii", 8, 12) === "WEBP") return { contentType: "image/webp", extension: "webp" };
  if (bytes.toString("ascii", 0, 4) === "RIFF" && bytes.toString("ascii", 8, 12) === "WAVE") return { contentType: "audio/wav", extension: "wav" };
  if (bytes.toString("ascii", 0, 4) === "ID3" || (bytes[0] === 0xff && (bytes[1] & 0xe0) === 0xe0)) return { contentType: "audio/mpeg", extension: "mp3" };
  if (bytes[0] === 0x1a && bytes[1] === 0x45 && bytes[2] === 0xdf && bytes[3] === 0xa3) return { contentType: "video/webm", extension: "webm" };
  if (bytes.toString("ascii", 4, 8) === "ftyp") return { contentType: "video/mp4", extension: "mp4" };
  const sample = bytes.subarray(0, Math.min(bytes.length, 4096)).toString("utf8");
  if (sample.includes("\0") || /[<>]/.test(sample)) return null;
  if (/^[\t\n\r\x20-\x7e\u00a0-\uffff]*$/.test(sample)) return { contentType: "text/plain; charset=utf-8", extension: "txt" };
  return null;
}

async function jwtSecret() {
  const configured = process.env.STUDIO_JWT_KEY?.trim();
  if (configured) return configured;
  const rows = await sql<{ value: string }>("select value from app_settings where key = $1", ["jwt"]);
  if (rows[0]) return rows[0].value;
  const value = randomBytes(32).toString("hex");
  await sql("insert into app_settings (key, value) values ($1, $2) on conflict (key) do nothing", ["jwt", value]);
  const stored = await sql<{ value: string }>("select value from app_settings where key = $1", ["jwt"]);
  return stored[0]?.value || value;
}

function signedPath(id: string, secret: string) {
  const exp = Date.now() + 60 * 60 * 1000;
  const sig = createHmac("sha256", secret).update(`${id}.${exp}`).digest("base64url");
  return `/api/v1/media/${id}?exp=${exp}&sig=${sig}`;
}

function sign(payload: { sub: string; exp: number }, secret: string) {
  const header = base64url(JSON.stringify({ alg: "HS256", typ: "JWT" }));
  const body = base64url(JSON.stringify(payload));
  const sig = createHmac("sha256", secret).update(`${header}.${body}`).digest("base64url");
  return `${header}.${body}.${sig}`;
}

function verify(token: string, secret: string) {
  const [header, body, sig] = token.split(".");
  if (!header || !body || !sig) return null;
  const expected = createHmac("sha256", secret).update(`${header}.${body}`).digest("base64url");
  if (sig.length !== expected.length || !timingSafeEqual(Buffer.from(sig), Buffer.from(expected))) return null;
  const payload = JSON.parse(Buffer.from(body, "base64url").toString()) as { sub?: string; exp?: number };
  if (!payload.sub || !payload.exp || payload.exp < Math.floor(Date.now() / 1000)) return null;
  return { sub: payload.sub };
}

function hashPassword(password: string) {
  const salt = randomBytes(16).toString("hex");
  const hash = scryptSync(password, salt, 32).toString("hex");
  return `scrypt$${salt}$${hash}`;
}

function verifyPassword(password: string, stored: string) {
  const [kind, salt, hash] = stored.split("$");
  if (kind !== "scrypt" || !salt || !hash) return false;
  const actual = scryptSync(password, salt, 32);
  const expected = Buffer.from(hash, "hex");
  return actual.length === expected.length && timingSafeEqual(actual, expected);
}

function base64url(value: string) {
  return Buffer.from(value).toString("base64url");
}

function normalize(email?: string) {
  return (email || "").trim().toLowerCase();
}

function fail(status: number, code: string, message: string) {
  return Response.json({ error: { code, message } }, { status });
}

function studio(status: number, code: string, message: string) {
  return Object.assign(new Error(message), { status, code });
}
