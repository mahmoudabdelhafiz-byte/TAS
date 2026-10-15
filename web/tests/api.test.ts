import assert from 'node:assert/strict';
import test from 'node:test';
import { ApiClient, ApiError, type Session, type SessionStore } from '../src/lib/api.ts';

const user = { userId: 1, email: 'd@x', fullName: 'D', role: 'CompanyUser', companyId: 7, companyName: 'Al Noor', terminalId: null, preferredLanguage: 'ar' } as const;

function memoryStore(initial: Session | null = null): SessionStore & { value: Session | null } {
  return { value: initial, load() { return this.value; }, save(s) { this.value = s; } };
}

const json = (status: number, body: unknown) =>
  new Response(JSON.stringify(body), { status, headers: { 'Content-Type': 'application/json' } });

test('an expired access token is refreshed once and the request retried', async () => {
  const store = memoryStore({ accessToken: 'old', refreshToken: 'r1', user });
  const calls: string[] = [];
  const api = new ApiClient(store, async (url, init) => {
    const auth = (init?.headers as Record<string, string> | undefined)?.Authorization ?? '';
    calls.push(`${url} ${auth}`);
    if (url === '/api/auth/refresh') return json(200, { accessToken: 'new', refreshToken: 'r2', accessTokenExpiresAt: '', refreshTokenExpiresAt: '', user });
    return auth === 'Bearer new' ? json(200, [{ ok: 1 }]) : new Response(null, { status: 401 });
  });

  const [a, b] = await Promise.all([api.get<unknown[]>('/api/x'), api.get<unknown[]>('/api/y')]);
  assert.deepEqual(a, [{ ok: 1 }]);
  assert.deepEqual(b, [{ ok: 1 }]);
  assert.equal(calls.filter(c => c.startsWith('/api/auth/refresh')).length, 1, 'parallel 401s share one refresh');
  assert.equal(store.value?.refreshToken, 'r2');
});

test('a refused refresh ends the session', async () => {
  const store = memoryStore({ accessToken: 'old', refreshToken: 'r1', user });
  let ended = false;
  const api = new ApiClient(store, async () => new Response(null, { status: 401 }));
  api.onSessionEnded = () => { ended = true; };

  await assert.rejects(api.get('/api/x'), (e: unknown) => e instanceof ApiError && e.status === 401);
  assert.ok(ended);
  assert.equal(store.value, null);
  assert.equal(api.user, null);
});

test('problem details become readable errors with booking checks', async () => {
  const api = new ApiClient(memoryStore({ accessToken: 'a', refreshToken: 'r', user }), async () => json(422, {
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
  assert.equal((await api.login('d@x', 'pw')).fullName, 'D');
  assert.equal(store.value?.accessToken, 'a');
  await api.logout();
  assert.equal(store.value, null);
});
