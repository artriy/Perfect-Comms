const LOBBY_WIRE_VERSION = 1;
const LOBBY_HUB_NAME = "perfect-comms-global-v1";
const HOST_STALE_SECONDS = 90;
const PRUNE_INTERVAL_SECONDS = 30;
const MAX_HOSTS = 500;
const MAX_BROWSERS = 5000;
const MAX_VISIBLE_LOBBIES = 100;
const MAX_MESSAGE_BYTES = 4096;
const MAX_MESSAGES_PER_MINUTE = 30;
const MAX_TITLE = 40;
const MAX_HOST = 24;
const MAX_REGION = 40;
const MAX_LANGUAGE = 16;
const MAX_MOD_VERSION = 24;
const MAX_CODE = 8;
const GITHUB_LATEST_RELEASE_API =
	"https://api.github.com/repos/artriy/Perfect-Comms/releases/latest";
const GITHUB_RELEASES_URL =
	"https://github.com/artriy/Perfect-Comms/releases/latest";
const TURN_TTL_SECONDS = 3600;
const RATE_LIMIT_WINDOW_SECONDS = 60;
const MAX_TURN_CREDENTIALS_PER_WINDOW = 30;
const MAX_RATE_LIMIT_KEYS = 2048;

const turnCredentialWindows = new Map();

export default {
	async fetch(request, env) {
		try {
			const url = new URL(request.url);
			const now = nowSeconds();

			if (url.pathname === "/lobbies/live")
				return await liveLobbyUpgrade(request, env, url);

			// TURN credentials are for the native client only. Keeping this route out
			// of the wildcard CORS surface prevents websites from minting credentials.
			if (url.pathname === "/turn-credentials") {
				if (request.method !== "POST") return turnMethodNotAllowed();
				return await turnCredentials(request, env, now);
			}

			if (request.method === "OPTIONS")
				return withCors(new Response(null, { status: 204 }));

			if (url.pathname === "/health" && request.method === "GET") {
				return json({
					ok: true,
					service: "perfect-comms-lobbies",
					directory: "durable-object-websocket",
					wireVersion: LOBBY_WIRE_VERSION,
				});
			}

			if (url.pathname === "/updates/latest" && request.method === "GET")
				return await latestUpdateNotification(url, env);

			return json({ error: "not_found" }, 404);
		} catch {
			return json({ error: "server_error" }, 500);
		}
	},
};

async function liveLobbyUpgrade(request, env, url) {
	if (request.method !== "GET")
		return liveJson({ error: "method_not_allowed" }, 405, { allow: "GET" });
	if ((request.headers.get("upgrade") || "").toLowerCase() !== "websocket")
		return liveJson({ error: "websocket_upgrade_required" }, 426, { upgrade: "websocket" });

	const role = url.searchParams.get("role");
	if (role !== "host" && role !== "browser")
		return liveJson({ error: "invalid_role" }, 400);
	if (!env.LOBBY_HUB)
		return liveJson({ error: "live_directory_not_configured" }, 503);

	const id = env.LOBBY_HUB.idFromName(LOBBY_HUB_NAME);
	return env.LOBBY_HUB.get(id).fetch(request);
}

export class LobbyHub {
	constructor(ctx, env) {
		this.ctx = ctx;
		this.env = env;
	}

	async fetch(request) {
		const url = new URL(request.url);
		const role = url.searchParams.get("role");
		if (role !== "host" && role !== "browser")
			return liveJson({ error: "invalid_role" }, 400);
		if ((request.headers.get("upgrade") || "").toLowerCase() !== "websocket")
			return liveJson({ error: "websocket_upgrade_required" }, 426, { upgrade: "websocket" });

		const existing = this.ctx.getWebSockets(role).length;
		const limit = role === "host" ? MAX_HOSTS : MAX_BROWSERS;
		if (existing >= limit)
			return liveJson({ error: "directory_capacity" }, 503);

		const pair = new WebSocketPair();
		const [client, server] = Object.values(pair);
		server.serializeAttachment({
			role,
			lastSeen: nowSeconds(),
			windowStartedAt: nowSeconds(),
			windowMessages: 0,
			ownerToken: "",
			listing: null,
		});
		this.ctx.acceptWebSocket(server, [role]);

		if (role === "browser")
			this.sendSnapshot(server);
		else {
			safeSend(server, envelope("ready", { heartbeatSeconds: PRUNE_INTERVAL_SECONDS }));
			await this.ensurePruneAlarm();
		}

		return new Response(null, { status: 101, webSocket: client });
	}

	async webSocketMessage(ws, message) {
		if (typeof message !== "string" || byteLength(message) > MAX_MESSAGE_BYTES) {
			this.rejectSocket(ws, "invalid_message", "Messages must be UTF-8 JSON no larger than 4096 bytes");
			return;
		}

		let payload;
		try {
			payload = JSON.parse(message);
		} catch {
			this.sendError(ws, "invalid_json");
			return;
		}
		if (!payload || typeof payload !== "object" || Array.isArray(payload)) {
			this.sendError(ws, "invalid_message");
			return;
		}
		if (payload.wire !== LOBBY_WIRE_VERSION) {
			this.sendError(ws, "wire_version_mismatch");
			return;
		}

		const attachment = readAttachment(ws);
		if (!this.consumeMessageAllowance(ws, attachment)) return;

		if (attachment.role === "browser") {
			if (payload.type !== "refresh") {
				this.sendError(ws, "browser_message_not_allowed");
				return;
			}
			attachment.lastSeen = nowSeconds();
			ws.serializeAttachment(attachment);
			this.sendSnapshot(ws);
			return;
		}

		if (attachment.role !== "host") {
			this.rejectSocket(ws, "invalid_role", "Unknown live-directory role");
			return;
		}

		const now = nowSeconds();
		attachment.lastSeen = now;
		if (payload.type === "heartbeat") {
			if (attachment.listing)
				attachment.listing.expiresAt = now + HOST_STALE_SECONDS;
			ws.serializeAttachment(attachment);
			await this.ensurePruneAlarm();
			return;
		}
		if (payload.type === "remove") {
			this.removeOwnedListing(ws, attachment);
			ws.serializeAttachment(attachment);
			return;
		}
		if (payload.type !== "publish") {
			this.sendError(ws, "host_message_not_allowed");
			return;
		}

		const ownerToken = sanitizeToken(payload.lobby?.ownerToken);
		const id = sanitizeLobbyId(payload.lobby?.id);
		const cleanListing = sanitizeLobby(payload.lobby, id);
		if (!ownerToken || !id || !cleanListing) {
			this.sendError(ws, "invalid_lobby");
			return;
		}

		const ownership = this.claimListing(ws, id, ownerToken);
		if (!ownership.ok) {
			this.sendError(ws, "listing_id_in_use");
			return;
		}

		const replacedId = attachment.listing && attachment.listing.id !== id
			? attachment.listing.id
			: "";
		const previous = attachment.listing?.id === id
			? attachment.listing
			: ownership.previous;
		if (!attachment.listing && !ownership.previous
			&& this.activeListings(now).length >= MAX_VISIBLE_LOBBIES) {
			this.sendError(ws, "directory_capacity");
			return;
		}

		cleanListing.stateChangedAt = previous && previous.state === cleanListing.state
			? previous.stateChangedAt || previous.updatedAt || now
			: now;
		cleanListing.updatedAt = now;
		cleanListing.expiresAt = now + HOST_STALE_SECONDS;
		attachment.ownerToken = ownerToken;
		attachment.listing = cleanListing;
		ws.serializeAttachment(attachment);
		if (replacedId)
			this.broadcast(envelope("remove", { id: replacedId }));
		this.broadcast(envelope("upsert", { lobby: cleanListing }));
		safeSend(ws, envelope("published", { id: cleanListing.id }));
		await this.ensurePruneAlarm();
	}

	webSocketClose(ws) {
		const attachment = readAttachment(ws);
		this.removeOwnedListing(ws, attachment);
	}

	webSocketError(ws) {
		const attachment = readAttachment(ws);
		this.removeOwnedListing(ws, attachment);
	}

	async alarm() {
		const now = nowSeconds();
		let hasPublishedHosts = false;
		for (const ws of this.ctx.getWebSockets("host")) {
			const attachment = readAttachment(ws);
			if (!attachment.listing) continue;
			if (now - attachment.lastSeen > HOST_STALE_SECONDS) {
				const id = attachment.listing.id;
				attachment.listing = null;
				attachment.ownerToken = "";
				ws.serializeAttachment(attachment);
				this.broadcast(envelope("remove", { id }));
				try { ws.close(4000, "host heartbeat expired"); } catch { }
				continue;
			}
			hasPublishedHosts = true;
		}
		if (hasPublishedHosts)
			await this.ctx.storage.setAlarm(Date.now() + PRUNE_INTERVAL_SECONDS * 1000);
	}

	consumeMessageAllowance(ws, attachment) {
		const now = nowSeconds();
		if (now - attachment.windowStartedAt >= RATE_LIMIT_WINDOW_SECONDS) {
			attachment.windowStartedAt = now;
			attachment.windowMessages = 0;
		}
		attachment.windowMessages += 1;
		ws.serializeAttachment(attachment);
		if (attachment.windowMessages <= MAX_MESSAGES_PER_MINUTE) return true;
		this.rejectSocket(ws, "rate_limited", "Too many live-directory messages");
		return false;
	}

	claimListing(currentSocket, id, ownerToken) {
		let previous = null;
		for (const candidate of this.ctx.getWebSockets("host")) {
			if (candidate === currentSocket) continue;
			const attachment = readAttachment(candidate);
			if (attachment.listing?.id !== id) continue;
			if (attachment.ownerToken !== ownerToken)
				return { ok: false, previous: null };

			previous = attachment.listing;
			attachment.listing = null;
			attachment.ownerToken = "";
			candidate.serializeAttachment(attachment);
			try { candidate.close(4001, "listing connection replaced"); } catch { }
		}
		return { ok: true, previous };
	}

	removeOwnedListing(ws, attachment) {
		if (!attachment.listing) return;
		const id = attachment.listing.id;
		attachment.listing = null;
		attachment.ownerToken = "";
		try { ws.serializeAttachment(attachment); } catch { }
		this.broadcast(envelope("remove", { id }));
	}

	activeListings(now = nowSeconds()) {
		const byId = new Map();
		for (const ws of this.ctx.getWebSockets("host")) {
			const attachment = readAttachment(ws);
			const listing = attachment.listing;
			if (!listing || now - attachment.lastSeen > HOST_STALE_SECONDS) continue;
			listing.expiresAt = attachment.lastSeen + HOST_STALE_SECONDS;
			const existing = byId.get(listing.id);
			if (!existing || listing.updatedAt >= existing.updatedAt)
				byId.set(listing.id, listing);
		}
		return [...byId.values()]
			.sort((left, right) => {
				const stateOrder = Number(right.state === "Lobby") - Number(left.state === "Lobby");
				return stateOrder || right.updatedAt - left.updatedAt || right.players - left.players;
			})
			.slice(0, MAX_VISIBLE_LOBBIES);
	}

	sendSnapshot(ws) {
		safeSend(ws, envelope("snapshot", {
			lobbies: this.activeListings(),
			now: nowSeconds(),
		}));
	}

	broadcast(message) {
		for (const browser of this.ctx.getWebSockets("browser"))
			safeSend(browser, message);
	}

	sendError(ws, code) {
		safeSend(ws, envelope("error", { error: code }));
	}

	rejectSocket(ws, code, reason) {
		this.sendError(ws, code);
		try { ws.close(1008, reason); } catch { }
	}

	async ensurePruneAlarm() {
		const next = Date.now() + PRUNE_INTERVAL_SECONDS * 1000;
		const current = await this.ctx.storage.getAlarm();
		if (current === null || current > next)
			await this.ctx.storage.setAlarm(next);
	}
}

function envelope(type, values = {}) {
	return JSON.stringify({ wire: LOBBY_WIRE_VERSION, type, ...values });
}

function safeSend(ws, message) {
	try { ws.send(message); } catch { }
}

function readAttachment(ws) {
	try {
		return ws.deserializeAttachment() || {
			role: "",
			lastSeen: 0,
			windowStartedAt: 0,
			windowMessages: 0,
			ownerToken: "",
			listing: null,
		};
	} catch {
		return {
			role: "",
			lastSeen: 0,
			windowStartedAt: 0,
			windowMessages: 0,
			ownerToken: "",
			listing: null,
		};
	}
}

function sanitizeLobby(body, id) {
	if (!body || typeof body !== "object" || Array.isArray(body) || !id) return null;
	const code = String(body.code || "")
		.trim()
		.toUpperCase()
		.replace(/[^A-Z]/g, "")
		.slice(0, MAX_CODE);
	if (code.length < 4) return null;

	const region = clean(body.region, MAX_REGION, "");
	const state = sanitizeState(body.state);
	if (!region || !state) return null;
	const maxPlayers = clampInt(body.maxPlayers, 1, 99, 15);

	return {
		id,
		code,
		region,
		language: clean(body.language, MAX_LANGUAGE, "English"),
		title: clean(body.title, MAX_TITLE, "Perfect Comms"),
		host: clean(body.host, MAX_HOST, "Unknown"),
		players: clampInt(body.players, 0, maxPlayers, 0),
		maxPlayers,
		state,
		stateChangedAt: 0,
		modVersion: clean(body.modVersion, MAX_MOD_VERSION, "unknown"),
		protocolVersion: clampInt(body.protocolVersion, 1, 999999, 1),
		updatedAt: 0,
		expiresAt: 0,
	};
}

function sanitizeLobbyId(value) {
	const raw = String(value || "").trim();
	return /^[A-Za-z0-9_-]{1,64}$/.test(raw) ? raw : "";
}

function sanitizeToken(value) {
	const token = String(value || "").trim();
	return token.length >= 16 && token.length <= 256 ? token : "";
}

function sanitizeState(value) {
	const state = clean(value, 16, "");
	return state === "Lobby" || state === "InGame" ? state : "";
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

function byteLength(value) {
	return new TextEncoder().encode(value).byteLength;
}

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
		const customIdentifier = await turnCustomIdentifier(request, apiToken, now);
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
		return turnJson({ iceServers: payload.iceServers, ttl: TURN_TTL_SECONDS });
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
	return allowWindow(turnCredentialWindows, key, now, MAX_TURN_CREDENTIALS_PER_WINDOW);
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
		if (!server || typeof server !== "object" || Array.isArray(server)) return false;
		const urls = Array.isArray(server.urls) ? server.urls : [server.urls];
		const validUrls = urls.length > 0 && urls.every(
			(url) => typeof url === "string" && /^(stun|turn|turns):/i.test(url),
		);
		if (!validUrls) return false;
		const hasRelayUrl = urls.some(
			(url) => typeof url === "string" && /^turns?:/i.test(url),
		);
		if (hasRelayUrl) {
			if (typeof server.username !== "string" || server.username.length === 0 ||
				typeof server.credential !== "string" || server.credential.length === 0)
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
			tag_name: env.UPDATE_RELEASE_FALLBACK_VERSION || env.UPDATE_LATEST_VERSION || "1.0.0",
			html_url: env.UPDATE_RELEASE_FALLBACK_URL || env.UPDATE_RELEASE_URL || GITHUB_RELEASES_URL,
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
	return withCors(new Response(JSON.stringify(value), {
		status,
		headers: { "content-type": "application/json; charset=utf-8" },
	}));
}

function liveJson(value, status = 200, extraHeaders = {}) {
	return privateResponse(new Response(JSON.stringify(value), {
		status,
		headers: {
			"content-type": "application/json; charset=utf-8",
			...extraHeaders,
		},
	}));
}

function turnJson(value, status = 200) {
	return privateResponse(new Response(JSON.stringify(value), {
		status,
		headers: { "content-type": "application/json; charset=utf-8" },
	}));
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
	headers.set("access-control-allow-methods", "GET,OPTIONS");
	headers.set("access-control-allow-headers", "content-type");
	headers.set("cache-control", "no-store");
	return new Response(response.body, {
		status: response.status,
		statusText: response.statusText,
		headers,
	});
}

export const testing = {
	sanitizeLobby,
	sanitizeLobbyId,
	sanitizeState,
	envelope,
	HOST_STALE_SECONDS,
	LOBBY_WIRE_VERSION,
};
