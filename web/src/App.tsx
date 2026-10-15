import { useCallback, useEffect, useMemo, useState } from 'react';
import { ApiClient, browserStore } from './lib/api.ts';
import { translate, translateDynamic, type TextKey } from './lib/i18n.ts';
import { buildHash, parseHash, screensFor, type Route, type Screen } from './lib/routes.ts';
import { localDate, type Lang } from './lib/time.ts';
import type { Terminal, UserProfile } from './lib/types.ts';
import { AppContext, type AppState } from './ui/context.tsx';
import { Login } from './screens/Login.tsx';
import { Shell } from './screens/Shell.tsx';

const api = new ApiClient(browserStore);

function initialLang(user: UserProfile | null): Lang {
  try {
    const saved = localStorage.getItem('yardsyncs.lang');
    if (saved === 'en' || saved === 'ar') return saved;
  } catch {
    // ignore
  }
  if (user) return user.preferredLanguage === 'ar' ? 'ar' : 'en';
  return typeof navigator !== 'undefined' && navigator.language?.startsWith('ar') ? 'ar' : 'en';
}

function useHashRoute(): Route {
  const [route, setRoute] = useState(() => parseHash(window.location.hash));
  useEffect(() => {
    const onChange = () => setRoute(parseHash(window.location.hash));
    window.addEventListener('hashchange', onChange);
    return () => window.removeEventListener('hashchange', onChange);
  }, []);
  return route;
}

export function App() {
  const [user, setUser] = useState<UserProfile | null>(api.user);
  const [lang, setLangState] = useState<Lang>(() => initialLang(api.user));
  const [ended, setEnded] = useState(false);
  const route = useHashRoute();

  useEffect(() => {
    api.onSessionEnded = () => { setUser(null); setEnded(true); };
  }, []);

  useEffect(() => {
    document.documentElement.lang = lang;
    document.documentElement.dir = lang === 'ar' ? 'rtl' : 'ltr';
  }, [lang]);

  const setLang = useCallback((next: Lang) => {
    setLangState(next);
    try { localStorage.setItem('yardsyncs.lang', next); } catch { /* ignore */ }
  }, []);

  const t = useCallback((key: TextKey, values?: Record<string, string | number>) => translate(lang, key, values), [lang]);

  if (!user) {
    return (
      <Login api={api} lang={lang} t={t} setLang={setLang} sessionEnded={ended}
        onSignedIn={u => { setEnded(false); setUser(u); }} />
    );
  }

  return <SignedIn user={user} lang={lang} setLang={setLang} t={t} route={route}
    onSignOut={async () => { await api.logout(); setUser(null); }} />;
}

function SignedIn({ user, lang, setLang, t, route, onSignOut }: {
  user: UserProfile;
  lang: Lang;
  setLang: (l: Lang) => void;
  t: AppState['t'];
  route: Route;
  onSignOut: () => void;
}) {
  const [terminals, setTerminals] = useState<Terminal[] | null>(null);
  const [terminalId, setTerminalId] = useState<number | null>(() => {
    try { return Number(localStorage.getItem('yardsyncs.terminal')) || null; } catch { return null; }
  });
  const [failed, setFailed] = useState(false);
  const [, setClockTick] = useState(0);

  useEffect(() => {
    let live = true;
    api.get<Terminal[]>('/api/terminals')
      .then(list => { if (live) setTerminals(list); })
      .catch(() => { if (live) setFailed(true); });
    return () => { live = false; };
  }, []);

  // Keep "today" right across midnight for screens left open on a gate PC.
  useEffect(() => {
    const id = setInterval(() => setClockTick(n => n + 1), 60_000);
    return () => clearInterval(id);
  }, []);

  const terminal = terminals?.find(x => x.terminalId === terminalId) ?? terminals?.[0] ?? null;
  const screens = useMemo(() => screensFor(user.role), [user.role]);

  const go = useCallback((screen: Screen, params?: Record<string, string | undefined>) => {
    window.location.hash = buildHash(screen, params);
  }, []);

  const state = useMemo<AppState | null>(() => terminal && {
    api,
    user,
    lang,
    t,
    tx: (group, value) => translateDynamic(lang, `${group}.${value}`, value),
    terminal,
    route: route.screen && screens.includes(route.screen) ? route : { screen: screens[0], params: {} },
    go,
    today: localDate(new Date(), terminal.timeZone),
  }, [terminal, user, lang, t, route, go, screens]);

  if (failed) {
    return <main className="boot"><p>{t('common.offline')}</p><button className="btn btn-primary" onClick={() => window.location.reload()}>{t('common.retry')}</button></main>;
  }
  if (!state) return <main className="boot"><p>{t('common.loading')}</p></main>;

  return (
    <AppContext.Provider value={state}>
      <Shell terminals={terminals!} screens={screens}
        onTerminal={id => { setTerminalId(id); try { localStorage.setItem('yardsyncs.terminal', String(id)); } catch { /* ignore */ } }}
        setLang={setLang} onSignOut={onSignOut} />
    </AppContext.Provider>
  );
}
