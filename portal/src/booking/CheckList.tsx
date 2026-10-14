import type { Check } from '../api/types';
import { useI18n } from '../i18n/I18n';
import { checkResultKeys, checkTypeKeys } from './labels';

/** Checks with the terminal: what was checked, the outcome, and the terminal system's own words. */
export function CheckList({ checks, onlyProblems = false }: { checks: Check[]; onlyProblems?: boolean }) {
  const { t } = useI18n();
  const shown = onlyProblems ? checks.filter((c) => c.result !== 'Pass') : checks;
  if (shown.length === 0) return null;
  return (
    <ul className="checks">
      {shown.map((c, i) => (
        <li key={i} data-result={c.result}>
          <span className="check-name">{t(checkTypeKeys[c.type])}</span>
          <span className="check-result">{t(checkResultKeys[c.result])}</span>
          {c.message && c.result !== 'Pass' && (
            <span className="sub" lang="en">
              {c.message}
            </span>
          )}
        </li>
      ))}
    </ul>
  );
}
