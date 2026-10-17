// HTTP client for the Yardsyncs API: bearer token, one silent refresh on 401, problem-details errors.
//
// The portal's session: the access token lives only in this page's memory, the refresh token in an HttpOnly cookie
// the page can't read (X-Yardsyncs-Session: cookie). Only who is signed in is kept in browser storage, so a reload
// knows to ask the cookie for a new access token. Nothing a script on the page could steal outlives the page.

import type { TokenResponse, UserProfile } from './types.ts';

export interface Session {
  /** In memory only; null after a reload until the cookie gives a new one. */
  accessToken: string | null;
  user: UserProfile;
}

/** What browser storage keeps: who is signed in, never a token. (Older versions kept tokens here: see load.) */
interface Stored {
  user: UserProfile;
  /** A refresh token left by an older version: used once to move to the cookie, then dropped. */
  refreshToken?: string;
}

const SESSION_HEADERS = { 'Content-Type': 'application/json', 'X-Yardsyncs-Session': 'cookie' };

/** An API refusal with the server's explanation (RFC 7807 problem details). */
export class ApiError extends Error {
  readonly status: number;
  readonly errors: string[];
  readonly checks: { item: number | null; type: string; result: string; message: string | null }[];

  constructor(status: number, message: string, errors: string[] = [], checks: ApiError['checks'] = []) {
    super(message);
    this.name = 'ApiError';
    this.status = status;
    this.errors = errors;
    this.checks = checks;
  }
}

export interface SessionStore {
  load(): Stored | null;
  save(stored: Stored | null): void;
}

/** Remembers who is signed in across tabs and reloads (the cookie does the rest). */
export const browserStore: SessionStore = {
  load() {
    try {
      const raw = localStorage.getItem('yardsyncs.session');
      return raw ? (JSON.parse(raw) as Stored) : null;
    } catch {
      return null;
    }
  },
  save(stored) {
    try {
      if (stored) localStorage.setItem('yardsyncs.session', JSON.stringify({ user: stored.user }));
      else localStorage.removeItem('yardsyncs.session');
    } catch {
      // Storage blocked (private mode): the session lives in memory and the cookie only.
    }
  },
};

type Fetch = (input: string, init?: RequestInit) => Promise<Response>;

export async function readProblem(response: Response): Promise<ApiError> {
  let body: { detail?: string; title?: string; errors?: unknown; checks?: unknown } = {};
  try {
    body = await response.json();
  } catch {
    // Not JSON (proxy error page, empty body).
  }
  const errors = Array.isArray(body.errors) ? body.errors.map(String) : [];
  const checks = Array.isArray(body.checks) ? (body.checks as ApiError['checks']) : [];
  const message = body.detail || errors.join(' ') || body.title || `Request failed (${response.status}).`;
  return new ApiError(response.status, message, errors, checks);
}

export class ApiClient {
  private session: Session | null;
  private refreshing: Promise<boolean> | null = null;
  private readonly store: SessionStore;
  private readonly fetcher: Fetch;
  private readonly base: string;
  /** Called when the session ends (refresh refused or sign-out). */
  onSessionEnded: (() => void) | null = null;

  private legacyRefreshToken: string | null;

  constructor(store: SessionStore, fetcher: Fetch = (i, init) => fetch(i, init), base = '') {
    this.store = store;
    this.fetcher = fetcher;
    this.base = base;
    const stored = store.load();
    this.session = stored ? { accessToken: null, user: stored.user } : null;
    this.legacyRefreshToken = stored?.refreshToken ?? null;
    if (stored?.refreshToken) store.save({ user: stored.user }); // tokens never stay in storage
  }

  get user(): UserProfile | null {
    return this.session?.user ?? null;
  }

  /** The password: signed in, or (two-step sign-in) a challenge for {@link loginCode}. */
  async login(email: string, password: string): Promise<{ user: UserProfile } | { challenge: string }> {
    const response = await this.fetcher(`${this.base}/api/auth/login`, {
      method: 'POST',
      headers: SESSION_HEADERS,
      credentials: 'same-origin',
      body: JSON.stringify({ email, password }),
    });
    if (!response.ok) throw await readProblem(response);
    const body = (await response.json()) as TokenResponse | { secondStep: true; challenge: string };
    if ('secondStep' in body) return { challenge: body.challenge };
    this.adopt(body);
    return { user: this.session!.user };
  }

  /** The second step: the code from the authenticator app. */
  async loginCode(challenge: string, code: string): Promise<UserProfile> {
    const response = await this.fetcher(`${this.base}/api/auth/login/code`, {
      method: 'POST',
      headers: SESSION_HEADERS,
      credentials: 'same-origin',
      body: JSON.stringify({ challenge, code }),
    });
    if (!response.ok) throw await readProblem(response);
    this.adopt((await response.json()) as TokenResponse);
    return this.session!.user;
  }

  /** The signed-in user changed (e.g. two-step sign-in turned on). */
  updateUser(user: UserProfile) {
    if (!this.session) return;
    this.session = { ...this.session, user };
    this.store.save({ user });
  }

  async logout(): Promise<void> {
    this.end(false);
    try {
      await this.fetcher(`${this.base}/api/auth/logout`, {
        method: 'POST',
        headers: SESSION_HEADERS,
        credentials: 'same-origin',
        body: JSON.stringify({}),
      });
    } catch {
      // Signed out locally either way.
    }
  }

  get<T>(path: string): Promise<T> {
    return this.request<T>('GET', path);
  }

  post<T>(path: string, body?: unknown): Promise<T> {
    return this.request<T>('POST', path, body);
  }

  put<T>(path: string, body?: unknown): Promise<T> {
    return this.request<T>('PUT', path, body);
  }

  async request<T>(method: string, path: string, body?: unknown): Promise<T> {
    const response = await this.authorized(method, path, body);
    if (response.status === 204) return undefined as T;
    const text = await response.text();
    return (text ? JSON.parse(text) : undefined) as T;
  }

  del<T>(path: string): Promise<T> {
    return this.request<T>('DELETE', path);
  }

  /** Multipart upload (a file in a form). */
  postForm<T>(path: string, form: FormData): Promise<T> {
    return this.request<T>('POST', path, form);
  }

  /** A file from the API, with the name the server suggests. */
  async download(path: string): Promise<{ blob: Blob; fileName: string | null }> {
    const response = await this.authorized('GET', path);
    const disposition = response.headers.get('Content-Disposition') ?? '';
    const match = /filename\*?=(?:UTF-8'')?"?([^";]+)"?/i.exec(disposition);
    return { blob: await response.blob(), fileName: match ? decodeURIComponent(match[1]) : null };
  }

  /** Sends with the access token, refreshes once on 401, turns errors into ApiError. */
  private async authorized(method: string, path: string, body?: unknown): Promise<Response> {
    let response = await this.send(method, path, body);
    if (response.status === 401 && this.session && (await this.refresh())) {
      response = await this.send(method, path, body);
    }
    if (response.status === 401) {
      this.end(true);
      throw new ApiError(401, 'Session ended.');
    }
    if (!response.ok) throw await readProblem(response);
    return response;
  }

  private send(method: string, path: string, body?: unknown): Promise<Response> {
    const headers: Record<string, string> = { Accept: 'application/json' };
    const isForm = typeof FormData !== 'undefined' && body instanceof FormData;
    if (body !== undefined && !isForm) headers['Content-Type'] = 'application/json';
    if (this.session?.accessToken) headers.Authorization = `Bearer ${this.session.accessToken}`;
    return this.fetcher(`${this.base}${path}`, {
      method,
      headers,
      body: body === undefined ? undefined : isForm ? (body as FormData) : JSON.stringify(body),
    });
  }

  /**
   * One refresh at a time: parallel requests that hit 401 wait for the same refresh. Tried twice: when two tabs refresh
   * at the same moment, the one that loses finds the winner's new cookie on the second try.
   */
  private refresh(): Promise<boolean> {
    if (!this.refreshing) {
      this.refreshing = (async () => {
        for (let attempt = 0; attempt < 2; attempt++) {
          if (attempt > 0) await new Promise(resolve => setTimeout(resolve, this.retryDelayMs));
          try {
            const legacy = this.legacyRefreshToken;
            this.legacyRefreshToken = null;
            const response = await this.fetcher(`${this.base}/api/auth/refresh`, {
              method: 'POST',
              headers: SESSION_HEADERS,
              credentials: 'same-origin',
              body: JSON.stringify(legacy ? { refreshToken: legacy } : {}),
            });
            if (response.ok) {
              this.adopt((await response.json()) as TokenResponse);
              return true;
            }
          } catch {
            return false;
          }
        }
        return false;
      })().finally(() => {
        this.refreshing = null;
      });
    }
    return this.refreshing;
  }

  /** Wait before the second refresh try (tests set 0). */
  retryDelayMs = 400;

  private adopt(tokens: TokenResponse) {
    this.session = { accessToken: tokens.accessToken, user: tokens.user };
    this.store.save({ user: tokens.user });
  }

  private end(notify: boolean) {
    this.session = null;
    this.legacyRefreshToken = null;
    this.store.save(null);
    if (notify) this.onSessionEnded?.();
  }
}
