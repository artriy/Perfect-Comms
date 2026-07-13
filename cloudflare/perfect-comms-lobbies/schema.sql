CREATE TABLE IF NOT EXISTS lobbies (
  id TEXT PRIMARY KEY,
  code TEXT NOT NULL,
  region TEXT NOT NULL,
  language TEXT NOT NULL DEFAULT 'English',
  title TEXT NOT NULL,
  host TEXT NOT NULL,
  players INTEGER NOT NULL,
  maxPlayers INTEGER NOT NULL,
  state TEXT NOT NULL,
  stateChangedAt INTEGER NOT NULL DEFAULT 0,
  modVersion TEXT NOT NULL,
  protocolVersion INTEGER NOT NULL,
  ownerTokenHash TEXT NOT NULL,
  updatedAt INTEGER NOT NULL,
  expiresAt INTEGER NOT NULL
);

CREATE INDEX IF NOT EXISTS idx_lobbies_expires ON lobbies(expiresAt);
CREATE INDEX IF NOT EXISTS idx_lobbies_state ON lobbies(state);
