import { NavLink, Navigate, Outlet, useLocation } from 'react-router';
import { useAuth } from '../auth/Auth';
import { LanguageSwitch, LoadError } from '../components/ui';
import { useI18n } from '../i18n/I18n';
import { TerminalProvider, useTerminals } from '../terminals/Terminals';
import { navFor, roleLabel } from './nav';

/** Signed-in pages: redirects to sign-in otherwise, keeping where the person wanted to go. */
export function RequireAuth() {
  const { state } = useAuth();
  const location = useLocation();
  const { t } = useI18n();

  if (state.status === 'loading') {
    return (
      <p className="muted" role="status" style={{ padding: '2rem' }}>
        {t('loading')}
      </p>
    );
  }
  if (state.status === 'signed-out') {
    return <Navigate to="/sign-in" replace state={{ from: location.pathname + location.search }} />;
  }
  return (
    <TerminalProvider>
      <AppShell />
    </TerminalProvider>
  );
}

function AppShell() {
  const { t } = useI18n();
  const { user, signOut } = useAuth();
  const { terminals, terminal, select, isLoading, error } = useTerminals();
  if (!user) return null;

  return (
    <div className="shell">
      <a className="skip-link" href="#content">
        {t('skipToContent')}
      </a>
      <aside className="rail">
        <span className="wordmark" lang="en" dir="ltr">
          {t('appName')}
        </span>
        <nav aria-label={t('mainNavigation')}>
          <ul>
            {navFor(user.role).map((item) => (
              <li key={item.path}>
                <NavLink to={item.path}>{t(item.label)}</NavLink>
              </li>
            ))}
          </ul>
        </nav>
        <div className="rail-foot">
          {terminals.length > 1 && terminal && (
            <div className="field">
              <label htmlFor="terminal-select">{t('terminal')}</label>
              <select id="terminal-select" value={terminal.terminalId} onChange={(e) => select(Number(e.target.value))}>
                {terminals.map((x) => (
                  <option key={x.terminalId} value={x.terminalId}>
                    {x.name}
                  </option>
                ))}
              </select>
            </div>
          )}
          {terminals.length === 1 && terminal && <span>{terminal.name}</span>}
          <div className="who">
            <strong>{user.fullName}</strong>
            <span>{user.companyName ?? t(roleLabel(user.role))}</span>
          </div>
          <LanguageSwitch />
          <button type="button" className="button quiet" onClick={() => void signOut()}>
            {t('signOut')}
          </button>
        </div>
      </aside>
      <main className="main" id="content" tabIndex={-1}>
        {isLoading ? (
          <p className="muted" role="status">
            {t('loading')}
          </p>
        ) : error ? (
          <LoadError error={error} onRetry={() => window.location.reload()} />
        ) : !terminal ? (
          <p>{t('noTerminals')}</p>
        ) : (
          <Outlet />
        )}
      </main>
    </div>
  );
}
