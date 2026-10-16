import { useEffect, useState } from 'react';
import { createPortal } from 'react-dom';
import { ApiError } from '../lib/api.ts';
import { isCompanyRole } from '../lib/routes.ts';
import { stamp } from '../lib/time.ts';
import type {
  AdminResponse, AppointmentInspections, DamageEntry, DamageKind, DamageSeverity, DamageSide, Inspection, InspectionStage,
} from '../lib/types.ts';
import { useApp } from './context.tsx';
import { ActionButton, LoadError, Notice, useLoad } from './kit.tsx';

const sides: DamageSide[] = ['Front', 'Rear', 'Left', 'Right', 'Top', 'Underside', 'Inside', 'Door'];
const kinds: DamageKind[] = ['Dent', 'Hole', 'Cut', 'Scratch', 'Rust', 'Broken', 'Missing', 'Wet', 'Other'];

const sideText = {
  Front: 'insp.side.Front', Rear: 'insp.side.Rear', Left: 'insp.side.Left', Right: 'insp.side.Right', Top: 'insp.side.Top',
  Underside: 'insp.side.Underside', Inside: 'insp.side.Inside', Door: 'insp.side.Door',
} as const;
const kindText = {
  Dent: 'insp.kind.Dent', Hole: 'insp.kind.Hole', Cut: 'insp.kind.Cut', Scratch: 'insp.kind.Scratch', Rust: 'insp.kind.Rust',
  Broken: 'insp.kind.Broken', Missing: 'insp.kind.Missing', Wet: 'insp.kind.Wet', Other: 'insp.kind.Other',
} as const;
const sealText = {
  Ok: 'insp.seal.Ok', Mismatch: 'insp.seal.Mismatch', Missing: 'insp.seal.Missing', Broken: 'insp.seal.Broken', NotChecked: 'insp.seal.NotChecked',
} as const;
const disputeText = { Open: 'insp.dispute.Open', Accepted: 'insp.dispute.Accepted', Rejected: 'insp.dispute.Rejected' } as const;

const disputeShortText = { Open: 'insp.state.Open', Accepted: 'insp.state.Accepted', Rejected: 'insp.state.Rejected' } as const;

export { sealText, disputeText, disputeShortText };

/** A photo kept by Yardsyncs: fetched with the sign-in, shown from memory. */
export function AuthImage({ path, alt }: { path: string; alt: string }) {
  const { api } = useApp();
  const [url, setUrl] = useState<string | null>(null);
  useEffect(() => {
    let made: string | null = null;
    let live = true;
    api.download(path).then(({ blob }) => {
      if (!live) return;
      made = URL.createObjectURL(blob);
      setUrl(made);
    }).catch(() => setUrl(null));
    return () => { live = false; if (made) URL.revokeObjectURL(made); };
  }, [api, path]);
  return url ? <a href={url} target="_blank" rel="noreferrer"><img src={url} alt={alt} /></a> : <span className="photo-wait" aria-label={alt} />;
}

export function DamageLine({ d }: { d: { side: DamageSide | null; kind: DamageKind | null; severity: DamageSeverity | null; note?: string | null } }) {
  const { t } = useApp();
  return (
    <>
      {d.side && <strong>{t(sideText[d.side])}: </strong>}
      {d.kind ? t(kindText[d.kind]) : ''}
      {d.severity === 'Major' && <span className="major"> · {t('insp.major')}</span>}
      {d.note && <span dir="auto"> · {d.note}</span>}
    </>
  );
}

/**
 * A visit's inspections, read-only with the dispute flow: the company disputes an inspection, the terminal's planners
 * and admins accept or reject it with a note.
 */
export function InspectionSummary({ appointmentNo }: { appointmentNo: string }) {
  const { api, t, lang, user, terminal } = useApp();
  const base = `/api/terminals/${terminal.terminalId}`;
  const data = useLoad(() => api.get<AppointmentInspections>(`${base}/appointments/${encodeURIComponent(appointmentNo)}/inspections`), [base, appointmentNo]);
  const [error, setError] = useState<string | null>(null);
  const company = isCompanyRole(user.role);
  const settles = user.role === 'Planner' || user.role === 'TerminalAdmin' || user.role === 'OperatorAdmin' || user.role === 'SysAdmin';
  const x = data.data;
  if (data.error) return <LoadError error={data.error} retry={data.reload} />;
  if (!x) return null;
  if (x.inspections.length === 0) return <p className="muted">{t('insp.none')}</p>;

  return (
    <div className="insp-summary">
      {error && <Notice kind="error" onClose={() => setError(null)}>{error}</Notice>}
      {x.newAtGateOut.length > 0 && (
        <p className="warn-text">{t('insp.newAtOut')} {x.newAtGateOut.map((d, i) => (
          <span key={i} className="insp-new"><bdi>{d.containerNo ?? ''}</bdi> <DamageLine d={d} /></span>
        ))}</p>
      )}
      {x.inspections.map(i => (
        <section key={i.inspectionId} className="insp-card">
          <header>
            <strong>{t(i.stage === 'In' ? 'insp.atIn' : 'insp.atOut')}</strong>
            {i.containerNo && <bdi className="insp-container">{i.containerNo}</bdi>}
            <span className="muted">{stamp(i.inspectedAt, terminal.timeZone, lang)} · {t(i.source === 'Camera' ? 'insp.byCamera' : 'insp.byClerk')}</span>
          </header>
          <p className={`seal seal-${i.sealStatus}`}>
            {t('insp.sealLabel')} {t(sealText[i.sealStatus])}
            {i.sealSeen && <> · <bdi>{i.sealSeen}</bdi></>}
            {i.sealExpected && i.sealStatus === 'Mismatch' && <> ({t('insp.declared')} <bdi>{i.sealExpected}</bdi>)</>}
          </p>
          {i.damage.length === 0
            ? <p className="muted">{t('insp.noDamage')}</p>
            : <ul className="insp-damage">{i.damage.map((d, n) => (
              <li key={n}>
                <DamageLine d={d} />
                {d.photoRef && /^https:\/\//.test(d.photoRef) && <> · <a href={d.photoRef} target="_blank" rel="noreferrer">{t('insp.cameraPhoto')}</a></>}
              </li>
            ))}</ul>}
          {i.notes && <p dir="auto">{i.notes}</p>}
          {i.photoIds.length > 0 && (
            <ul className="photos">
              {i.photoIds.map((p, n) => <li key={p}><AuthImage path={`${base}/inspections/photos/${p}`} alt={t('clerk.photo', { n: n + 1 })} /></li>)}
            </ul>
          )}
          <DisputeBlock inspection={i} company={company} settles={settles} base={base} onDone={data.reload} onError={setError} />
        </section>
      ))}
    </div>
  );
}

function DisputeBlock({ inspection, company, settles, base, onDone, onError }: {
  inspection: Inspection; company: boolean; settles: boolean; base: string; onDone: () => void; onError: (m: string) => void;
}) {
  const { api, t } = useApp();
  const [text, setText] = useState('');
  const [open, setOpen] = useState(false);
  const d = inspection.dispute;
  if (d) {
    return (
      <div className={`dispute dispute-${d.status}`}>
        <p><strong>{t(disputeText[d.status])}</strong> <span dir="auto">{d.reason}</span></p>
        {d.resolutionNote && <p dir="auto">{t('insp.answer')} {d.resolutionNote}</p>}
        {d.status === 'Open' && settles && (
          <div className="inline-ask wrap">
            <input value={text} placeholder={t('insp.answerHint')} aria-label={t('insp.answerHint')} onChange={e => setText(e.target.value)} />
            {(['accept', 'reject'] as const).map(kind => (
              <ActionButton key={kind} kind={kind === 'accept' ? 'primary' : 'secondary'} disabled={!text.trim()} onError={onError} onRun={async () => {
                await api.post(`${base}/disputes/${d.disputeId}/resolve`, { accept: kind === 'accept', note: text.trim() });
                setText('');
                onDone();
              }}>{t(kind === 'accept' ? 'insp.accept' : 'insp.reject')}</ActionButton>
            ))}
          </div>
        )}
      </div>
    );
  }
  if (!company) return null;
  if (!open) return <button type="button" className="link-btn" onClick={() => setOpen(true)}>{t('insp.disputeThis')}</button>;
  return (
    <div className="inline-ask wrap">
      <input value={text} maxLength={600} placeholder={t('insp.disputeHint')} aria-label={t('insp.disputeHint')} onChange={e => setText(e.target.value)} />
      <ActionButton kind="primary" disabled={text.trim().length < 5} onError={onError} onRun={async () => {
        await api.post(`${base}/inspections/${inspection.inspectionId}/dispute`, { reason: text.trim() });
        setOpen(false);
        onDone();
      }}>{t('insp.sendDispute')}</ActionButton>
      <button type="button" className="link-btn" onClick={() => setOpen(false)}>{t('lanes.cancel')}</button>
    </div>
  );
}

interface Draft {
  sealSeen: string;
  sealBroken: boolean;
  sealMissing: boolean;
  notes: string;
  damage: DamageEntry[];
}

const blank: Draft = { sealSeen: '', sealBroken: false, sealMissing: false, notes: '', damage: [] };

/**
 * The clerk's inspection of a visit, over the page: choose the passage and the container, check the seal against the
 * declared one, record damage (side, kind, severity, note, photo) and take photos.
 */
export function InspectionEditor({ appointmentNo, stage: initialStage, onClose }: { appointmentNo: string; stage: InspectionStage; onClose: () => void }) {
  const { api, t, terminal } = useApp();
  const base = `/api/terminals/${terminal.terminalId}`;
  const path = `${base}/appointments/${encodeURIComponent(appointmentNo)}/inspections`;
  const data = useLoad(() => api.get<AppointmentInspections>(path), [path]);
  const [stage, setStage] = useState<InspectionStage>(initialStage);
  const [container, setContainer] = useState<string | null>(null);
  const [draft, setDraft] = useState<Draft | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [done, setDone] = useState<string | null>(null);

  useEffect(() => {
    const onKey = (e: KeyboardEvent) => { if (e.key === 'Escape') onClose(); };
    window.addEventListener('keydown', onKey);
    return () => window.removeEventListener('keydown', onKey);
  }, [onClose]);

  const x = data.data;
  const containers = (x?.items ?? []).filter(i => i.containerNo).map(i => i.containerNo!);
  const chosen = container ?? containers[0] ?? '';
  const item = x?.items.find(i => (i.containerNo ?? '') === chosen);
  const existing = x?.inspections.find(i => i.stage === stage && (i.containerNo ?? '') === chosen);
  const form: Draft = draft ?? (existing
    ? { sealSeen: existing.sealSeen ?? '', sealBroken: existing.sealStatus === 'Broken', sealMissing: existing.sealStatus === 'Missing', notes: existing.notes ?? '', damage: existing.damage }
    : blank);
  const set = (change: Partial<Draft>) => setDraft({ ...form, ...change });
  const pick = (next: () => void) => { setDraft(null); setDone(null); next(); };

  async function upload(file: File) {
    if (!existing) return;
    const body = new FormData();
    body.append('file', file);
    try {
      await api.postForm(`${base}/inspections/${existing.inspectionId}/photos`, body);
      data.reload();
    } catch (e) {
      setError(e instanceof ApiError ? e.message : t('common.offline'));
    }
  }

  return createPortal(
    <div className="ticket-sheet" role="dialog" aria-modal="true" aria-label={t('insp.title')}>
      <div className="ticket-paper insp-editor">
        <header className="ticket-head">
          <div>
            <p className="ticket-terminal">{x?.companyName}{x?.truckPlate && <> · <bdi className="plate">{x.truckPlate}</bdi></>}</p>
            <h2>{t('insp.title')}</h2>
          </div>
          <p className="ticket-no"><bdi>{appointmentNo}</bdi></p>
        </header>
        {data.error && <LoadError error={data.error} retry={data.reload} />}
        {error && <Notice kind="error" onClose={() => setError(null)}>{error}</Notice>}
        {done && <Notice kind="ok" onClose={() => setDone(null)}>{done}</Notice>}
        {x && (
          <form onSubmit={e => e.preventDefault()} className="form-stack">
            <div className="inline-form">
              <fieldset className="seg-group">
                <legend>{t('insp.passage')}</legend>
                {(['In', 'Out'] as const).map(s => (
                  <label key={s} className={`seg${stage === s ? ' seg-on' : ''}`}>
                    <input type="radio" name="stage" checked={stage === s} onChange={() => pick(() => setStage(s))} /> {t(s === 'In' ? 'insp.atIn' : 'insp.atOut')}
                  </label>
                ))}
              </fieldset>
              {containers.length > 1 && (
                <label className="field field-short"><span>{t('ticket.container')}</span>
                  <select dir="ltr" value={chosen} onChange={e => pick(() => setContainer(e.target.value))}>
                    {containers.map(c => <option key={c} value={c}>{c}</option>)}
                  </select></label>
              )}
              {containers.length === 1 && <p className="insp-container-one"><bdi>{chosen}</bdi></p>}
            </div>
            {existing?.source === 'Camera' && <p className="hint">{t('insp.fromCamera')}</p>}

            {chosen && (
              <div className="inline-form">
                <label className="field field-short"><span>{t('insp.sealSeen')}</span>
                  <input dir="ltr" value={form.sealSeen} maxLength={20} onChange={e => set({ sealSeen: e.target.value.toUpperCase() })} /></label>
                <p className="hint">{item?.sealNo ? <>{t('insp.declared')} <bdi>{item.sealNo}</bdi></> : t('insp.notDeclared')}</p>
                <label className="check"><input type="checkbox" checked={form.sealBroken} onChange={e => set({ sealBroken: e.target.checked })} /> {t('insp.sealBroken')}</label>
                <label className="check"><input type="checkbox" checked={form.sealMissing} onChange={e => set({ sealMissing: e.target.checked })} /> {t('insp.sealMissing')}</label>
              </div>
            )}

            <h3>{t('insp.damage')}</h3>
            {form.damage.length === 0 && <p className="muted">{t('insp.noDamage')}</p>}
            {form.damage.map((d, n) => (
              <div key={n} className="inline-form damage-row">
                <label className="field field-short"><span>{t('insp.side')}</span>
                  <select value={d.side ?? ''} onChange={e => set({ damage: form.damage.map((x2, k) => k === n ? { ...x2, side: (e.target.value || null) as DamageSide | null } : x2) })}>
                    <option value="">—</option>
                    {sides.map(s => <option key={s} value={s}>{t(sideText[s])}</option>)}
                  </select></label>
                <label className="field field-short"><span>{t('insp.kind')}</span>
                  <select value={d.kind} onChange={e => set({ damage: form.damage.map((x2, k) => k === n ? { ...x2, kind: e.target.value as DamageKind } : x2) })}>
                    {kinds.map(s => <option key={s} value={s}>{t(kindText[s])}</option>)}
                  </select></label>
                <label className="check"><input type="checkbox" checked={d.severity === 'Major'}
                  onChange={e => set({ damage: form.damage.map((x2, k) => k === n ? { ...x2, severity: e.target.checked ? 'Major' : 'Minor' } : x2) })} /> {t('insp.major')}</label>
                <label className="field grow"><span>{t('insp.note')}</span>
                  <input value={d.note ?? ''} maxLength={200} onChange={e => set({ damage: form.damage.map((x2, k) => k === n ? { ...x2, note: e.target.value } : x2) })} /></label>
                {existing && existing.photoIds.length > 0 && (
                  <label className="field field-short"><span>{t('insp.photo')}</span>
                    <select value={d.photoId ?? ''} onChange={e => set({ damage: form.damage.map((x2, k) => k === n ? { ...x2, photoId: e.target.value ? Number(e.target.value) : null } : x2) })}>
                      <option value="">—</option>
                      {existing.photoIds.map((p, k) => <option key={p} value={p}>{t('clerk.photo', { n: k + 1 })}</option>)}
                    </select></label>
                )}
                <button type="button" className="link-btn" onClick={() => set({ damage: form.damage.filter((_, k) => k !== n) })}>{t('lanes.remove')}</button>
              </div>
            ))}
            <button type="button" className="btn btn-secondary" onClick={() => set({ damage: [...form.damage, { side: null, kind: 'Dent', severity: 'Minor', note: null, photoRef: null, photoId: null }] })}>{t('insp.addDamage')}</button>

            <label className="field"><span>{t('insp.notes')}</span>
              <input value={form.notes} maxLength={400} onChange={e => set({ notes: e.target.value })} /></label>

            <h3>{t('insp.photos')}</h3>
            {existing
              ? (
                <>
                  {existing.photoIds.length > 0 && (
                    <ul className="photos">
                      {existing.photoIds.map((p, n) => <li key={p}><AuthImage path={`${base}/inspections/photos/${p}`} alt={t('clerk.photo', { n: n + 1 })} /></li>)}
                    </ul>
                  )}
                  <label className="btn btn-secondary file-btn">
                    {t('insp.takePhoto')}
                    <input type="file" accept="image/jpeg,image/png,image/webp" capture="environment" onChange={e => { const f = e.target.files?.[0]; if (f) void upload(f); e.target.value = ''; }} />
                  </label>
                </>
              )
              : <p className="hint">{t('insp.saveFirst')}</p>}

            <div className="actions ticket-actions">
              <ActionButton kind="primary" onError={setError} onRun={async () => {
                await api.post<AdminResponse<Inspection>>(path, {
                  stage,
                  containerNo: chosen || null,
                  sealSeen: form.sealSeen.trim() || null,
                  sealBroken: form.sealBroken,
                  sealMissing: form.sealMissing,
                  notes: form.notes.trim() || null,
                  damage: form.damage.map(d => ({ side: d.side, kind: d.kind, severity: d.severity, note: d.note?.trim() || null, photoRef: d.photoRef, photoId: d.photoId })),
                });
                setDraft(null);
                setDone(t('insp.saved'));
                data.reload();
              }}>{t('insp.save')}</ActionButton>
              <button type="button" className="btn btn-secondary" onClick={onClose}>{t('common.close')}</button>
            </div>
          </form>
        )}
      </div>
    </div>,
    document.body,
  );
}
