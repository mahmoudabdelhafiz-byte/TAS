import type { ReactNode } from 'react';
import type { TextKey } from '../lib/i18n.ts';
import { buildHash, type Screen } from '../lib/routes.ts';
import type { Lang } from '../lib/time.ts';
import type { Terminal } from '../lib/types.ts';
import { useApp } from '../ui/context.tsx';
import { Appointments } from './Appointments.tsx';
import { Book } from './Book.tsx';
import { GateScreen } from './Gate.tsx';
import { SlotBoard } from './SlotBoard.tsx';

const navText: Record<Screen, TextKey> = {
  book: 'nav.book',
  appointments: 'nav.appointments',
  slots: 'nav.slots',
  gate: 'nav.gate',
};

export function Shell({ terminals, screens, onTerminal, setLang, onSignOut }: {
  terminals: Terminal[];
  screens: Screen[];
  onTerminal: (id: number) => void;
  setLang: (l: Lang) => void;
  onSignOut: () => void;
}) {
  const { t, tx, lang, user, terminal, route } = useApp();
  const screen = route.screen!;

  return (
    <div className="shell">
      <nav className="rail" aria-label="Yardsyncs">
        <div className="rail-mark"><Mark /></div>
        <ul>
          {screens.map(s => (
            <li key={s}>
              <a href={buildHash(s)} className={s === screen ? 'rail-link active' : 'rail-link'} aria-current={s === screen ? 'page' : undefined}>
                <NavIcon screen={s} />
                <span>{t(navText[s])}</span>
              </a>
            </li>
          ))}
        </ul>
      </nav>

      <div className="frame">
        <header className="topbar">
          <div className="topbar-terminal">
            {terminals.length > 1 ? (
              <label>
                <span className="visually-hidden">{t('nav.terminal')}</span>
                <select value={terminal.terminalId} onChange={e => onTerminal(Number(e.target.value))}>
                  {terminals.map(x => <option key={x.terminalId} value={x.terminalId}>{x.name}</option>)}
                </select>
              </label>
            ) : (
              <span className="terminal-name">{terminal.name}</span>
            )}
          </div>
          <div className="topbar-end">
            <button type="button" className="link-btn" lang={lang === 'en' ? 'ar' : 'en'} onClick={() => setLang(lang === 'en' ? 'ar' : 'en')}>
              {t('lang.switch')}
            </button>
            <div className="who">
              <span className="who-name">{user.fullName}</span>
              <span className="who-role">{user.companyName ?? tx('role', user.role)}</span>
            </div>
            <button type="button" className="btn btn-quiet" onClick={onSignOut}>{t('nav.signOut')}</button>
          </div>
        </header>

        <main className="page" key={`${terminal.terminalId}-${screen}`}>
          {screen === 'book' && <Book />}
          {screen === 'appointments' && <Appointments />}
          {screen === 'slots' && <SlotBoard />}
          {screen === 'gate' && <GateScreen />}
        </main>
      </div>
    </div>
  );
}

/** The Yardsyncs mark: three boxes, one placed — a yard stack in sync. */
export function Mark() {
  return (
    <svg width="28" height="28" viewBox="0 0 32 32" aria-hidden="true" className="mark">
      <rect x="5" y="17" width="10" height="10" rx="1" className="mark-a" />
      <rect x="17" y="17" width="10" height="10" rx="1" className="mark-b" />
      <rect x="17" y="5" width="10" height="10" rx="1" className="mark-a" />
    </svg>
  );
}

function NavIcon({ screen }: { screen: Screen }) {
  const paths: Record<Screen, ReactNode> = {
    book: <><rect x="3" y="5" width="18" height="15" rx="1.5" /><path d="M3 10h18M8 3v4M16 3v4M12 13v5M9.5 15.5h5" /></>,
    appointments: <><path d="M4 6h16M4 12h16M4 18h10" /><circle cx="19" cy="18" r="2" /></>,
    slots: <><rect x="3" y="12" width="4" height="9" /><rect x="10" y="6" width="4" height="15" /><rect x="17" y="9" width="4" height="12" /></>,
    gate: <><path d="M3 21V8l9-5 9 5v13" /><path d="M7 21v-8h10v8M7 17h10" /></>,
  };
  return (
    <svg width="22" height="22" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="1.6"
      strokeLinecap="round" strokeLinejoin="round" aria-hidden="true">
      {paths[screen]}
    </svg>
  );
}
