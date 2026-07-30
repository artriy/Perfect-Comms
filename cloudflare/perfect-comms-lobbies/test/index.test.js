import assert from "node:assert/strict";
import test from "node:test";
import worker, { LobbyHub, testing } from "../src/index.js";

const LIVE_URL = "https://example.com/lobbies/live";

function attachment(role, overrides = {}) {
	return {
		role,
		lastSeen: Math.floor(Date.now() / 1000),
		windowStartedAt: Math.floor(Date.now() / 1000),
		windowMessages: 0,
		ownerToken: "",
		listing: null,
		...overrides,
	};
}

class FakeSocket {
	constructor(role, overrides = {}) {
		this.attachment = attachment(role, overrides);
		this.sent = [];
		this.closed = null;
	}

	serializeAttachment(value) {
		this.attachment = JSON.parse(JSON.stringify(value));
	}

	deserializeAttachment() {
		return JSON.parse(JSON.stringify(this.attachment));
	}

	send(message) {
		this.sent.push(JSON.parse(message));
	}

	close(code, reason) {
		this.closed = { code, reason };
	}
}

class FakeContext {
	constructor(sockets = []) {
		this.sockets = sockets;
		this.alarmAt = null;
		this.storage = {
			getAlarm: async () => this.alarmAt,
			setAlarm: async (value) => { this.alarmAt = value; },
		};
	}

	getWebSockets(tag) {
		return this.sockets.filter((socket) => socket.attachment.role === tag);
	}

	acceptWebSocket(socket) {
		this.sockets.push(socket);
	}
}

function publish(overrides = {}) {
	return JSON.stringify({
		wire: testing.LOBBY_WIRE_VERSION,
		type: "publish",
		lobby: {
			id: "lobby-1",
			ownerToken: "owner-token-at-least-16-chars",
			code: "ABCDEF",
			region: "North America",
			language: "English",
			title: "Friday Crew",
			host: "Alice",
			players: 4,
			maxPlayers: 15,
			state: "Lobby",
			modVersion: "4.1.7",
			protocolVersion: 5,
			...overrides,
		},
	});
}

function messages(socket, type) {
	return socket.sent.filter((message) => message.type === type);
}

test("health identifies the live Durable Object directory", async () => {
	const response = await worker.fetch(new Request("https://example.com/health"), {});
	assert.equal(response.status, 200);
	assert.deepEqual(await response.json(), {
		ok: true,
		service: "perfect-comms-lobbies",
		directory: "durable-object-websocket",
		wireVersion: 1,
	});
});

test("live route requires a WebSocket upgrade and a valid role", async () => {
	const ordinary = await worker.fetch(new Request(`${LIVE_URL}?role=browser`), {});
	assert.equal(ordinary.status, 426);
	assert.equal(ordinary.headers.get("upgrade"), "websocket");
	assert.equal(ordinary.headers.get("access-control-allow-origin"), null);

	const invalid = await worker.fetch(new Request(`${LIVE_URL}?role=reader`, {
		headers: { upgrade: "websocket" },
	}), { LOBBY_HUB: {} });
	assert.equal(invalid.status, 400);
	assert.deepEqual(await invalid.json(), { error: "invalid_role" });
});

test("live route forwards upgrades to the global Durable Object", async () => {
	let selectedName = "";
	let forwardedUrl = "";
	const env = {
		LOBBY_HUB: {
			idFromName(name) {
				selectedName = name;
				return "hub-id";
			},
			get(id) {
				assert.equal(id, "hub-id");
				return {
					async fetch(request) {
						forwardedUrl = request.url;
						return new Response(null, { status: 204 });
					},
				};
			},
		},
	};
	const response = await worker.fetch(new Request(`${LIVE_URL}?role=browser`, {
		headers: { upgrade: "websocket" },
	}), env);
	assert.equal(response.status, 204);
	assert.equal(selectedName, "perfect-comms-global-v1");
	assert.equal(forwardedUrl, `${LIVE_URL}?role=browser`);
});

test("retired polling lobby routes stay unavailable", async () => {
	for (const path of ["/lobbies", "/lobbies/example", "/lobbies/example/heartbeat"])
	{
		const response = await worker.fetch(new Request(`https://example.com${path}`), {});
		assert.equal(response.status, 404);
	}
});

test("host publish broadcasts an owned, sanitized live listing", async () => {
	const host = new FakeSocket("host");
	const browser = new FakeSocket("browser");
	const ctx = new FakeContext([host, browser]);
	const hub = new LobbyHub(ctx, {});

	await hub.webSocketMessage(host, publish({
		title: "  Crew\u0000 Night  ",
		players: 99,
		maxPlayers: 12,
	}));

	assert.equal(messages(host, "published").length, 1);
	const upserts = messages(browser, "upsert");
	assert.equal(upserts.length, 1);
	assert.deepEqual(upserts[0].lobby, {
		id: "lobby-1",
		code: "ABCDEF",
		region: "North America",
		language: "English",
		title: "Crew Night",
		host: "Alice",
		players: 12,
		maxPlayers: 12,
		state: "Lobby",
		stateChangedAt: upserts[0].lobby.stateChangedAt,
		modVersion: "4.1.7",
		protocolVersion: 5,
		updatedAt: upserts[0].lobby.updatedAt,
		expiresAt: upserts[0].lobby.expiresAt,
	});
	assert.ok(upserts[0].lobby.stateChangedAt > 0);
	assert.ok(upserts[0].lobby.expiresAt > upserts[0].lobby.updatedAt);
	assert.equal("ownerToken" in upserts[0].lobby, false);
	assert.equal(host.attachment.ownerToken, "owner-token-at-least-16-chars");
	assert.ok(ctx.alarmAt > Date.now());
});

test("browser snapshot contains current listings without polling storage", async () => {
	const host = new FakeSocket("host");
	const browser = new FakeSocket("browser");
	const hub = new LobbyHub(new FakeContext([host, browser]), {});
	await hub.webSocketMessage(host, publish());
	browser.sent.length = 0;

	hub.sendSnapshot(browser);

	assert.equal(browser.sent.length, 1);
	assert.equal(browser.sent[0].type, "snapshot");
	assert.equal(browser.sent[0].wire, 1);
	assert.equal(browser.sent[0].lobbies.length, 1);
	assert.equal(browser.sent[0].lobbies[0].code, "ABCDEF");
});

test("host update preserves state age and broadcasts changed players immediately", async () => {
	const host = new FakeSocket("host");
	const browser = new FakeSocket("browser");
	const hub = new LobbyHub(new FakeContext([host, browser]), {});
	await hub.webSocketMessage(host, publish({ players: 2 }));
	const first = messages(browser, "upsert").at(-1).lobby;

	await hub.webSocketMessage(host, publish({ players: 3 }));
	const second = messages(browser, "upsert").at(-1).lobby;

	assert.equal(second.players, 3);
	assert.equal(second.stateChangedAt, first.stateChangedAt);
});

test("state transition resets stateChangedAt", async () => {
	const host = new FakeSocket("host");
	const browser = new FakeSocket("browser");
	const hub = new LobbyHub(new FakeContext([host, browser]), {});
	await hub.webSocketMessage(host, publish({ state: "Lobby" }));
	host.attachment.listing.stateChangedAt = 10;
	await hub.webSocketMessage(host, publish({ state: "InGame" }));

	const changed = messages(browser, "upsert").at(-1).lobby;
	assert.equal(changed.state, "InGame");
	assert.ok(changed.stateChangedAt > 10);
});

test("same owner token transfers a listing to a replacement host socket", async () => {
	const firstHost = new FakeSocket("host");
	const replacement = new FakeSocket("host");
	const browser = new FakeSocket("browser");
	const hub = new LobbyHub(new FakeContext([firstHost, replacement, browser]), {});
	await hub.webSocketMessage(firstHost, publish({ players: 2 }));
	await hub.webSocketMessage(replacement, publish({ players: 5 }));

	assert.equal(firstHost.attachment.listing, null);
	assert.deepEqual(firstHost.closed, { code: 4001, reason: "listing connection replaced" });
	assert.equal(replacement.attachment.listing.players, 5);
	assert.equal(hub.activeListings().length, 1);
});

test("host changing lobby code removes its prior listing id", async () => {
	const host = new FakeSocket("host");
	const browser = new FakeSocket("browser");
	const hub = new LobbyHub(new FakeContext([host, browser]), {});
	await hub.webSocketMessage(host, publish());
	browser.sent.length = 0;

	await hub.webSocketMessage(host, publish({
		id: "lobby-2",
		ownerToken: "replacement-owner-token-12345",
		code: "ZZZZZZ",
	}));

	assert.equal(messages(browser, "remove").at(-1).id, "lobby-1");
	assert.equal(messages(browser, "upsert").at(-1).lobby.id, "lobby-2");
	assert.equal(hub.activeListings().length, 1);
});

test("different owner token cannot hijack an active listing id", async () => {
	const owner = new FakeSocket("host");
	const attacker = new FakeSocket("host");
	const browser = new FakeSocket("browser");
	const hub = new LobbyHub(new FakeContext([owner, attacker, browser]), {});
	await hub.webSocketMessage(owner, publish());
	browser.sent.length = 0;
	await hub.webSocketMessage(attacker, publish({ ownerToken: "different-owner-token-12345" }));

	assert.equal(messages(attacker, "error").at(-1).error, "listing_id_in_use");
	assert.equal(attacker.attachment.listing, null);
	assert.equal(messages(browser, "upsert").length, 0);
	assert.equal(owner.attachment.listing.id, "lobby-1");
});

test("remove and socket close immediately remove the owned listing", async () => {
	const host = new FakeSocket("host");
	const browser = new FakeSocket("browser");
	const hub = new LobbyHub(new FakeContext([host, browser]), {});
	await hub.webSocketMessage(host, publish());
	browser.sent.length = 0;

	await hub.webSocketMessage(host, JSON.stringify({ wire: 1, type: "remove" }));
	assert.equal(messages(browser, "remove").at(-1).id, "lobby-1");
	assert.equal(host.attachment.listing, null);

	await hub.webSocketMessage(host, publish());
	browser.sent.length = 0;
	hub.webSocketClose(host);
	assert.equal(messages(browser, "remove").at(-1).id, "lobby-1");
});

test("alarm prunes stale hosts and notifies browsers", async () => {
	const stale = new FakeSocket("host");
	const browser = new FakeSocket("browser");
	const ctx = new FakeContext([stale, browser]);
	const hub = new LobbyHub(ctx, {});
	await hub.webSocketMessage(stale, publish());
	stale.attachment.lastSeen = Math.floor(Date.now() / 1000) - testing.HOST_STALE_SECONDS - 1;
	browser.sent.length = 0;

	await hub.alarm();

	assert.equal(stale.attachment.listing, null);
	assert.equal(messages(browser, "remove").at(-1).id, "lobby-1");
	assert.deepEqual(stale.closed, { code: 4000, reason: "host heartbeat expired" });
});

test("invalid state and oversized messages are rejected", async () => {
	const host = new FakeSocket("host");
	const hub = new LobbyHub(new FakeContext([host]), {});
	await hub.webSocketMessage(host, publish({ state: "Unknown" }));
	assert.equal(messages(host, "error").at(-1).error, "invalid_lobby");

	await hub.webSocketMessage(host, "x".repeat(5000));
	assert.equal(messages(host, "error").at(-1).error, "invalid_message");
	assert.equal(host.closed.code, 1008);
});

test("per-socket message limit closes abusive clients", async () => {
	const browser = new FakeSocket("browser");
	const hub = new LobbyHub(new FakeContext([browser]), {});
	const refresh = JSON.stringify({ wire: 1, type: "refresh" });
	for (let index = 0; index < 31; index++)
		await hub.webSocketMessage(browser, refresh);
	assert.equal(messages(browser, "error").at(-1).error, "rate_limited");
	assert.equal(browser.closed.code, 1008);
});

test("TURN credentials remain private and use the configured upstream", async () => {
	let upstreamRequest;
	const env = {
		TURN_TOKEN_ID: "token-id",
		TURN_API_TOKEN: "api-secret",
		TURN_RATE_LIMITER: { async limit() { return { success: true }; } },
		async TURN_CREDENTIAL_FETCH(url, options) {
			upstreamRequest = { url, options };
			return new Response(JSON.stringify({
				iceServers: [{
					urls: ["stun:turn.example.com", "turn:turn.example.com"],
					username: "generated-user",
					credential: "generated-password",
				}],
			}), { status: 200, headers: { "content-type": "application/json" } });
		},
	};
	const response = await worker.fetch(new Request("https://example.com/turn-credentials", {
		method: "POST",
		headers: { "cf-connecting-ip": "203.0.113.10" },
	}), env);
	assert.equal(response.status, 200);
	assert.equal(response.headers.get("access-control-allow-origin"), null);
	assert.equal(response.headers.get("cache-control"), "no-store");
	const body = await response.json();
	assert.equal(body.ttl, 3600);
	assert.equal(body.iceServers[0].username, "generated-user");
	assert.match(upstreamRequest.url, /token-id\/credentials\/generate-ice-servers$/);
	assert.equal(upstreamRequest.options.headers.authorization, "Bearer api-secret");
	const requestBody = JSON.parse(upstreamRequest.options.body);
	assert.equal(requestBody.ttl, 3600);
	assert.match(requestBody.customIdentifier, /^[a-f0-9]{32}$/);
});

test("TURN credential rate limiting fails closed without fetching", async () => {
	let fetched = false;
	const response = await worker.fetch(new Request("https://example.com/turn-credentials", {
		method: "POST",
	}), {
		TURN_TOKEN_ID: "token-id",
		TURN_API_TOKEN: "api-secret",
		TURN_RATE_LIMITER: { async limit() { return { success: false }; } },
		async TURN_CREDENTIAL_FETCH() {
			fetched = true;
			throw new Error("must not fetch");
		},
	});
	assert.equal(response.status, 429);
	assert.deepEqual(await response.json(), { error: "rate_limited" });
	assert.equal(fetched, false);
});

test("latest update notification compares release versions", async () => {
	const env = {
		UPDATE_RELEASE_FIXTURE_JSON: JSON.stringify({
			tag_name: "v4.1.7",
			html_url: "https://github.com/artriy/Perfect-Comms/releases/tag/v4.1.7",
		}),
	};
	const response = await worker.fetch(
		new Request("https://example.com/updates/latest?current=4.1.6"),
		env,
	);
	const body = await response.json();
	assert.equal(body.enabled, true);
	assert.equal(body.latestVersion, "v4.1.7");
	assert.equal(body.showEveryMainMenu, false);
});
