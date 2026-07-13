const MAX_TTL_SECONDS = 45;
const DEFAULT_TTL_SECONDS = 35;
const STALE_GRACE_SECONDS = 60;
const MAX_TITLE = 40;
const MAX_HOST = 24;
const MAX_REGION = 40;
const MAX_LANGUAGE = 16;
const MAX_MOD_VERSION = 24;
const MAX_CODE = 8;
const MAX_BODY_BYTES = 4096;
const PURGE_INTERVAL_SECONDS = 30;
const RATE_LIMIT_WINDOW_SECONDS = 60;
const MAX_MUTATIONS_PER_WINDOW = 60;
const MAX_TURN_CREDENTIALS_PER_WINDOW = 30;
const MAX_RATE_LIMIT_KEYS = 2048;
const GITHUB_LATEST_RELEASE_API =
	"https://api.github.com/repos/artriy/Perfect-Comms/releases/latest";
const GITHUB_RELEASES_URL =
	"https://github.com/artriy/Perfect-Comms/releases/latest";
const TURN_TTL_SECONDS = 3600;

let lastPurgeAt = 0;
const mutationWindows = new Map();
const turnCredentialWindows = new Map();

export default {
	async fetch(request, env) {
		try {
			const url = new URL(request.url);
			const now = nowSeconds();

			// TURN credentials are for the native client only. Keeping this route out
			// of the wildcard CORS surface prevents websites from minting credentials.
			if (url.pathname === "/turn-credentials") {
				if (request.method !== "POST") return turnMethodNotAllowed();
				return await turnCredentials(request, env, now);
			}

			if (request.method === "OPTIONS")
				return withCors(new Response(null, { status: 204 }));

			if (url.pathname === "/health" && request.method === "GET") {
				return json({ ok: true, service: "perfect-comms-lobbies" });
			}

			if (url.pathname === "/updates/latest" && request.method === "GET") {
				return await latestUpdateNotification(url, env);
			}

			if (isMutationRequest(request) && !allowMutation(request, now)) {
				return json({ error: "rate_limited" }, 429);
			}
			await maybePurgeExpired(env.DB, now);

			if (url.pathname === "/lobbies" && request.method === "GET") {
				return await listLobbies(env.DB);
			}

			if (url.pathname === "/lobbies" && request.method === "POST") {
				return await upsertLobby(request, env.DB);
			}

			const heartbeat = url.pathname.match(/^\/lobbies\/([^/]+)\/heartbeat$/);
			if (heartbeat && request.method === "POST") {
				const id = decodeLobbyPathId(heartbeat[1]);
				if (!id) return json({ error: "invalid_lobby_id" }, 400);
				return await heartbeatLobby(request, env.DB, id);
			}

			const lobby = url.pathname.match(/^\/lobbies\/([^/]+)$/);
			if (lobby && request.method === "DELETE") {
				const id = decodeLobbyPathId(lobby[1]);
				if (!id) return json({ error: "invalid_lobby_id" }, 400);
				return await deleteLobby(request, env.DB, id);
			}

			return json({ error: "not_found" }, 404);
		} catch {
			return json({ error: "server_error" }, 500);
		}
	},
};

async function latestUpdateNotification(url, env) {
	const release = await getLatestGitHubRelease(env);
	const latestVersion = String(release.tag_name || "1.0.0").trim();
	const releaseUrl = String(release.html_url || GITHUB_RELEASES_URL).trim();
	const currentVersion = (url.searchParams.get("current") || "0.0.0").trim();
	const updateAvailable = compareVersions(latestVersion, currentVersion) > 0;

	return json({
		enabled: updateAvailable,
		test: false,
		latestVersion,
		title: "Perfect Comms update available",
		message: "Click here to download the latest Perfect Comms release.",
		releaseUrl,
		showEveryMainMenu: false,
	});
}

async function turnCredentials(request, env, now) {
	const tokenId = env.TURN_TOKEN_ID;
	const apiToken = env.TURN_API_TOKEN;
	if (!tokenId || !apiToken)
		return turnJson({ error: "turn_not_configured" }, 503);
	if (!(await allowTurnCredentialRequest(request, env, now)))
		return turnJson({ error: "rate_limited" }, 429);

	try {
		const customIdentifier = await turnCustomIdentifier(
			request,
			apiToken,
			now,
		);
		const credentialFetch = env.TURN_CREDENTIAL_FETCH || fetch;
		const response = await credentialFetch(
			`https://rtc.live.cloudflare.com/v1/turn/keys/${tokenId}/credentials/generate-ice-servers`,
			{
				method: "POST",
				headers: {
					"content-type": "application/json",
					authorization: `Bearer ${apiToken}`,
				},
				body: JSON.stringify({
					ttl: TURN_TTL_SECONDS,
					customIdentifier,
				}),
			},
		);
		if (!response.ok)
			return turnJson({ error: "turn_generate_failed" }, 502);

		const payload = await response.json();
		if (!validIceServers(payload?.iceServers))
			return turnJson({ error: "turn_generate_failed" }, 502);
		return turnJson({
			iceServers: payload.iceServers,
			ttl: TURN_TTL_SECONDS,
		});
	} catch {
		return turnJson({ error: "turn_generate_failed" }, 502);
	}
}

async function allowTurnCredentialRequest(request, env, now) {
	const key = `turn:${clientKey(request)}`;
	if (env.TURN_RATE_LIMITER?.limit) {
		try {
			const result = await env.TURN_RATE_LIMITER.limit({ key });
			return result?.success === true;
		} catch {
			// A binding outage must not leave the credential issuer unbounded.
		}
	}
	return allowWindow(
		turnCredentialWindows,
		key,
		now,
		MAX_TURN_CREDENTIALS_PER_WINDOW,
	);
}

async function turnCustomIdentifier(request, apiToken, now) {
	const day = Math.floor(now / 86400);
	const message = new TextEncoder().encode(`${day}:${clientKey(request)}`);
	const key = await crypto.subtle.importKey(
		"raw",
		new TextEncoder().encode(apiToken),
		{ name: "HMAC", hash: "SHA-256" },
		false,
		["sign"],
	);
	const signature = await crypto.subtle.sign("HMAC", key, message);
	return [...new Uint8Array(signature)]
		.slice(0, 16)
		.map((byte) => byte.toString(16).padStart(2, "0"))
		.join("");
}

function validIceServers(value) {
	if (!Array.isArray(value) || value.length === 0) return false;
	let hasAuthenticatedRelay = false;
	const allValid = value.every((server) => {
		if (!server || typeof server !== "object" || Array.isArray(server))
			return false;
		const urls = Array.isArray(server.urls) ? server.urls : [server.urls];
		const validUrls =
			urls.length > 0 &&
			urls.every(
				(url) =>
					typeof url === "string" &&
					/^(stun|turn|turns):/i.test(url),
			);
		if (!validUrls) return false;

		const hasRelayUrl = urls.some(
			(url) => typeof url === "string" && /^turns?:/i.test(url),
		);
		if (hasRelayUrl) {
			if (
				typeof server.username !== "string" ||
				server.username.length === 0 ||
				typeof server.credential !== "string" ||
				server.credential.length === 0
			)
				return false;
			hasAuthenticatedRelay = true;
		}
		return true;
	});
	return allValid && hasAuthenticatedRelay;
}

function turnMethodNotAllowed() {
	const response = turnJson({ error: "method_not_allowed" }, 405);
	const headers = new Headers(response.headers);
	headers.set("allow", "POST");
	return new Response(response.body, {
		status: response.status,
		statusText: response.statusText,
		headers,
	});
}

async function getLatestGitHubRelease(env) {
	if (env.UPDATE_RELEASE_FIXTURE_JSON)
		return JSON.parse(env.UPDATE_RELEASE_FIXTURE_JSON);

	try {
		const response = await fetch(GITHUB_LATEST_RELEASE_API, {
			headers: {
				accept: "application/vnd.github+json",
				"user-agent": "PerfectCommsUpdateWorker",
			},
		});
		if (!response.ok) throw new Error("github_release_fetch_failed");
		return await response.json();
	} catch {
		return {
			tag_name:
				env.UPDATE_RELEASE_FALLBACK_VERSION ||
				env.UPDATE_LATEST_VERSION ||
				"1.0.0",
			html_url:
				env.UPDATE_RELEASE_FALLBACK_URL ||
				env.UPDATE_RELEASE_URL ||
				GITHUB_RELEASES_URL,
		};
	}
}

function compareVersions(left, right) {
	const leftParts = splitVersion(left);
	const rightParts = splitVersion(right);
	const count = Math.max(leftParts.length, rightParts.length, 1);
	for (let i = 0; i < count; i++) {
		const a = leftParts[i] || 0;
		const b = rightParts[i] || 0;
		if (a !== b) return a > b ? 1 : -1;
	}
	return 0;
}

function splitVersion(value) {
	value = String(value || "").trim();
	if (value[0]?.toLowerCase() === "v") value = value.slice(1);
	value = value.split(/[+-]/, 1)[0];
	return value
		.split(".")
		.filter(Boolean)
		.slice(0, 4)
		.map((part) => {
			const parsed = Number.parseInt(part, 10);
			return Number.isFinite(parsed) ? parsed : 0;
		});
}

async function listLobbies(db) {
	const now = nowSeconds();
	const result = await db
		.prepare(`
    SELECT id, code, region, language, title, host, players, maxPlayers, state,
           COALESCE(NULLIF(stateChangedAt, 0), updatedAt) AS stateChangedAt,
           modVersion, protocolVersion, updatedAt, expiresAt
    FROM lobbies
    WHERE expiresAt > ? AND updatedAt > ?
    ORDER BY state = 'Lobby' DESC, updatedAt DESC, players DESC
    LIMIT 100
  `)
		.bind(now, now - STALE_GRACE_SECONDS)
		.all();

	return json({ lobbies: result.results || [], now });
}

async function upsertLobby(request, db) {
	const body = await readJson(request);
	if (!body) return json({ error: "invalid_json" }, 400);
	const lobbyId = sanitizeLobbyId(body.id, crypto.randomUUID());
	if (!lobbyId) return json({ error: "invalid_lobby_id" }, 400);
	const lobby = sanitizeLobby(body, lobbyId);
	if (!lobby) return json({ error: "invalid_code" }, 400);
	const ownerToken = sanitizeToken(body.ownerToken);
	if (!ownerToken) return json({ error: "missing_owner_token" }, 400);

	const now = nowSeconds();
	const ttl = clampInt(
		body.ttlSeconds,
		30,
		MAX_TTL_SECONDS,
		DEFAULT_TTL_SECONDS,
	);
	const ownerTokenHash = await sha256(ownerToken);

	const existing = await db
		.prepare("SELECT ownerTokenHash FROM lobbies WHERE id = ?")
		.bind(lobby.id)
		.first();
	if (existing && existing.ownerTokenHash !== ownerTokenHash) {
		return json({ error: "owner_token_mismatch" }, 403);
	}

	const result = await db
		.prepare(`
    INSERT INTO lobbies
      (id, code, region, language, title, host, players, maxPlayers, state, stateChangedAt,
       modVersion, protocolVersion, ownerTokenHash, updatedAt, expiresAt)
    VALUES (?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?)
    ON CONFLICT(id) DO UPDATE SET
      code = excluded.code,
      region = excluded.region,
      language = excluded.language,
      title = excluded.title,
      host = excluded.host,
      players = excluded.players,
      maxPlayers = excluded.maxPlayers,
      stateChangedAt = CASE
        WHEN lobbies.state != excluded.state THEN excluded.updatedAt
        ELSE COALESCE(NULLIF(lobbies.stateChangedAt, 0), lobbies.updatedAt, excluded.updatedAt)
      END,
      state = excluded.state,
      modVersion = excluded.modVersion,
      protocolVersion = excluded.protocolVersion,
      ownerTokenHash = lobbies.ownerTokenHash,
      updatedAt = excluded.updatedAt,
      expiresAt = excluded.expiresAt
    WHERE lobbies.ownerTokenHash = excluded.ownerTokenHash
  `)
		.bind(
			lobby.id,
			lobby.code,
			lobby.region,
			lobby.language,
			lobby.title,
			lobby.host,
			lobby.players,
			lobby.maxPlayers,
			lobby.state,
			now,
			lobby.modVersion,
			lobby.protocolVersion,
			ownerTokenHash,
			now,
			now + ttl,
		)
		.run();

	if (result?.meta?.changes === 0)
		return json({ error: "owner_token_mismatch" }, 403);

	return json({ ok: true, id: lobby.id, expiresAt: now + ttl });
}

async function heartbeatLobby(request, db, id) {
	const body = await readJson(request);
	if (!body) return json({ error: "invalid_json" }, 400);
	const ownerToken = sanitizeToken(body.ownerToken);
	if (!ownerToken) return json({ error: "missing_owner_token" }, 400);

	const ownerTokenHash = await sha256(ownerToken);
	const current = await db
		.prepare(
			"SELECT ownerTokenHash, state, stateChangedAt, updatedAt FROM lobbies WHERE id = ?",
		)
		.bind(id)
		.first();
	if (!current) return json({ error: "not_found" }, 404);
	if (current.ownerTokenHash !== ownerTokenHash)
		return json({ error: "owner_token_mismatch" }, 403);

	const now = nowSeconds();
	const ttl = clampInt(
		body.ttlSeconds,
		30,
		MAX_TTL_SECONDS,
		DEFAULT_TTL_SECONDS,
	);
	const updates = [];
	const values = [];

	if (body.players !== undefined) {
		updates.push("players = ?");
		values.push(clampInt(body.players, 0, 99, 0));
	}
	if (body.maxPlayers !== undefined) {
		updates.push("maxPlayers = ?");
		values.push(clampInt(body.maxPlayers, 1, 99, 15));
	}
	if (body.state !== undefined) {
		const nextState = sanitizeState(body.state);
		if (nextState !== current.state) {
			updates.push("stateChangedAt = ?");
			values.push(now);
		} else if (!current.stateChangedAt) {
			updates.push("stateChangedAt = ?");
			values.push(current.updatedAt || now);
		}
		updates.push("state = ?");
		values.push(nextState);
	}
	if (body.host !== undefined) {
		updates.push("host = ?");
		values.push(clean(body.host, MAX_HOST, "Unknown"));
	}

	updates.push("updatedAt = ?", "expiresAt = ?");
	values.push(now, now + ttl, id);

	await db
		.prepare(`UPDATE lobbies SET ${updates.join(", ")} WHERE id = ?`)
		.bind(...values)
		.run();
	return json({ ok: true, id, expiresAt: now + ttl });
}

async function deleteLobby(request, db, id) {
	const body = await readJson(request);
	if (!body) return json({ error: "invalid_json" }, 400);
	const ownerToken = sanitizeToken(body.ownerToken);
	if (!ownerToken) return json({ error: "missing_owner_token" }, 400);

	const ownerTokenHash = await sha256(ownerToken);
	const current = await db
		.prepare("SELECT ownerTokenHash FROM lobbies WHERE id = ?")
		.bind(id)
		.first();
	if (!current) return json({ ok: true, id });
	if (current.ownerTokenHash !== ownerTokenHash)
		return json({ error: "owner_token_mismatch" }, 403);

	await db.prepare("DELETE FROM lobbies WHERE id = ?").bind(id).run();
	return json({ ok: true, id });
}

function sanitizeLobby(body, id) {
	const code = String(body.code || "")
		.trim()
		.toUpperCase()
		.replace(/[^A-Z]/g, "")
		.slice(0, MAX_CODE);
	if (code.length < 4) return null;

	return {
		id,
		code,
		region: clean(body.region, MAX_REGION, "Unknown"),
		language: clean(body.language, MAX_LANGUAGE, "English"),
		title: clean(body.title, MAX_TITLE, "Voice Lobby"),
		host: clean(body.host, MAX_HOST, "Unknown"),
		players: clampInt(body.players, 0, 99, 0),
		maxPlayers: clampInt(body.maxPlayers, 1, 99, 15),
		state: sanitizeState(body.state),
		modVersion: clean(body.modVersion, MAX_MOD_VERSION, "unknown"),
		protocolVersion: clampInt(body.protocolVersion, 1, 999999, 1),
	};
}

function sanitizeLobbyId(value, fallback) {
	const raw = value === undefined || value === null || value === ""
		? fallback
		: String(value).trim();
	return /^[A-Za-z0-9_-]{1,64}$/.test(raw) ? raw : "";
}

function decodeLobbyPathId(value) {
	try {
		return sanitizeLobbyId(decodeURIComponent(value), "");
	} catch {
		return "";
	}
}

function sanitizeState(value) {
	const state = clean(value, 16, "Unknown");
	return ["Lobby", "InGame", "Closed", "Unknown"].includes(state)
		? state
		: "Unknown";
}

function sanitizeToken(value) {
	const token = String(value || "").trim();
	return token.length >= 16 && token.length <= 256 ? token : "";
}

function clean(value, max, fallback) {
	const text = String(value ?? "")
		.replace(/[\u0000-\u001F\u007F]/g, "")
		.trim();
	return (text || fallback).slice(0, max);
}

function clampInt(value, min, max, fallback) {
	const num = Number.parseInt(value, 10);
	if (!Number.isFinite(num)) return fallback;
	return Math.max(min, Math.min(max, num));
}

async function readJson(request) {
	const contentType = request.headers.get("content-type") || "";
	if (!contentType.toLowerCase().includes("application/json")) return null;

	const contentLength = Number.parseInt(
		request.headers.get("content-length") || "0",
		10,
	);
	if (Number.isFinite(contentLength) && contentLength > MAX_BODY_BYTES)
		return null;

	const text = await request.text();
	if (text.length > MAX_BODY_BYTES) return null;

	try {
		const parsed = JSON.parse(text || "{}");
		return parsed && typeof parsed === "object" && !Array.isArray(parsed)
			? parsed
			: null;
	} catch {
		return null;
	}
}

async function sha256(value) {
	const bytes = new TextEncoder().encode(value);
	const hash = await crypto.subtle.digest("SHA-256", bytes);
	return [...new Uint8Array(hash)]
		.map((b) => b.toString(16).padStart(2, "0"))
		.join("");
}

async function purgeExpired(db, now) {
	await db
		.prepare("DELETE FROM lobbies WHERE expiresAt <= ? OR updatedAt <= ?")
		.bind(now, now - STALE_GRACE_SECONDS)
		.run();
}

async function maybePurgeExpired(db, now) {
	if (now - lastPurgeAt < PURGE_INTERVAL_SECONDS) return;
	lastPurgeAt = now;
	await purgeExpired(db, now);
}

function isMutationRequest(request) {
	return request.method === "POST" || request.method === "DELETE";
}

function allowMutation(request, now) {
	return allowWindow(
		mutationWindows,
		clientKey(request),
		now,
		MAX_MUTATIONS_PER_WINDOW,
	);
}

function allowWindow(windows, key, now, limit) {
	const current = windows.get(key);
	if (!current || now - current.windowStart >= RATE_LIMIT_WINDOW_SECONDS) {
		windows.set(key, { windowStart: now, count: 1 });
		pruneRateLimitKeys(windows, now);
		return true;
	}

	current.count += 1;
	return current.count <= limit;
}

function clientKey(request) {
	const cfIp = request.headers.get("cf-connecting-ip");
	if (cfIp) return cfIp;
	const forwarded = request.headers.get("x-forwarded-for");
	if (forwarded) return forwarded.split(",")[0].trim() || "unknown";
	return "unknown";
}


function pruneRateLimitKeys(windows, now) {
	if (windows.size <= MAX_RATE_LIMIT_KEYS) return;
	for (const [key, value] of windows) {
		if (now - value.windowStart >= RATE_LIMIT_WINDOW_SECONDS)
			windows.delete(key);
		if (windows.size <= MAX_RATE_LIMIT_KEYS) break;
	}
	for (const key of windows.keys()) {
		if (windows.size <= MAX_RATE_LIMIT_KEYS) break;
		windows.delete(key);
	}
}

function nowSeconds() {
	return Math.floor(Date.now() / 1000);
}

function json(value, status = 200) {
	return withCors(
		new Response(JSON.stringify(value), {
			status,
			headers: { "content-type": "application/json; charset=utf-8" },
		}),
	);
}

function turnJson(value, status = 200) {
	return privateResponse(
		new Response(JSON.stringify(value), {
			status,
			headers: { "content-type": "application/json; charset=utf-8" },
		}),
	);
}

function privateResponse(response) {
	const headers = new Headers(response.headers);
	headers.set("cache-control", "no-store");
	headers.set("referrer-policy", "no-referrer");
	headers.set("x-content-type-options", "nosniff");
	headers.delete("access-control-allow-origin");
	headers.delete("access-control-allow-methods");
	headers.delete("access-control-allow-headers");
	return new Response(response.body, {
		status: response.status,
		statusText: response.statusText,
		headers,
	});
}

function withCors(response) {
	const headers = new Headers(response.headers);
	headers.set("access-control-allow-origin", "*");
	headers.set("access-control-allow-methods", "GET,POST,DELETE,OPTIONS");
	headers.set("access-control-allow-headers", "content-type");
	headers.set("cache-control", "no-store");
	return new Response(response.body, {
		status: response.status,
		statusText: response.statusText,
		headers,
	});
}
