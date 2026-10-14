import type { ReactNode } from 'react';
import { useI18n } from '../i18n/I18n';
import { LanguageSwitch } from './ui';

// A plausible terminal day: quiet night, morning peak, crane maintenance at 13:00, evening tail.
// Values are share of the hour's capacity booked; null = closed.
const DAY: (number | null)[] = [
  0.15, 0.1, 0.08, 0.1, 0.2, 0.45, 0.8, 1, 1, 0.95, 0.85, 0.7, 0.6, null, 0.75, 0.9, 1, 0.95, 0.7, 0.55, 0.45, 0.35, 0.25, 0.2,
];

/** Frame of the signed-out pages: the form on one side, the slot strip on the other. */
export function GateLayout({ children }: { children: ReactNode }) {
  const { t } = useI18n();
  return (
    <div className="gate">
      <main className="gate-form" id="content">
        <div className="gate-top">
          <LanguageSwitch />
        </div>
        {children}
      </main>
      <aside className="gate-visual" aria-label={t('stripCaption')}>
        <div>
          <p className="title" lang="en" dir="ltr">
            {t('appName')}
          </p>
          <p className="tagline">{t('appTagline')}</p>
        </div>
        <div>
          <div className="slot-strip" aria-hidden="true">
            {DAY.map((load, hour) => (
              <span
                key={hour}
                data-full={load !== null && load >= 1 ? 'true' : undefined}
                data-closed={load === null ? 'true' : undefined}
                style={{ blockSize: `${Math.round((load ?? 1) * 100)}%` }}
              />
            ))}
          </div>
          <div className="slot-hours" aria-hidden="true">
            <span>00</span>
            <span>06</span>
            <span>12</span>
            <span>18</span>
            <span>23</span>
          </div>
        </div>
        <p className="strip-caption">{t('stripCaption')}</p>
      </aside>
    </div>
  );
}
