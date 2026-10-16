import { useEffect } from 'react';
import { createPortal } from 'react-dom';
import { stamp } from '../lib/time.ts';
import { useApp } from './context.tsx';
import { LoadError, useLoad } from './kit.tsx';

export interface GateTicketData {
  terminalName: string;
  appointmentNo: string;
  status: string;
  companyName: string;
  truckPlate: string | null;
  driverName: string | null;
  slotLocal: string;
  arrivedAt: string | null;
  gateInAt: string | null;
  gateOutAt: string | null;
  inLane: string | null;
  outLane: string | null;
  tosVisitReference: string | null;
  moves: { category: string; categoryAr: string; containerNo: string | null; isoType: string | null; reference: string | null; seals: string[] }[];
  damage: { photo: string | null; note: string | null; side: string | null }[];
  weightKg: number | null;
}

/**
 * The gate ticket (equipment interchange receipt) of a visit, over the page, ready to print: only the ticket goes to
 * the printer.
 */
export function GateTicket({ appointmentNo, onClose }: { appointmentNo: string; onClose: () => void }) {
  const { api, t, lang, terminal } = useApp();
  const ticket = useLoad(() => api.get<GateTicketData>(`/api/terminals/${terminal.terminalId}/gate/tickets/${encodeURIComponent(appointmentNo)}`), [appointmentNo]);
  useEffect(() => {
    const onKey = (e: KeyboardEvent) => { if (e.key === 'Escape') onClose(); };
    const done = () => document.body.classList.remove('print-ticket');
    window.addEventListener('keydown', onKey);
    window.addEventListener('afterprint', done);
    return () => { window.removeEventListener('keydown', onKey); window.removeEventListener('afterprint', done); done(); };
  }, [onClose]);
  const when = (iso: string | null) => (iso ? stamp(iso, terminal.timeZone, lang) : '—');
  const x = ticket.data;

  return createPortal(
    <div className="ticket-sheet" role="dialog" aria-modal="true" aria-label={t('ticket.title')}>
      <div className="ticket-paper">
        {ticket.error && <LoadError error={ticket.error} retry={ticket.reload} />}
        {x && (
          <>
            <header className="ticket-head">
              <div>
                <p className="ticket-terminal">{x.terminalName}</p>
                <h2>{t('ticket.title')}</h2>
              </div>
              <p className="ticket-no"><bdi>{x.appointmentNo}</bdi></p>
            </header>
            <dl className="ticket-facts">
              <div><dt>{t('common.company')}</dt><dd>{x.companyName}</dd></div>
              <div><dt>{t('appt.truck')}</dt><dd><bdi className="plate">{x.truckPlate ?? '—'}</bdi></dd></div>
              <div><dt>{t('ticket.driver')}</dt><dd>{x.driverName ?? '—'}</dd></div>
              <div><dt>{t('ticket.slot')}</dt><dd className="num"><bdi dir="ltr">{x.slotLocal}</bdi></dd></div>
              <div><dt>{t('ticket.in')}</dt><dd className="num">{when(x.gateInAt)}{x.inLane && <> · <bdi>{x.inLane}</bdi></>}</dd></div>
              <div><dt>{t('ticket.out')}</dt><dd className="num">{when(x.gateOutAt)}{x.outLane && <> · <bdi>{x.outLane}</bdi></>}</dd></div>
              <div><dt>{t('ticket.tosVisit')}</dt><dd><bdi>{x.tosVisitReference ?? t('ticket.notRecorded')}</bdi></dd></div>
              {x.weightKg !== null && <div><dt>{t('ticket.weight')}</dt><dd className="num"><bdi dir="ltr">{x.weightKg} kg</bdi></dd></div>}
            </dl>
            <table className="grid compact ticket-moves">
              <thead><tr><th>{t('ticket.move')}</th><th>{t('ticket.container')}</th><th>{t('ticket.reference')}</th><th>{t('ticket.seals')}</th></tr></thead>
              <tbody>
                {x.moves.map((m, i) => (
                  <tr key={i}>
                    <td>{lang === 'ar' ? m.categoryAr : m.category}</td>
                    <td><bdi>{m.containerNo ?? '—'}</bdi>{m.isoType && <span className="muted"> {m.isoType}</span>}</td>
                    <td><bdi>{m.reference ?? '—'}</bdi></td>
                    <td><bdi>{m.seals.join(', ') || '—'}</bdi></td>
                  </tr>
                ))}
              </tbody>
            </table>
            <h3>{t('ticket.damage')}</h3>
            {x.damage.length === 0
              ? <p>{t('ticket.noDamage')}</p>
              : <ul className="ticket-damage">{x.damage.map((d, i) => <li key={i} dir="auto">{[d.side, d.note].filter(Boolean).join(': ') || t('ticket.photoOnly')}</li>)}</ul>}
            <div className="ticket-sign">
              <p>{t('ticket.signDriver')}</p>
              <p>{t('ticket.signClerk')}</p>
            </div>
          </>
        )}
        <div className="actions ticket-actions">
          <button type="button" className="btn btn-primary" disabled={!x} onClick={() => { document.body.classList.add('print-ticket'); window.print(); }}>{t('ticket.print')}</button>
          <button type="button" className="btn btn-secondary" onClick={onClose}>{t('common.close')}</button>
        </div>
      </div>
    </div>,
    document.body,
  );
}
