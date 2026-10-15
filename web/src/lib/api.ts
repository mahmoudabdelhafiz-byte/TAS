// HTTP client for the Yardsyncs API: bearer token, one silent refresh on 401, problem-details errors.

import type { TokenResponse, UserProfile } from './types.ts';

export interface Session {
  accessToken: string;
  refreshToken: string;
  user: UserProfile;
}

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
  load(): Session | null;
  save(session: Session | null): void;
}

/** Keeps the session in localStorage so a dispatcher stays signed in across tabs and reloads. */
export const browserStore: SessionStore = {
  load() {
    try {
      const raw = localStorage.getItem('yardsyncs.session');
      return raw ? (JSON.parse(raw) as Session) : null;
    } catch {
      return null;
    }
  },
  save(session) {
    try {
      if (session) localStorage.setItem('yardsyncs.session', JSON.stringify(session));
      else localStorage.removeItem('yardsyncs.session');
    } catch {
      // Storage blocked (private mode): the session lives in memory only.
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

  constructor(store: SessionStore, fetcher: Fetch = (i, init) => fetch(i, init), base = '') {
    this.store = store;
    this.fetcher = fetcher;
    this.base = base;
    this.session = store.load();
  }

  get user(): UserProfile | null {
    return this.session?.user ?? null;
  }

  async login(email: string, password: string): Promise<UserProfile> {
    const response = await this.fetcher(`${this.base}/api/auth/login`, {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ email, password }),
    });
    if (!response.ok) throw await readProblem(response);
    this.adopt((await response.json()) as TokenResponse);
    return this.session!.user;
  }

  async logout(): Promise<void> {
    const refreshToken = this.session?.refreshToken;
    this.end(false);
    if (refreshToken) {
      try {
        await this.fetcher(`${this.base}/api/auth/logout`, {
          method: 'POST',
          headers: { 'Content-Type': 'application/json' },
          body: JSON.stringify({ refreshToken }),
        });
      } catch {
        // Signed out locally either way.
      }
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
    let response = await this.send(method, path, body);
    if (response.status === 401 && this.session && (await this.refresh())) {
      response = await this.send(method, path, body);
    }
    if (response.status === 401) {
      this.end(true);
      throw new ApiError(401, 'Session ended.');
    }
    if (!response.ok) throw await readProblem(response);
    if (response.status === 204) return undefined as T;
    const text = await response.text();
    return (text ? JSON.parse(text) : undefined) as T;
  }

  private send(method: string, path: string, body?: unknown): Promise<Response> {
    const headers: Record<string, string> = { Accept: 'application/json' };
    if (body !== undefined) headers['Content-Type'] = 'application/json';
    if (this.session) headers.Authorization = `Bearer ${this.session.accessToken}`;
    return this.fetcher(`${this.base}${path}`, {
      method,
      headers,
      body: body === undefined ? undefined : JSON.stringify(body),
    });
  }

  /** One refresh at a time: parallel requests that hit 401 wait for the same refresh. */
  private refresh(): Promise<boolean> {
    if (!this.refreshing) {
      const token = this.session?.refreshToken;
      this.refreshing = (async () => {
        if (!token) return false;
        try {
          const response = await this.fetcher(`${this.base}/api/auth/refresh`, {
            method: 'POST',
            headers: { 'Content-Type': 'application/json' },
            body: JSON.stringify({ refreshToken: token }),
          });
          if (!response.ok) return false;
          this.adopt((await response.json()) as TokenResponse);
          return true;
        } catch {
          return false;
        }
      })().finally(() => {
        this.refreshing = null;
      });
    }
    return this.refreshing;
  }

  private adopt(tokens: TokenResponse) {
    this.session = { accessToken: tokens.accessToken, refreshToken: tokens.refreshToken, user: tokens.user };
    this.store.save(this.session);
  }

  private end(notify: boolean) {
    this.session = null;
    this.store.save(null);
    if (notify) this.onSessionEnded?.();
  }
}
