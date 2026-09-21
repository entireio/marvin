import { DatabaseSync } from 'node:sqlite';
import { Pool, type PoolClient } from 'pg';
import { mkdirSync } from 'node:fs';
import { dirname } from 'node:path';
export interface Sql { query<T = Record<string, unknown>>(sql: string, params?: unknown[]): Promise<T[]>; }
export interface Database extends Sql { kind: 'sqlite' | 'postgres'; transaction<T>(fn: (tx: Sql) => Promise<T>): Promise<T>; close(): Promise<void>; }
class Mutex { private tail = Promise.resolve(); async run<T>(fn: () => Promise<T>): Promise<T> { const prev = this.tail; let release!: () => void; this.tail = new Promise<void>(r => release = r); await prev; try { return await fn(); } finally { release(); } } }
export function sqliteDatabase(path = ':memory:'): Database {
 if (path !== ':memory:') mkdirSync(dirname(path), { recursive: true, mode: 0o700 });
 const db = new DatabaseSync(path); db.exec('PRAGMA foreign_keys=ON; PRAGMA journal_mode=WAL; PRAGMA busy_timeout=5000;');
 const lock = new Mutex();
 const raw: Sql = { async query<T>(sql: string, params: unknown[] = []) { const stmt = db.prepare(sql); return stmt.all(...params as (string|number|null)[]) as T[]; } };
 return { kind: 'sqlite', query: <T>(s: string,p?: unknown[]) => lock.run(() => raw.query<T>(s,p)), transaction: fn => lock.run(async () => { db.exec('BEGIN IMMEDIATE'); try { const result = await fn(raw); db.exec('COMMIT'); return result; } catch (e) { db.exec('ROLLBACK'); throw e; } }), async close() { db.close(); } };
}
function pgSql(client: Pool | PoolClient): Sql { return { async query<T>(sql: string, params: unknown[] = []) { let i=0; const result = await client.query(sql.replace(/\?/g, () => '$'+(++i)), params); return result.rows as T[]; } }; }
export function postgresDatabase(url: string): Database {
 const pool = new Pool({ connectionString: url, max: 8 });
 return { kind: 'postgres', ...pgSql(pool), async transaction(fn) { const c=await pool.connect(); try { await c.query('BEGIN'); const result=await fn(pgSql(c)); await c.query('COMMIT'); return result; } catch(e) { await c.query('ROLLBACK'); throw e; } finally { c.release(); } }, async close() { await pool.end(); } };
}
