import test from 'node:test';
import assert from 'node:assert/strict';
import { readFile } from 'node:fs/promises';
import { createRefreshCoordinator } from '../lib/auth/refresh-coordinator.ts';

test('simultaneous callers rotate once and share identical result', async () => {
  const coordinate = createRefreshCoordinator();
  let calls = 0;
  let release;
  const gate = new Promise(resolve => { release = resolve; });
  const rotate = async () => { calls++; await gate; return { token: 'synthetic-new' }; };
  const first = coordinate('synthetic-old', rotate);
  const second = coordinate('synthetic-old', rotate);
  await Promise.resolve();
  release();
  const [a, b] = await Promise.all([first, second]);
  assert.equal(calls, 1);
  assert.strictEqual(a, b);
  assert.strictEqual(await coordinate('synthetic-old', rotate), a);
  assert.equal(calls, 1);
});

test('failures and exceptions are not cached', async () => {
  const coordinate = createRefreshCoordinator();
  assert.equal(await coordinate('x', async () => null), null);
  await assert.rejects(coordinate('x', async () => { throw new Error('synthetic'); }));
  assert.equal(await coordinate('x', async () => 'ok'), 'ok');
});

test('TTL expires and capacity stays bounded without evicting in-flight work', async () => {
  let now = 0;
  const coordinate = createRefreshCoordinator({ ttlMs: 5, capacity: 1, now: () => now });
  let release;
  const pending = coordinate('a', () => new Promise(resolve => { release = resolve; }));
  await assert.rejects(coordinate('b', async () => 'b'), /temporarily unavailable/);
  release('a');
  assert.equal(await pending, 'a');
  assert.equal(await coordinate('b', async () => 'b'), 'b');
  let calls = 0;
  now = 5;
  assert.equal(await coordinate('b', async () => { calls++; return 'new-b'; }), 'new-b');
  assert.equal(calls, 1);
});

test('both entrypoints share refreshSession and coordinator stores only digests as keys, no logs', async () => {
  const auto = await readFile(new URL('../lib/auth/api-session.ts', import.meta.url), 'utf8');
  const manual = await readFile(new URL('../app/api/auth/refresh/route.ts', import.meta.url), 'utf8');
  const coordinator = await readFile(new URL('../lib/auth/refresh-coordinator.ts', import.meta.url), 'utf8');
  for (const source of [auto, manual]) {
    assert.match(source, /await refreshSession\(session\)/);
    assert.doesNotMatch(source, /decodeJwt|JSON.stringify\(\{ refreshToken/);
  }
  assert.doesNotMatch(manual, /refreshData|\{ traceId, userId \}/);
  assert.match(coordinator, /createHash\("sha256"\)/);
  assert.doesNotMatch(coordinator, /console\.|process\.env/);
});

test('proxy parse failure preserves refreshed session and cookie', async () => {
  const source = await readFile(new URL('../lib/bff/proxy.ts', import.meta.url), 'utf8');
  assert.match(source, /catch\s*\{[\s\S]*?attachSessionCookie\(out, updatedSession, session\)[\s\S]*?return \{ response: out, session: updatedSession \}/);
});
