import { useCallback, useEffect, useMemo, useState } from 'react';
import { ApiClient, browserStore } from './lib/api.ts';
import { translate, translateDynamic, type TextKey } from './lib/i18n.ts';
import { direction, isPortalLanguage, loadLanguages } from './lib/languages.ts';
import { applyBrand, defaultBrand, type Brand } from './lib/brand.ts';
import { buildHash, parseHash, screensFor, type Route, type Screen } from './lib/routes.ts';
import { localDate, type Lang } from './lib/time.ts';
import type { Terminal, UserProfile } from './lib/types.ts';
import { AppContext, type AppState } from './ui/context.tsx';
import { Login } from './screens/Login.tsx';
import { Shell } from './screens/Shell.tsx';
import { SetPassword } from './screens/SetPassword.tsx';
import { Docs } from './screens/Docs.tsx';

const api = new ApiClient(browserStore);

function initialLang(user: UserProfile | null): Lang {
  try {
    // Checked against the installation's languages once they are loaded (a pack may have been removed).
    const saved = localStorage.getItem('yardsyncs.lang');
    if (saved && /^[a-z]{2}$/.test(saved)) return saved;
  } catch {
    // ignore
  }
  if (user && isPortalLanguage(user.preferredLanguage.trim())) return user.preferredLanguage.trim();
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
  const [, setLanguagesLoaded] = useState(false);
  // The installation's brand, or an operator's at its own web address; after sign-in, the terminal's.
  const [publicBrand, setPublicBrand] = useState<Brand>(defaultBrand);
  const [brand, setBrand] = useState<Brand>(defaultBrand);

  useEffect(() => {
    fetch('/api/branding')
      .then(r => (r.ok ? (r.json() as Promise<Brand>) : null))
      .then(b => { if (b) { setPublicBrand(b); setBrand(b); } })
      .catch(() => { /* the Yardsyncs look */ });
  }, []);

  useEffect(() => applyBrand(brand), [brand]);
  const route = useHashRoute();

  // Language packs on the server: more portal languages, or corrected texts.
  useEffect(() => {
    void loadLanguages().then(() => {
      setLanguagesLoaded(true);
      setLangState(current => (isPortalLanguage(current) ? current : 'en'));
    });
  }, []);

  useEffect(() => {
    api.onSessionEnded = () => { setUser(null); setEnded(true); };
  }, []);

  useEffect(() => {
    document.documentElement.lang = lang;
    document.documentElement.dir = direction(lang);
  }, [lang]);

  const setLang = useCallback((next: Lang) => {
    setLangState(next);
    try { localStorage.setItem('yardsyncs.lang', next); } catch { /* ignore */ }
  }, []);

  // Texts name the brand (the product documentation, the branding settings and the app's store name stay Yardsyncs).
  const t = useCallback((key: TextKey, values?: Record<string, string | number>) => {
    const text = translate(lang, key, values);
    return brand.name === defaultBrand.name || key.startsWith('docs.') || key.startsWith('brand.') ? text : text.replace(/Yardsyncs(?! mobile)/g, brand.name);
  }, [lang, brand.name]);

  // The documentation is public: readable signed in or not.
  if (window.location.hash.startsWith('#/docs')) {
    return <Docs page={window.location.hash.replace(/^#\/docs\/?/, '').split(/[?#]/)[0]} lang={lang} setLang={setLang} t={t} signedIn={!!user} />;
  }

  // The emailed link to set a password works whether or not someone is signed in on this browser.
  if (window.location.hash.startsWith('#/password')) {
    const token = new URLSearchParams(window.location.hash.split('?')[1] ?? '').get('token') ?? '';
    return <SetPassword api={api} token={token} lang={lang} t={t} setLang={setLang} brand={publicBrand}
      onDone={async () => { if (user) { await api.logout(); setUser(null); } window.location.hash = '#/'; }} />;
  }

  if (!user) {
    return (
      <Login api={api} lang={lang} t={t} setLang={setLang} sessionEnded={ended} brand={publicBrand}
        onSignedIn={u => { setEnded(false); setUser(u); }} />
    );
  }

  return <SignedIn user={user} lang={lang} setLang={setLang} t={t} route={route} brand={brand} setBrand={setBrand}
    onSignOut={async () => { await api.logout(); setUser(null); setBrand(publicBrand); }} />;
}

function SignedIn({ user, lang, setLang, t, route, onSignOut, brand, setBrand }: {
  brand: Brand;
  setBrand: (brand: Brand) => void;
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

  // The terminal's brand: its operator's look for its people and its companies' dispatchers alike.
  const brandTerminal = terminal?.terminalId;
  useEffect(() => {
    if (brandTerminal == null) return;
    let live = true;
    api.get<Brand>(`/api/terminals/${brandTerminal}/branding`).then(b => { if (live) setBrand(b); }).catch(() => { /* keep */ });
    return () => { live = false; };
  }, [brandTerminal, setBrand]);
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
    brand,
  }, [terminal, user, lang, t, route, go, screens, brand]);

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
