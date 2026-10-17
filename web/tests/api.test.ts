import assert from 'node:assert/strict';
import test from 'node:test';
import { ApiClient, ApiError, type SessionStore } from '../src/lib/api.ts';

const user = { userId: 1, email: 'd@x', fullName: 'D', role: 'CompanyUser', companyId: 7, companyName: 'Al Noor', terminalId: null, preferredLanguage: 'ar' } as const;

type Stored = ReturnType<SessionStore['load']>;

function memoryStore(initial: Stored = null): SessionStore & { value: Stored } {
  return { value: initial, load() { return this.value; }, save(s) { this.value = s; } };
}

const json = (status: number, body: unknown) =>
  new Response(JSON.stringify(body), { status, headers: { 'Content-Type': 'application/json' } });

test('after a reload the cookie gives a new access token, once for parallel requests; no token is stored', async () => {
  const store = memoryStore({ user });
  const calls: string[] = [];
  const api = new ApiClient(store, async (url, init) => {
    const headers = (init?.headers as Record<string, string> | undefined) ?? {};
    const auth = headers.Authorization ?? '';
    calls.push(`${url} ${auth}`);
    if (url === '/api/auth/refresh') {
      assert.equal(headers['X-Yardsyncs-Session'], 'cookie');
      assert.equal(init?.body, '{}');
      return json(200, { accessToken: 'new', refreshToken: '', accessTokenExpiresAt: '', refreshTokenExpiresAt: '', user });
    }
    return auth === 'Bearer new' ? json(200, [{ ok: 1 }]) : new Response(null, { status: 401 });
  });

  const [a, b] = await Promise.all([api.get<unknown[]>('/api/x'), api.get<unknown[]>('/api/y')]);
  assert.deepEqual(a, [{ ok: 1 }]);
  assert.deepEqual(b, [{ ok: 1 }]);
  assert.equal(calls.filter(c => c.startsWith('/api/auth/refresh')).length, 1, 'parallel 401s share one refresh');
  assert.deepEqual(store.value, { user });
});

test('a session from the older version moves its refresh token to the cookie once', async () => {
  const store = memoryStore({ user, refreshToken: 'legacy' });
  const bodies: string[] = [];
  const api = new ApiClient(store, async (url, init) => {
    if (url === '/api/auth/refresh') {
      bodies.push(String(init?.body));
      return json(200, { accessToken: 'new', refreshToken: '', accessTokenExpiresAt: '', refreshTokenExpiresAt: '', user });
    }
    return (init?.headers as Record<string, string>).Authorization === 'Bearer new' ? json(200, {}) : new Response(null, { status: 401 });
  });
  assert.deepEqual(store.value, { user }, 'the token leaves storage at once');
  await api.get('/api/x');
  assert.deepEqual(bodies, ['{"refreshToken":"legacy"}']);
});

test('two tabs refreshing at once: the losing one tries again with the new cookie', async () => {
  let refreshes = 0;
  const api = new ApiClient(memoryStore({ user }), async (url, init) => {
    if (url === '/api/auth/refresh') {
      refreshes++;
      return refreshes === 1 ? new Response(null, { status: 401 }) : json(200, { accessToken: 'new', refreshToken: '', accessTokenExpiresAt: '', refreshTokenExpiresAt: '', user });
    }
    return (init?.headers as Record<string, string>).Authorization === 'Bearer new' ? json(200, { ok: true }) : new Response(null, { status: 401 });
  });
  api.retryDelayMs = 0;
  assert.deepEqual(await api.get('/api/x'), { ok: true });
  assert.equal(refreshes, 2);
});

test('a refused refresh ends the session', async () => {
  const store = memoryStore({ user });
  let ended = false;
  const api = new ApiClient(store, async () => new Response(null, { status: 401 }));
  api.retryDelayMs = 0;
  api.onSessionEnded = () => { ended = true; };

  await assert.rejects(api.get('/api/x'), (e: unknown) => e instanceof ApiError && e.status === 401);
  assert.ok(ended);
  assert.equal(store.value, null);
  assert.equal(api.user, null);
});

test('problem details become readable errors with booking checks', async () => {
  const api = new ApiClient(memoryStore({ user }), async () => json(422, {
    title: 'Not cleared for booking',
    detail: 'Item 1: customs hold.',
    errors: ['Item 1: customs hold.'],
    checks: [{ item: 1, type: 'CustomsRelease', result: 'Fail', message: 'Customs hold', source: 'TOS' }],
  }));

  const error = await api.post('/api/terminals/1/appointments', {}).catch((e: unknown) => e);
  assert.ok(error instanceof ApiError);
  assert.equal(error.status, 422);
  assert.equal(error.message, 'Item 1: customs hold.');
  assert.equal(error.checks[0].message, 'Customs hold');
});

test('login stores the session; logout clears it even if the server is down', async () => {
  const store = memoryStore();
  const api = new ApiClient(store, async url => {
    if (url === '/api/auth/login') return json(200, { accessToken: 'a', refreshToken: 'r', accessTokenExpiresAt: '', refreshTokenExpiresAt: '', user });
    throw new TypeError('offline');
  });
  const outcome = await api.login('d@x', 'pw');
  assert.ok('user' in outcome && outcome.user.fullName === 'D');
  assert.deepEqual(store.value, { user }, 'only who is signed in is stored');
  await api.logout();
  assert.equal(store.value, null);
});

test('two-step sign-in: the password gives a challenge, the code signs in', async () => {
  const store = memoryStore();
  const api = new ApiClient(store, async (url, init) => {
    if (url === '/api/auth/login') return json(200, { secondStep: true, challenge: 'c1' });
    if (url === '/api/auth/login/code') {
      assert.deepEqual(JSON.parse(String(init?.body)), { challenge: 'c1', code: '123456' });
      return json(200, { accessToken: 'a', refreshToken: '', accessTokenExpiresAt: '', refreshTokenExpiresAt: '', user });
    }
    return new Response(null, { status: 404 });
  });
  const outcome = await api.login('d@x', 'pw');
  assert.deepEqual(outcome, { challenge: 'c1' });
  assert.equal(store.value, null, 'not signed in after the password alone');
  assert.equal((await api.loginCode('c1', '123456')).fullName, 'D');
  assert.deepEqual(store.value, { user });
});
