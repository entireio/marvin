import type { Database } from './database.js';
export const migrations = [
 { version: 1, statements: [
 `CREATE TABLE owners (id TEXT PRIMARY KEY, issuer TEXT NOT NULL, subject TEXT NOT NULL, name TEXT NOT NULL, entire_state TEXT NOT NULL DEFAULT 'disconnected', active_conversation TEXT, created_at BIGINT NOT NULL, UNIQUE(issuer,subject))`,
 `CREATE TABLE sessions (hash TEXT PRIMARY KEY, owner_id TEXT NOT NULL REFERENCES owners(id), csrf TEXT NOT NULL, expires_at BIGINT NOT NULL, authenticated_at BIGINT NOT NULL)`,
 `CREATE TABLE conversations (id TEXT PRIMARY KEY, owner_id TEXT NOT NULL REFERENCES owners(id), title TEXT NOT NULL, repository_id TEXT, summary TEXT NOT NULL DEFAULT '', summary_through INTEGER NOT NULL DEFAULT 0, updated_at BIGINT NOT NULL)`,
 `CREATE TABLE turns (id TEXT PRIMARY KEY, conversation_id TEXT NOT NULL REFERENCES conversations(id), owner_id TEXT NOT NULL REFERENCES owners(id), ordinal INTEGER NOT NULL, user_text TEXT NOT NULL, assistant_text TEXT NOT NULL DEFAULT '', status TEXT NOT NULL CHECK(status IN ('running','completed','cancelled','failed')), surface TEXT NOT NULL CHECK(surface IN ('web_text','web_voice','body_voice')), route_id TEXT NOT NULL, repository_id TEXT, created_at BIGINT NOT NULL, lease_until BIGINT NOT NULL, worker_id TEXT NOT NULL, cards TEXT NOT NULL DEFAULT '[]', UNIQUE(conversation_id,ordinal))`,
 `CREATE UNIQUE INDEX one_generation_per_conversation ON turns(conversation_id) WHERE status='running'`,
 `CREATE TABLE events (interaction_id TEXT NOT NULL REFERENCES turns(id), seq INTEGER NOT NULL, event_json TEXT NOT NULL, PRIMARY KEY(interaction_id,seq))`,
 `CREATE TABLE tool_invocations (id TEXT PRIMARY KEY, interaction_id TEXT NOT NULL REFERENCES turns(id), name TEXT NOT NULL, args_json TEXT NOT NULL, result_json TEXT NOT NULL, status TEXT NOT NULL, created_at BIGINT NOT NULL)`,
 `CREATE TABLE body_slots (owner_id TEXT PRIMARY KEY REFERENCES owners(id), device_id TEXT NOT NULL UNIQUE, enrollment_id TEXT NOT NULL UNIQUE, state TEXT NOT NULL CHECK(state IN ('reserved','linked')), expires_at BIGINT NOT NULL, epoch INTEGER NOT NULL DEFAULT 1, network TEXT, simulated INTEGER NOT NULL DEFAULT 0)`,
 `CREATE TABLE device_epochs (device_id TEXT PRIMARY KEY, epoch INTEGER NOT NULL)`,
 `CREATE TABLE audit_events (id TEXT PRIMARY KEY, owner_id TEXT NOT NULL REFERENCES owners(id), kind TEXT NOT NULL, created_at BIGINT NOT NULL)`,
 `CREATE TABLE identity_flows (hash TEXT PRIMARY KEY, state TEXT NOT NULL, verifier TEXT NOT NULL, nonce TEXT NOT NULL, return_to TEXT NOT NULL, expires_at BIGINT NOT NULL)`
 ]},
 { version: 2, statements: [
 `CREATE INDEX conversation_history ON conversations(owner_id,updated_at)`,
 `CREATE INDEX turn_history ON turns(conversation_id,ordinal)`,
 `CREATE INDEX session_expiry ON sessions(expires_at)`
 ]}
 ,{ version: 3, statements: [
 `CREATE TABLE device_credentials (hash TEXT PRIMARY KEY, device_id TEXT NOT NULL, owner_id TEXT NOT NULL REFERENCES owners(id), epoch INTEGER NOT NULL, expires_at BIGINT NOT NULL)`,
 `CREATE INDEX device_credential_lookup ON device_credentials(device_id,epoch)`,
 `CREATE TABLE device_commands (id TEXT PRIMARY KEY, device_id TEXT NOT NULL, owner_id TEXT NOT NULL REFERENCES owners(id), epoch INTEGER NOT NULL, boot_id TEXT NOT NULL, action TEXT NOT NULL, args_json TEXT NOT NULL, deadline BIGINT NOT NULL, state TEXT NOT NULL, created_at BIGINT NOT NULL)`,
 `CREATE INDEX device_command_pending ON device_commands(device_id,state,deadline)`
 ]}
 ,{version:4,statements:[
 `CREATE TABLE device_identities (device_id TEXT PRIMARY KEY, public_key TEXT NOT NULL, registered_at BIGINT NOT NULL)`,
 `CREATE TABLE enrollment_tickets (id TEXT PRIMARY KEY, owner_id TEXT NOT NULL REFERENCES owners(id), device_id TEXT NOT NULL REFERENCES device_identities(device_id), nonce TEXT NOT NULL, operation TEXT NOT NULL, epoch INTEGER NOT NULL, ticket_hash TEXT NOT NULL, expires_at BIGINT NOT NULL, receipt TEXT, network TEXT, UNIQUE(device_id,nonce))`
 ]}
 ,{version:5,statements:[
 `ALTER TABLE owners ADD COLUMN retention_days INTEGER`,
 `CREATE INDEX owner_turn_retention ON turns(owner_id,created_at)`
 ]}
];
export async function migrate(db: Database, target=migrations.at(-1)!.version) {
 await db.transaction(async tx => {
  if(db.kind==='postgres') await tx.query('SELECT pg_advisory_xact_lock(71263381)');
  await tx.query('CREATE TABLE IF NOT EXISTS schema_migrations (version INTEGER PRIMARY KEY)');
  const applied = new Set((await tx.query<{version:number}>('SELECT version FROM schema_migrations')).map(x=>x.version));
  for(const m of migrations) if(m.version<=target && !applied.has(m.version)) { for(const sql of m.statements) await tx.query(sql); await tx.query('INSERT INTO schema_migrations(version) VALUES (?)',[m.version]); }
 });
}
