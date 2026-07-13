import test from "node:test";
import assert from "node:assert/strict";
import worker from "../src/index.js";

test("health returns ok", async () => {
	const env = {
		DB: new FakeDB(),
		TURN_TOKEN_ID: "health-token-id-must-not-leak",
		TURN_API_TOKEN: "health-api-token-must-not-leak",
	};
	const response = await worker.fetch(
		new Request("https://example.com/health"),
		env,
	);
	assert.equal(response.status, 200);
	const text = await response.text();
	assert.equal(JSON.parse(text).ok, true);
	assert.equal(text.includes(env.TURN_TOKEN_ID), false);
	assert.equal(text.includes(env.TURN_API_TOKEN), false);
});

test("TURN credentials are POST-only, private, short-lived, and sanitized", async () => {
	let upstreamRequest;
	const env = {
		TURN_TOKEN_ID: "configured-token-id",
		TURN_API_TOKEN: "configured-api-token",
		TURN_RATE_LIMITER: {
			async limit() {
				return { success: true };
			},
		},
		async TURN_CREDENTIAL_FETCH(url, options) {
			upstreamRequest = { url, options };
			return new Response(
				JSON.stringify({
					iceServers: [
						{ urls: ["stun:stun.cloudflare.com:3478"] },
						{
							urls: ["turn:turn.cloudflare.com:3478?transport=udp"],
							username: "ephemeral-user",
							credential: "ephemeral-credential",
						},
					],
					upstreamInternalField: "must-not-pass-through",
				}),
				{ status: 200, headers: { "content-type": "application/json" } },
			);
		},
	};

	let response = await worker.fetch(
		new Request("https://example.com/turn-credentials"),
		env,
	);
	assert.equal(response.status, 405);
	assert.equal(response.headers.get("allow"), "POST");
	assert.equal(response.headers.has("access-control-allow-origin"), false);

	response = await worker.fetch(
		new Request("https://example.com/turn-credentials", {
			method: "POST",
			headers: { "cf-connecting-ip": "203.0.113.20" },
		}),
		env,
	);
	assert.equal(response.status, 200);
	assert.equal(response.headers.get("cache-control"), "no-store");
	assert.equal(response.headers.has("access-control-allow-origin"), false);

	const body = await response.json();
	assert.equal(Array.isArray(body.iceServers), true);
	assert.equal(body.ttl, 3600);
	assert.equal(body.upstreamInternalField, undefined);
	assert.equal(body.iceServers[1].credential, "ephemeral-credential");

	const upstreamBody = JSON.parse(upstreamRequest.options.body);
	assert.equal(upstreamBody.ttl, 3600);
	assert.match(upstreamBody.customIdentifier, /^[a-f0-9]{32}$/);
	assert.equal(upstreamBody.customIdentifier.includes("203.0.113.20"), false);
	assert.equal(
		upstreamRequest.options.headers.authorization,
		`Bearer ${env.TURN_API_TOKEN}`,
	);
});

test("TURN credential rate limiting fails closed without exposing secrets", async () => {
	let fetched = false;
	const env = {
		TURN_TOKEN_ID: "rate-token-id-must-not-leak",
		TURN_API_TOKEN: "rate-api-token-must-not-leak",
		TURN_RATE_LIMITER: {
			async limit() {
				return { success: false };
			},
		},
		async TURN_CREDENTIAL_FETCH() {
			fetched = true;
			throw new Error("should not fetch");
		},
	};

	const response = await worker.fetch(
		new Request("https://example.com/turn-credentials", {
			method: "POST",
			headers: { "cf-connecting-ip": "203.0.113.21" },
		}),
		env,
	);
	assert.equal(response.status, 429);
	assert.equal(fetched, false);
	const text = await response.text();
	assert.deepEqual(JSON.parse(text), { error: "rate_limited" });
	assert.equal(text.includes(env.TURN_TOKEN_ID), false);
	assert.equal(text.includes(env.TURN_API_TOKEN), false);
	assert.equal(response.headers.has("access-control-allow-origin"), false);
});

test("TURN credential issuer falls back to a bounded local limiter", async () => {
	let fetchCount = 0;
	const env = {
		TURN_TOKEN_ID: "fallback-token-id",
		TURN_API_TOKEN: "fallback-api-token",
		TURN_RATE_LIMITER: {
			async limit() {
				throw new Error("simulated binding outage");
			},
		},
		async TURN_CREDENTIAL_FETCH() {
			fetchCount += 1;
			return new Response(
				JSON.stringify({
					iceServers: [
						{
							urls: ["turn:turn.cloudflare.com:3478?transport=udp"],
							username: "ephemeral-user",
							credential: "ephemeral-credential",
						},
					],
				}),
				{ status: 200, headers: { "content-type": "application/json" } },
			);
		},
	};

	let response;
	for (let i = 0; i < 31; i++) {
		response = await worker.fetch(
			new Request("https://example.com/turn-credentials", {
				method: "POST",
				headers: { "cf-connecting-ip": "203.0.113.23" },
			}),
			env,
		);
	}

	assert.equal(response.status, 429);
	assert.equal(fetchCount, 30);
});

test("TURN credential issuer rejects payloads without an authenticated relay", async () => {
	const env = {
		TURN_TOKEN_ID: "validation-token-id",
		TURN_API_TOKEN: "validation-api-token",
		TURN_RATE_LIMITER: {
			async limit() {
				return { success: true };
			},
		},
		async TURN_CREDENTIAL_FETCH() {
			return new Response(
				JSON.stringify({
					iceServers: [{ urls: ["stun:stun.cloudflare.com:3478"] }],
				}),
				{ status: 200, headers: { "content-type": "application/json" } },
			);
		},
	};

	const response = await worker.fetch(
		new Request("https://example.com/turn-credentials", {
			method: "POST",
			headers: { "cf-connecting-ip": "203.0.113.24" },
		}),
		env,
	);
	assert.equal(response.status, 502);
	assert.deepEqual(await response.json(), { error: "turn_generate_failed" });
});

test("TURN credential upstream failures stay sanitized", async () => {
	const env = {
		TURN_TOKEN_ID: "failure-token-id-must-not-leak",
		TURN_API_TOKEN: "failure-api-token-must-not-leak",
		TURN_RATE_LIMITER: {
			async limit() {
				return { success: true };
			},
		},
		async TURN_CREDENTIAL_FETCH() {
			throw new Error("upstream detail must not leak");
		},
	};

	const response = await worker.fetch(
		new Request("https://example.com/turn-credentials", {
			method: "POST",
			headers: { "cf-connecting-ip": "203.0.113.22" },
		}),
		env,
	);
	assert.equal(response.status, 502);
	const text = await response.text();
	assert.deepEqual(JSON.parse(text), { error: "turn_generate_failed" });
	assert.equal(text.includes("upstream detail"), false);
	assert.equal(text.includes(env.TURN_API_TOKEN), false);
});

test("update notification endpoint returns disabled for current release", async () => {
	const env = updateEnv();
	const response = await worker.fetch(
		new Request("https://example.com/updates/latest?current=1.0.1"),
		env,
	);
	assert.equal(response.status, 200);
	const body = await response.json();
	assert.equal(body.enabled, false);
	assert.equal(body.test, false);
	assert.equal(body.latestVersion, "v1.0.1");
	assert.equal(
		body.releaseUrl,
		"https://github.com/artriy/Perfect-Comms/releases/tag/v1.0.1",
	);
	assert.equal(body.showEveryMainMenu, false);
});

test("update notification endpoint enables for v1.0.0 clients", async () => {
	const env = updateEnv();
	const response = await worker.fetch(
		new Request("https://example.com/updates/latest?current=1.0.0"),
		env,
	);
	assert.equal(response.status, 200);
	const body = await response.json();
	assert.equal(body.enabled, true);
	assert.equal(body.test, false);
	assert.equal(body.latestVersion, "v1.0.1");
	assert.equal(
		body.releaseUrl,
		"https://github.com/artriy/Perfect-Comms/releases/tag/v1.0.1",
	);
});

function updateEnv() {
	return {
		DB: new FakeDB(),
		UPDATE_RELEASE_FIXTURE_JSON: JSON.stringify({
			tag_name: "v1.0.1",
			html_url: "https://github.com/artriy/Perfect-Comms/releases/tag/v1.0.1",
		}),
	};
}

test("lobby publish list heartbeat delete flow", async () => {
	const env = { DB: new FakeDB() };
	const token = "0123456789abcdef";

	let response = await worker.fetch(
		jsonRequest("/lobbies", "POST", {
			id: "room-1",
			ownerToken: token,
			code: "abcxyz",
			region: "NA",
			language: "English",
			title: "A".repeat(80),
			host: "Host",
			players: 2,
			maxPlayers: 15,
			state: "Lobby",
			modVersion: "1.0.0",
			protocolVersion: 3,
			ttlSeconds: 999,
		}),
		env,
	);
	assert.equal(response.status, 200);
	const published = await response.json();
	assert.equal(published.id, "room-1");

	response = await worker.fetch(
		new Request("https://example.com/lobbies"),
		env,
	);
	const listed = await response.json();
	assert.equal(listed.lobbies.length, 1);
	assert.equal(listed.lobbies[0].code, "ABCXYZ");
	assert.equal(listed.lobbies[0].title.length, 40);
	assert.equal(listed.lobbies[0].protocolVersion, 3);

	response = await worker.fetch(
		jsonRequest("/lobbies/room-1/heartbeat", "POST", {
			ownerToken: token,
			players: 4,
			state: "InGame",
			ttlSeconds: 35,
		}),
		env,
	);
	assert.equal(response.status, 200);

	response = await worker.fetch(
		jsonRequest("/lobbies/room-1", "DELETE", { ownerToken: token }),
		env,
	);
	assert.equal(response.status, 200);

	response = await worker.fetch(
		new Request("https://example.com/lobbies"),
		env,
	);
	assert.equal((await response.json()).lobbies.length, 0);
});

test("worker rejects bad json, bad owner token, and token mismatch", async () => {
	const env = { DB: new FakeDB() };

	let response = await worker.fetch(
		new Request("https://example.com/lobbies", {
			method: "POST",
			headers: { "content-type": "text/plain" },
			body: "{}",
		}),
		env,
	);
	assert.equal(response.status, 400);
	assert.equal((await response.json()).error, "invalid_json");

	response = await worker.fetch(
		jsonRequest("/lobbies", "POST", {
			code: "ABCD",
			ownerToken: "short",
		}),
		env,
	);
	assert.equal(response.status, 400);
	assert.equal((await response.json()).error, "missing_owner_token");

	response = await worker.fetch(
		jsonRequest("/lobbies", "POST", {
			id: "room-2",
			code: "ABCD",
			ownerToken: "0123456789abcdef",
		}),
		env,
	);
	assert.equal(response.status, 200);

	response = await worker.fetch(
		jsonRequest("/lobbies", "POST", {
			id: "room-2",
			code: "ABCD",
			ownerToken: "fedcba9876543210",
		}),
		env,
	);
	assert.equal(response.status, 403);
	assert.equal((await response.json()).error, "owner_token_mismatch");
});

test("worker rejects path-unsafe lobby ids before publish", async () => {
	const env = { DB: new FakeDB() };
	const response = await worker.fetch(
		jsonRequest("/lobbies", "POST", {
			id: "bad/id",
			code: "ABCD",
			ownerToken: "0123456789abcdef",
		}),
		env,
	);

	assert.equal(response.status, 400);
	assert.equal((await response.json()).error, "invalid_lobby_id");
	assert.equal(env.DB.rows.size, 0);
});

test("ttl-only heartbeat preserves existing lobby metadata", async () => {
	const env = { DB: new FakeDB() };
	const token = "0123456789abcdef";

	let response = await worker.fetch(
		jsonRequest("/lobbies", "POST", {
			id: "room-safe_1",
			ownerToken: token,
			code: "ABCD",
			region: "NA",
			host: "Host",
			players: 4,
			maxPlayers: 15,
			state: "Lobby",
		}),
		env,
	);
	assert.equal(response.status, 200);

	response = await worker.fetch(
		jsonRequest("/lobbies/room-safe_1/heartbeat", "POST", {
			ownerToken: token,
			ttlSeconds: 35,
		}),
		env,
	);
	assert.equal(response.status, 200);

	response = await worker.fetch(new Request("https://example.com/lobbies"), env);
	const listed = await response.json();
	assert.equal(listed.lobbies[0].players, 4);
	assert.equal(listed.lobbies[0].maxPlayers, 15);
	assert.equal(listed.lobbies[0].state, "Lobby");
	assert.equal(listed.lobbies[0].host, "Host");
});

test("worker rate limits excessive mutations per client", async () => {
	const env = { DB: new FakeDB() };
	const token = "0123456789abcdef";
	let response;

	for (let i = 0; i < 61; i++) {
		response = await worker.fetch(
			jsonRequest(
				"/lobbies",
				"POST",
				{
					id: `rate-${i}`,
					ownerToken: token,
					code: `ABCD${i}`,
				},
				{ "cf-connecting-ip": "203.0.113.10" },
			),
			env,
		);
	}

	assert.equal(response.status, 429);
	assert.equal((await response.json()).error, "rate_limited");
});

test("worker hides internal server error details", async () => {
	const response = await worker.fetch(
		new Request("https://example.com/lobbies"),
		{
			DB: {
				prepare() {
					throw new Error("secret database path");
				},
			},
		},
	);

	assert.equal(response.status, 500);
	const body = await response.json();
	assert.equal(body.error, "server_error");
	assert.equal(body.detail, undefined);
});

function jsonRequest(path, method, body, headers = {}) {
	return new Request(`https://example.com${path}`, {
		method,
		headers: { "content-type": "application/json", ...headers },
		body: JSON.stringify(body),
	});
}

class FakeDB {
	constructor() {
		this.rows = new Map();
	}

	prepare(sql) {
		return new FakeStatement(this, sql);
	}
}

class FakeStatement {
	constructor(db, sql) {
		this.db = db;
		this.sql = sql;
		this.args = [];
	}

	bind(...args) {
		this.args = args;
		return this;
	}

	async all() {
		if (this.sql.includes("SELECT id, code")) {
			const [now, staleCutoff] = this.args;
			const results = [...this.db.rows.values()]
				.filter((row) => row.expiresAt > now && row.updatedAt > staleCutoff)
				.sort(
					(a, b) =>
						(b.state === "Lobby") - (a.state === "Lobby") ||
						b.updatedAt - a.updatedAt ||
						b.players - a.players,
				)
				.slice(0, 100)
				.map(({ ownerTokenHash, ...publicRow }) => publicRow);
			return { results };
		}
		throw new Error(`unsupported all SQL: ${this.sql}`);
	}

	async first() {
		const id = this.args[0];
		const row = this.db.rows.get(id);
		if (!row) return null;
		if (
			this.sql.includes(
				"SELECT ownerTokenHash, state, stateChangedAt, updatedAt",
			)
		) {
			return {
				ownerTokenHash: row.ownerTokenHash,
				state: row.state,
				stateChangedAt: row.stateChangedAt,
				updatedAt: row.updatedAt,
			};
		}
		if (this.sql.includes("SELECT ownerTokenHash"))
			return { ownerTokenHash: row.ownerTokenHash };
		throw new Error(`unsupported first SQL: ${this.sql}`);
	}

	async run() {
		if (
			this.sql.includes(
				"DELETE FROM lobbies WHERE expiresAt <= ? OR updatedAt <= ?",
			)
		) {
			const [now, staleCutoff] = this.args;
			for (const [id, row] of this.db.rows) {
				if (row.expiresAt <= now || row.updatedAt <= staleCutoff)
					this.db.rows.delete(id);
			}
			return {};
		}

		if (this.sql.includes("INSERT INTO lobbies")) {
			const [
				id,
				code,
				region,
				language,
				title,
				host,
				players,
				maxPlayers,
				state,
				stateChangedAt,
				modVersion,
				protocolVersion,
				ownerTokenHash,
				updatedAt,
				expiresAt,
			] = this.args;
			const previous = this.db.rows.get(id);
			if (previous && previous.ownerTokenHash !== ownerTokenHash)
				return { meta: { changes: 0 } };
			this.db.rows.set(id, {
				id,
				code,
				region,
				language,
				title,
				host,
				players,
				maxPlayers,
				state,
				stateChangedAt:
					previous?.state !== state
						? stateChangedAt
						: (previous?.stateChangedAt ?? stateChangedAt),
				modVersion,
				protocolVersion,
				ownerTokenHash,
				updatedAt,
				expiresAt,
			});
			return { meta: { changes: 1 } };
		}

		if (this.sql.includes("UPDATE lobbies SET")) {
			const id = this.args[this.args.length - 1];
			const row = this.db.rows.get(id);
			assert.ok(row, `missing row ${id}`);
			const assignments = this.sql
				.match(/SET([\s\S]+)WHERE id = \?/)[1]
				.split(",")
				.map((part) => part.trim())
				.filter(Boolean);
			let argIndex = 0;
			for (const assignment of assignments) {
				const field = assignment.split("=")[0].trim();
				row[field] = this.args[argIndex++];
			}
			return {};
		}

		if (this.sql.includes("DELETE FROM lobbies WHERE id = ?")) {
			this.db.rows.delete(this.args[0]);
			return {};
		}

		throw new Error(`unsupported run SQL: ${this.sql}`);
	}
}
