import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { api, ApiError, buildPath, onSessionEnded, problemMessage, refreshSession, resetClientState, signIn } from './client';

const profile = { userId: 1, email: 'k@alnoor.example', fullName: 'Khalid', role: 'CompanyAdmin', companyId: 1, companyName: 'Al Noor', terminalId: null, preferredLanguage: 'ar' };
const tokens = (n: number) => ({ accessToken: `access-${n}`, accessTokenExpiresAt: '', refreshToken: `refresh-${n}`, refreshTokenExpiresAt: '', user: profile });

function json(status: number, body: unknown) {
  return new Response(JSON.stringify(body), { status, headers: { 'Content-Type': 'application/json' } });
}

let fetchMock: ReturnType<typeof vi.fn>;

beforeEach(() => {
  resetClientState();
  fetchMock = vi.fn();
  vi.stubGlobal('fetch', fetchMock);
});

afterEach(() => vi.unstubAllGlobals());

const calls = (path: string) => fetchMock.mock.calls.filter(([url]) => url === path);
const auth = (i: number) => new Headers(fetchMock.mock.calls[i]![1].headers).get('Authorization');

describe('api client', () => {
  it('signs in, keeps the access token in memory and the refresh token in storage', async () => {
    fetchMock.mockResolvedValueOnce(json(200, tokens(1))).mockResolvedValueOnce(json(200, [{ terminalId: 1 }]));

    expect((await signIn('k@alnoor.example', 'pw')).fullName).toBe('Khalid');
    expect(localStorage.getItem('tas.refresh')).toBe('refresh-1');
    await api('/api/terminals');
    expect(auth(1)).toBe('Bearer access-1');
  });

  it('renews an expired access token once and repeats the request', async () => {
    localStorage.setItem('tas.refresh', 'refresh-1');
    fetchMock.mockImplementation(async (url: string, init: RequestInit) => {
      if (url === '/api/auth/refresh') return json(200, tokens(2));
      const authorized = new Headers(init.headers).get('Authorization') === 'Bearer access-2';
      return authorized ? json(200, { ok: true }) : json(401, {});
    });

    // Three requests fail at the same moment: only one refresh.
    const results = await Promise.all([api('/api/a'), api('/api/b'), api('/api/c')]);
    expect(results).toEqual([{ ok: true }, { ok: true }, { ok: true }]);
    expect(calls('/api/auth/refresh')).toHaveLength(1);
    expect(JSON.parse(calls('/api/auth/refresh')[0]![1].body)).toEqual({ refreshToken: 'refresh-1' });
    expect(localStorage.getItem('tas.refresh')).toBe('refresh-2');
  });

  it('ends the session when it can no longer be renewed', async () => {
    localStorage.setItem('tas.refresh', 'stolen-or-expired');
    const ended = vi.fn();
    onSessionEnded(ended);
    fetchMock.mockResolvedValue(json(401, { detail: 'Session expired.' }));

    await expect(api('/api/terminals')).rejects.toMatchObject({ status: 401 });
    expect(ended).toHaveBeenCalledOnce();
    expect(localStorage.getItem('tas.refresh')).toBeNull();
    expect(await refreshSession()).toBeNull();
  });

  it('reports the server’s reason', async () => {
    fetchMock.mockResolvedValueOnce(json(409, { title: 'No capacity', detail: 'The slot is full.' }));
    const error = await api('/api/x', { anonymous: true }).catch((e: unknown) => e);
    expect(error).toBeInstanceOf(ApiError);
    expect((error as ApiError).message).toBe('The slot is full.');
    expect(problemMessage({ errors: ['A', 'B'] }, 'x')).toBe('A B');
    expect(problemMessage({ errors: { name: ['Required.'] } }, 'x')).toBe('Required.');
    expect(problemMessage(undefined, 'fallback')).toBe('fallback');
  });

  it('builds query strings without empty values', () => {
    expect(buildPath('/api/x', { date: '2026-10-20', companyId: null, q: '' })).toBe('/api/x?date=2026-10-20');
    expect(buildPath('/api/x')).toBe('/api/x');
  });
});
