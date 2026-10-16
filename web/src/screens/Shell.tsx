import type { ReactNode } from 'react';
import type { TextKey } from '../lib/i18n.ts';
import { buildHash, type Screen } from '../lib/routes.ts';
import type { Lang } from '../lib/time.ts';
import type { Terminal } from '../lib/types.ts';
import { useApp } from '../ui/context.tsx';
import { Appointments } from './Appointments.tsx';
import { Book } from './Book.tsx';
import { BulkUpload } from './BulkUpload.tsx';
import { CapacitySetup } from './CapacitySetup.tsx';
import { Fleet } from './Fleet.tsx';
import { Kpi } from './Kpi.tsx';
import { MapEditor } from './MapEditor.tsx';
import { ControlRoom } from './ControlRoom.tsx';
import { TrackContainers } from './TrackContainers.tsx';
import { GateScreen } from './Gate.tsx';
import { SlotBoard } from './SlotBoard.tsx';
import { Help } from './Help.tsx';

const navText: Record<Screen, TextKey> = {
  book: 'nav.book',
  track: 'nav.track',
  bulk: 'nav.bulk',
  appointments: 'nav.appointments',
  slots: 'nav.slots',
  setup: 'nav.setup',
  map: 'nav.map',
  control: 'nav.control',
  gate: 'nav.gate',
  kpi: 'nav.kpi',
  fleet: 'nav.fleet',
  help: 'nav.help',
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
          {screen === 'track' && <TrackContainers />}
          {screen === 'appointments' && <Appointments />}
          {screen === 'slots' && <SlotBoard />}
          {screen === 'gate' && <GateScreen />}
          {screen === 'bulk' && <BulkUpload />}
          {screen === 'setup' && <CapacitySetup />}
          {screen === 'map' && <MapEditor />}
          {screen === 'control' && <ControlRoom />}
          {screen === 'kpi' && <Kpi />}
          {screen === 'fleet' && <Fleet />}
          {screen === 'help' && <Help />}
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
    track: <><circle cx="10.5" cy="10.5" r="6.5" /><path d="M15.5 15.5L21 21" /><rect x="7.5" y="8" width="6" height="5" rx="0.5" /></>,
    appointments: <><path d="M4 6h16M4 12h16M4 18h10" /><circle cx="19" cy="18" r="2" /></>,
    slots: <><rect x="3" y="12" width="4" height="9" /><rect x="10" y="6" width="4" height="15" /><rect x="17" y="9" width="4" height="12" /></>,
    gate: <><path d="M3 21V8l9-5 9 5v13" /><path d="M7 21v-8h10v8M7 17h10" /></>,
    bulk: <><path d="M6 3h8l4 4v14H6z" /><path d="M14 3v4h4M9 12h6M9 16h6" /></>,
    kpi: <><path d="M4 20V4M4 20h16" /><path d="M7 15l4-5 3 3 5-7" /></>,
    fleet: <><path d="M2 16V7h11v9M13 10h4l3 3v3h-7" /><circle cx="6" cy="17" r="2" /><circle cx="17" cy="17" r="2" /></>,
    control: <><circle cx="12" cy="12" r="8.5" /><circle cx="12" cy="12" r="4" /><path d="M12 1.5v4M12 18.5v4M1.5 12h4M18.5 12h4" /></>,
    map: <><path d="M3 6l6-2 6 2 6-2v14l-6 2-6-2-6 2z" /><path d="M9 4v14M15 6v14" /></>,
    setup: <><rect x="3" y="4" width="18" height="16" rx="1" /><path d="M3 9h18M9 4v16M15 4v16" /></>,
    help: <><circle cx="12" cy="12" r="9" /><path d="M9.5 9.5a2.5 2.5 0 1 1 3.5 2.3c-.6.3-1 .9-1 1.6v.6" /><circle cx="12" cy="17.2" r="0.6" fill="currentColor" /></>,
  };
  return (
    <svg width="22" height="22" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="1.6"
      strokeLinecap="round" strokeLinejoin="round" aria-hidden="true">
      {paths[screen]}
    </svg>
  );
}
