import { useState } from 'react';
import { ApiError } from '../lib/api.ts';
import { money } from '../lib/money.ts';
import { isCompanyRole } from '../lib/routes.ts';
import { stamp } from '../lib/time.ts';
import { useApp } from '../ui/context.tsx';
import { ActionButton, LoadError, Notice, useLoad } from '../ui/kit.tsx';

type ChargeKind = 'Booking' | 'NoShow' | 'LateCancel';

interface ChargeRow {
  chargeId: number;
  appointmentNo: string;
  companyId: number;
  companyName: string;
  kind: ChargeKind;
  amountMinor: number;
  currency: string;
  createdAt: string;
  waivedAt: string | null;
  waiveReason: string | null;
}

interface ChargeReport {
  currency: string;
  decimals: number;
  charges: ChargeRow[];
  companies: { companyId: number; companyName: string; charges: number; amountMinor: number }[];
  totalMinor: number;
}

const kindText = { Booking: 'charges.kind.Booking', NoShow: 'charges.kind.NoShow', LateCancel: 'charges.kind.LateCancel' } as const;

/**
 * The fees charged at this terminal for a period: totals per company and every charge, as the terminal bills them
 * through its own system (CSV). Companies see their own; the terminal's admins can waive a charge with a reason.
 */
export function Charges() {
  const { api, t, lang, user, terminal, today } = useApp();
  const [from, setFrom] = useState(`${today.slice(0, 8)}01`);
  const [to, setTo] = useState(today);
  const [error, setError] = useState<string | null>(null);
  const base = `/api/terminals/${terminal.terminalId}/charges`;
  const query = `from=${from}&to=${to}`;
  const report = useLoad(() => api.get<ChargeReport>(`${base}?${query}`), [base, query]);
  const company = isCompanyRole(user.role);
  const canWaive = user.role === 'TerminalAdmin' || user.role === 'OperatorAdmin' || user.role === 'SysAdmin';
  const r = report.data;
  const m = (minor: number) => (r ? money(minor, r.currency, r.decimals, lang) : '');

  async function csv() {
    try {
      const { blob, fileName } = await api.download(`${base}?${query}&format=csv`);
      const url = URL.createObjectURL(blob);
      const a = document.createElement('a');
      a.href = url;
      a.download = fileName ?? `charges-${from}-${to}.csv`;
      a.click();
      setTimeout(() => URL.revokeObjectURL(url), 1000);
    } catch (e) {
      setError(e instanceof ApiError ? e.message : t('common.offline'));
    }
  }

  return (
    <section className="screen">
      <header className="screen-head">
        <h1>{t('charges.title')}</h1>
        <label className="field field-inline"><span>{t('charges.from')}</span>
          <input type="date" value={from} max={to} onChange={e => e.target.value && setFrom(e.target.value)} /></label>
        <label className="field field-inline"><span>{t('charges.to')}</span>
          <input type="date" value={to} min={from} onChange={e => e.target.value && setTo(e.target.value)} /></label>
        <button type="button" className="btn btn-secondary" onClick={csv}>{t('charges.csv')}</button>
      </header>
      {error && <Notice kind="error" onClose={() => setError(null)}>{error}</Notice>}
      {report.error && <LoadError error={report.error} retry={report.reload} />}
      {r && (
        <>
          <p className="head-figure"><strong className="num">{t('charges.total', { amount: m(r.totalMinor) })}</strong> · {t('charges.hint')}</p>
          {r.charges.length === 0 && <p className="empty">{t('charges.none')}</p>}
          {!company && r.companies.length > 0 && (
            <div className="table-wrap">
              <table className="grid compact">
                <thead><tr><th>{t('common.company')}</th><th className="num">{t('charges.count')}</th><th className="num">{t('charges.amount')}</th></tr></thead>
                <tbody>
                  {r.companies.map(c => (
                    <tr key={c.companyId}><td>{c.companyName}</td><td className="num">{c.charges}</td><td className="num"><bdi>{m(c.amountMinor)}</bdi></td></tr>
                  ))}
                </tbody>
              </table>
            </div>
          )}
          {r.charges.length > 0 && (
            <>
              <h2 className="section-title">{t('charges.list')}</h2>
              <div className="table-wrap">
                <table className="grid compact">
                  <thead><tr>
                    <th>{t('charges.when')}</th><th>{t('charges.appointment')}</th>{!company && <th>{t('common.company')}</th>}
                    <th>{t('charges.kind')}</th><th className="num">{t('charges.amount')}</th><th />
                  </tr></thead>
                  <tbody>
                    {r.charges.map(c => (
                      <tr key={c.chargeId} className={c.waivedAt ? 'inactive' : undefined}>
                        <td className="num">{stamp(c.createdAt, terminal.timeZone, lang)}</td>
                        <td><bdi>{c.appointmentNo}</bdi></td>
                        {!company && <td>{c.companyName}</td>}
                        <td>{t(kindText[c.kind])}</td>
                        <td className="num"><bdi>{m(c.amountMinor)}</bdi></td>
                        <td className="cell-action">
                          {c.waivedAt
                            ? <span className="muted" dir="auto">{t('charges.waived')}: {c.waiveReason}</span>
                            : canWaive && <Waive url={`${base}/${c.chargeId}/waive`} onDone={report.reload} onError={setError} />}
                        </td>
                      </tr>
                    ))}
                  </tbody>
                </table>
              </div>
            </>
          )}
        </>
      )}
    </section>
  );
}

function Waive({ url, onDone, onError }: { url: string; onDone: () => void; onError: (m: string) => void }) {
  const { api, t } = useApp();
  const [asking, setAsking] = useState(false);
  const [reason, setReason] = useState('');
  if (!asking)
    return <button type="button" className="btn btn-secondary" onClick={() => setAsking(true)}>{t('charges.waive')}</button>;
  return (
    <span className="inline-ask">
      <input value={reason} placeholder={t('charges.waiveReason')} aria-label={t('charges.waiveReason')} autoFocus onChange={e => setReason(e.target.value)} />
      <ActionButton kind="primary" disabled={!reason.trim()} onError={onError} onRun={async () => {
        await api.post(url, { reason: reason.trim() });
        setAsking(false);
        onDone();
      }}>{t('charges.waive')}</ActionButton>
      <button type="button" className="link-btn" onClick={() => setAsking(false)}>{t('charges.keep')}</button>
    </span>
  );
}
