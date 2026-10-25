// Deployment regression (spec S4/S6, task T7): the compose file is the only documented way to run the
// API, so it must forward the mandatory notifier operator id. Without it the enabled notifier cannot
// start (NtfyOptions validation) or, worse, silently stays at its fail-closed default.
//
// The file is read as plain text on purpose: this check never invokes `docker compose config`, because
// that resolves the real `.env` and would print secrets. It only asserts the literal interpolation and
// the entries that already existed around it.
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import test from 'node:test';

const COMPOSE_URL = new URL('../docker-compose.yml', import.meta.url);
const composeSource = readFileSync(COMPOSE_URL, 'utf8');

// Minimal reader: top-level `services:` map, then each service's `environment:` list entries as
// `KEY=value` strings. Enough for this file's shape and it does not need a YAML dependency.
function parseEnvironments(source) {
  const services = new Map();
  let section = null;
  let service = null;
  let inEnvironment = false;

  for (const raw of source.split(/\r?\n/)) {
    const line = raw.replace(/\s+$/, '');
    if (line.trim() === '' || line.trimStart().startsWith('#')) continue;

    const indent = line.length - line.trimStart().length;
    if (indent === 0) {
      section = line.endsWith(':') ? line.slice(0, -1).trim() : null;
      service = null;
      inEnvironment = false;
      continue;
    }
    if (section !== 'services') continue;

    if (indent === 2) {
      const match = /^([A-Za-z0-9_.-]+):\s*$/.exec(line.trim());
      service = match ? match[1] : null;
      inEnvironment = false;
      if (service !== null) services.set(service, []);
      continue;
    }
    if (service === null) continue;

    if (indent === 4) {
      inEnvironment = line.trim() === 'environment:';
      continue;
    }
    if (indent === 6 && inEnvironment && line.trimStart().startsWith('- ')) {
      services.get(service).push(line.trimStart().slice(2).trim());
    }
  }

  return services;
}

const services = parseEnvironments(composeSource);
const apiEnvironment = services.get('api');

function entryIndex(entries, key) {
  return entries.findIndex((entry) => entry.startsWith(`${key}=`));
}

test('the api service environment is readable from docker-compose.yml', () => {
  assert.ok(apiEnvironment, 'docker-compose.yml must define an `api` service with an environment list');
  assert.ok(apiEnvironment.length > 0, 'the api service must declare environment entries');
});

test('api forwards the operator id with an exact single-user default of 1', () => {
  const expected = 'Ntfy__UserId=${Ntfy__UserId:-1}';
  const occurrences = apiEnvironment.filter((entry) => entry.startsWith('Ntfy__UserId='));

  assert.deepEqual(occurrences, [expected], `api must forward exactly "${expected}" once`);

  const value = expected.slice('Ntfy__UserId='.length);
  assert.match(value, /^\$\{Ntfy__UserId:-1\}$/, 'the interpolation and its default must be literal');
  assert.equal(
    value.slice(value.indexOf(':-') + 2, -1),
    '1',
    'the default is the single-user account 1, so a deployment needs no manual id; an explicit id still overrides it',
  );
});

test('api forwards the operator id next to the notifier master switch', () => {
  const enabledIndex = entryIndex(apiEnvironment, 'Ntfy__Enabled');
  assert.notEqual(enabledIndex, -1, 'api must still forward Ntfy__Enabled');

  const userIdIndex = entryIndex(apiEnvironment, 'Ntfy__UserId');
  assert.notEqual(userIdIndex, -1, 'api must forward Ntfy__UserId');
  assert.equal(
    userIdIndex,
    enabledIndex + 1,
    'Ntfy__UserId must sit immediately after Ntfy__Enabled so the pair is read together',
  );
});

test('the existing notifier entries are preserved unchanged', () => {
  assert.deepEqual(
    [
      apiEnvironment[entryIndex(apiEnvironment, 'Ntfy__Enabled')],
      apiEnvironment[entryIndex(apiEnvironment, 'Ntfy__BaseUrl')],
      apiEnvironment[entryIndex(apiEnvironment, 'Ntfy__Topic')],
      apiEnvironment[entryIndex(apiEnvironment, 'Ntfy__AuthToken')],
    ],
    [
      'Ntfy__Enabled=${Ntfy__Enabled:-false}',
      'Ntfy__BaseUrl=${Ntfy__BaseUrl:-https://ntfy.amitzi.xyz/}',
      'Ntfy__Topic=${Ntfy__Topic:-Deals}',
      'Ntfy__AuthToken=${Ntfy__AuthToken:-}',
    ],
  );
});

test('the frontend service does not receive the notifier operator', () => {
  const frontendEnvironment = services.get('frontend') ?? [];
  const leaks = frontendEnvironment.filter((entry) => entry.startsWith('Ntfy__'));

  assert.deepEqual(leaks, [], 'the BFF does not read Ntfy options, so it must not forward them');
});
