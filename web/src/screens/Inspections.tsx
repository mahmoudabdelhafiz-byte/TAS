import { useState } from 'react';
import { isCompanyRole } from '../lib/routes.ts';
import { addDays, stamp } from '../lib/time.ts';
import type { InspectionRow } from '../lib/types.ts';
import { useApp } from '../ui/context.tsx';
import { InspectionSummary, disputeShortText, sealText } from '../ui/Inspection.tsx';
import { LoadError, Panel, useLoad } from '../ui/kit.tsx';

/**
 * Inspections at the gate for a period: seals and damage per container and passage, and disputes. Companies see their
 * own visits and dispute from here; the terminal's planners and admins settle disputes.
 */
export function Inspections() {
  const { api, t, lang, user, terminal, today } = useApp();
  const [from, setFrom] = useState(addDays(today, -7));
  const [to, setTo] = useState(today);
  const [damageOnly, setDamageOnly] = useState(true);
  const [disputedOnly, setDisputedOnly] = useState(false);
  const [open, setOpen] = useState<string | null>(null);
  const query = `from=${from}&to=${to}&damageOnly=${damageOnly}&disputedOnly=${disputedOnly}`;
  const rows = useLoad(() => api.get<InspectionRow[]>(`/api/terminals/${terminal.terminalId}/inspections?${query}`), [terminal.terminalId, query]);
  const company = isCompanyRole(user.role);
  const openDisputes = (rows.data ?? []).filter(r => r.dispute === 'Open').length;

  return (
    <section className={`screen with-panel${open ? ' panel-open' : ''}`}>
      <div>
        <header className="screen-head">
          <h1>{t('insp.listTitle')}</h1>
          <label className="field field-inline"><span>{t('charges.from')}</span>
            <input type="date" value={from} max={to} onChange={e => e.target.value && setFrom(e.target.value)} /></label>
          <label className="field field-inline"><span>{t('charges.to')}</span>
            <input type="date" value={to} min={from} onChange={e => e.target.value && setTo(e.target.value)} /></label>
          <label className="check"><input type="checkbox" checked={damageOnly} onChange={e => setDamageOnly(e.target.checked)} /> {t('insp.onlyProblems')}</label>
          <label className="check"><input type="checkbox" checked={disputedOnly} onChange={e => setDisputedOnly(e.target.checked)} /> {t('insp.onlyDisputed')}</label>
        </header>
        {openDisputes > 0 && <p className="warn-text">{t('insp.openDisputes', { n: openDisputes })}</p>}
        {rows.error && <LoadError error={rows.error} retry={rows.reload} />}
        {rows.data && rows.data.length === 0 && <p className="empty">{t('insp.noneFound')}</p>}
        {rows.data && rows.data.length > 0 && (
          <div className="table-wrap">
            <table className="grid">
              <thead><tr>
                <th>{t('clerk.when')}</th><th>{t('tosrec.appointment')}</th>{!company && <th>{t('common.company')}</th>}
                <th>{t('ticket.container')}</th><th>{t('insp.passage')}</th><th>{t('insp.sealLabel')}</th><th className="num">{t('insp.damage')}</th><th>{t('insp.disputeCol')}</th>
              </tr></thead>
              <tbody>
                {rows.data.map(r => (
                  <tr key={r.inspectionId} className={open === r.appointmentNo ? 'selected' : undefined} tabIndex={0}
                    onClick={() => setOpen(r.appointmentNo)} onKeyDown={e => { if (e.key === 'Enter') setOpen(r.appointmentNo); }}>
                    <td className="num">{stamp(r.inspectedAt, terminal.timeZone, lang)}</td>
                    <td className="nowrap"><bdi>{r.appointmentNo}</bdi></td>
                    {!company && <td>{r.companyName}</td>}
                    <td><bdi>{r.containerNo ?? '—'}</bdi></td>
                    <td>{t(r.stage === 'In' ? 'insp.atIn' : 'insp.atOut')}</td>
                    <td className={`seal seal-${r.sealStatus}`}>{t(sealText[r.sealStatus])}</td>
                    <td className="num">{r.damageCount || '—'}</td>
                    <td>{r.dispute ? t(disputeShortText[r.dispute]) : ''}</td>
                  </tr>
                ))}
              </tbody>
            </table>
          </div>
        )}
      </div>
      {open && (
        <Panel title={<span dir="ltr">{open}</span>} onClose={() => { setOpen(null); rows.reload(); }}>
          <InspectionSummary appointmentNo={open} />
        </Panel>
      )}
    </section>
  );
}
