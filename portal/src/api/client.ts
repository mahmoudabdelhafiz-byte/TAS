import type { Problem, TokenResponse, UserProfile } from './types';

/**
 * Talks to the TAS API.
 *
 * Sessions: the short-lived access token (30 min) is kept in memory only; the refresh token (14 days) is kept in
 * localStorage so a reopened tab stays signed in. The API rotates the refresh token on every use and treats
 * re-use of an old one as theft (it signs the user out everywhere). Several open tabs share the same refresh
 * token, so refreshes are serialised across tabs with the Web Locks API and always use the newest token.
 */

const REFRESH_KEY = 'tas.refresh';
const LOCK_NAME = 'tas-refresh';

export class ApiError extends Error {
  constructor(
    public readonly status: number,
    message: string,
    public readonly problem?: Problem,
  ) {
    super(message);
    this.name = 'ApiError';
  }
}

let accessToken: string | null = null;
let refreshing: Promise<UserProfile | null> | null = null;
const endedListeners = new Set<() => void>();

function storedRefreshToken(): string | null {
  try {
    return localStorage.getItem(REFRESH_KEY);
  } catch {
    return null;
  }
}

function store(tokens: TokenResponse) {
  accessToken = tokens.accessToken;
  try {
    localStorage.setItem(REFRESH_KEY, tokens.refreshToken);
  } catch {
    // private mode: signed in for this tab only
  }
}

function clear() {
  accessToken = null;
  try {
    localStorage.removeItem(REFRESH_KEY);
  } catch {
    // ignore
  }
}

/** Called when the session can't be renewed any more (expired, revoked, account deactivated). */
export function onSessionEnded(listener: () => void): () => void {
  endedListeners.add(listener);
  return () => endedListeners.delete(listener);
}

export function hasStoredSession(): boolean {
  return storedRefreshToken() !== null;
}

/** For tests. */
export function resetClientState() {
  accessToken = null;
  refreshing = null;
  endedListeners.clear();
}

async function readProblem(response: Response): Promise<Problem | undefined> {
  const type = response.headers.get('content-type') ?? '';
  if (!type.includes('json')) return undefined;
  try {
    return (await response.json()) as Problem;
  } catch {
    return undefined;
  }
}

/** One human-readable sentence from a problem response. */
export function problemMessage(problem: Problem | undefined, fallback: string): string {
  if (!problem) return fallback;
  if (Array.isArray(problem.errors) && problem.errors.length > 0) return problem.errors.join(' ');
  if (problem.errors && !Array.isArray(problem.errors)) {
    const all = Object.values(problem.errors).flat();
    if (all.length > 0) return all.join(' ');
  }
  return problem.detail ?? problem.title ?? fallback;
}

async function send(path: string, init: RequestInit, withAuth: boolean): Promise<Response> {
  const headers = new Headers(init.headers);
  if (withAuth && accessToken) headers.set('Authorization', `Bearer ${accessToken}`);
  // A FormData body sets its own multipart content type (with the boundary).
  if (init.body && !(init.body instanceof FormData) && !headers.has('Content-Type')) headers.set('Content-Type', 'application/json');
  headers.set('Accept', 'application/json');
  return fetch(path, { ...init, headers });
}

async function doRefresh(): Promise<UserProfile | null> {
  const token = storedRefreshToken(); // read inside the lock: another tab may have rotated it a moment ago
  if (!token) return null;
  const response = await send('/api/auth/refresh', { method: 'POST', body: JSON.stringify({ refreshToken: token }) }, false);
  if (!response.ok) {
    if (response.status === 401) clear();
    return null;
  }
  const tokens = (await response.json()) as TokenResponse;
  store(tokens);
  return tokens.user;
}

/**
 * Gets a fresh access token with the stored refresh token. One refresh at a time per tab, and one at a time
 * across tabs. Returns the profile, or null when there is no valid session.
 */
export function refreshSession(): Promise<UserProfile | null> {
  refreshing ??= (async () => {
    try {
      const locks = typeof navigator !== 'undefined' ? navigator.locks : undefined;
      return locks ? await locks.request(LOCK_NAME, doRefresh) : await doRefresh();
    } finally {
      refreshing = null;
    }
  })();
  return refreshing;
}

export interface RequestOptions {
  method?: 'GET' | 'POST' | 'PUT' | 'DELETE';
  body?: unknown;
  query?: Record<string, string | number | boolean | null | undefined>;
  /** Don't send the access token and don't try to refresh (sign-in, password links). */
  anonymous?: boolean;
  /** A multipart form (file upload) instead of a JSON body. */
  form?: FormData;
}

export function buildPath(path: string, query?: RequestOptions['query']): string {
  if (!query) return path;
  const params = new URLSearchParams();
  for (const [key, value] of Object.entries(query)) {
    if (value !== null && value !== undefined && value !== '') params.set(key, String(value));
  }
  const text = params.toString();
  return text ? `${path}?${text}` : path;
}

/** Calls the API; throws ApiError with the server's explanation on failure. */
export async function api<T>(path: string, options: RequestOptions = {}): Promise<T> {
  const url = buildPath(path, options.query);
  const init: RequestInit = {
    method: options.method ?? (options.body === undefined && !options.form ? 'GET' : 'POST'),
    body: options.form ?? (options.body === undefined ? undefined : JSON.stringify(options.body)),
  };

  let response = await send(url, init, !options.anonymous);

  if (response.status === 401 && !options.anonymous) {
    const profile = await refreshSession();
    if (profile) {
      response = await send(url, init, true);
    }
    if (!profile || response.status === 401) {
      clear();
      endedListeners.forEach((listener) => listener());
      throw new ApiError(401, 'Session ended', await readProblem(response));
    }
  }

  if (!response.ok) {
    const problem = await readProblem(response);
    throw new ApiError(response.status, problemMessage(problem, `${response.status} ${response.statusText}`), problem);
  }

  if (response.status === 204 || response.headers.get('content-length') === '0') {
    return undefined as T;
  }
  const type = response.headers.get('content-type') ?? '';
  return (type.includes('json') ? await response.json() : undefined) as T;
}

/** Downloads a file the API produces (with the session) and hands it to the browser as a download. */
export async function download(path: string, fallbackName: string): Promise<void> {
  let response = await send(path, { method: 'GET' }, true);
  if (response.status === 401 && (await refreshSession())) response = await send(path, { method: 'GET' }, true);
  if (!response.ok) throw new ApiError(response.status, problemMessage(await readProblem(response), `${response.status}`));

  const disposition = response.headers.get('content-disposition') ?? '';
  const name = /filename\*?=(?:UTF-8'')?"?([^";]+)"?/i.exec(disposition)?.[1] ?? fallbackName;
  const url = URL.createObjectURL(await response.blob());
  const link = Object.assign(document.createElement('a'), { href: url, download: decodeURIComponent(name) });
  document.body.append(link);
  link.click();
  link.remove();
  setTimeout(() => URL.revokeObjectURL(url), 10_000);
}

// ------------------------------------------------------------------ session

export async function signIn(email: string, password: string): Promise<UserProfile> {
  const tokens = await api<TokenResponse>('/api/auth/login', { body: { email, password }, anonymous: true });
  store(tokens);
  return tokens.user;
}

export async function signOut(): Promise<void> {
  const token = storedRefreshToken();
  clear();
  if (token) {
    try {
      await api('/api/auth/logout', { body: { refreshToken: token }, anonymous: true });
    } catch {
      // already signed out locally; the token expires on its own
    }
  }
}

/**
 * Changing the password ends every session, including this one, so sign in again right away with the
 * new password to stay signed in here.
 */
export async function changePassword(email: string, currentPassword: string, newPassword: string): Promise<UserProfile> {
  await api('/api/auth/change-password', { body: { currentPassword, newPassword } });
  return signIn(email, newPassword);
}
