import { useState } from 'react';
import type { AdminResponse, Lane, LaneDevice, LaneDeviceKind, LaneIdentification, LaneMode, LaneRead } from '../lib/types.ts';
import { ApiError } from '../lib/api.ts';
import { useApp } from '../ui/context.tsx';
import { ActionButton, LoadError, Notice, useLoad } from '../ui/kit.tsx';
import { laneStateText } from './Gate.tsx';

const kinds: LaneDeviceKind[] = ['Camera', 'Ocr', 'RfidReader', 'Kiosk', 'Barrier', 'TrafficLight', 'Display', 'Printer', 'Intercom', 'Weighbridge'];

const kindText = {
  Camera: 'lanes.kind.Camera', Ocr: 'lanes.kind.Ocr', RfidReader: 'lanes.kind.RfidReader', Kiosk: 'lanes.kind.Kiosk',
  Barrier: 'lanes.kind.Barrier', TrafficLight: 'lanes.kind.TrafficLight', Display: 'lanes.kind.Display',
  Printer: 'lanes.kind.Printer', Intercom: 'lanes.kind.Intercom', Weighbridge: 'lanes.kind.Weighbridge',
} as const;

const readText = {
  Plate: 'lanes.read.Plate', Container: 'lanes.read.Container', IsoCode: 'lanes.read.IsoCode',
  Seal: 'lanes.read.Seal', Damage: 'lanes.read.Damage', Tag: 'lanes.read.Tag',
} as const;

const identText = {
  AppointmentNo: 'lanes.by.AppointmentNo', GatePass: 'lanes.by.GatePass', Plate: 'lanes.by.Plate',
  Container: 'lanes.by.Container', RfidTag: 'lanes.by.RfidTag',
} as const;

const modeText = { Manual: 'lanes.mode.Manual', Automatic: 'lanes.mode.Automatic' } as const;

const problemText = { NoBarrier: 'lanes.problem.NoBarrier', NoIdentification: 'lanes.problem.NoIdentification' } as const;

/** What each kind of device can read (the server checks the same). */
export function readsFor(kind: LaneDeviceKind): LaneRead[] {
  if (kind === 'Camera' || kind === 'Ocr') return ['Plate', 'Container', 'IsoCode', 'Seal', 'Damage'];
  if (kind === 'RfidReader') return ['Tag'];
  return [];
}

/**
 * The Autogate lane model for terminal admins: every lane with its way of working (manual or automatic), how it can
 * identify trucks, what it still needs to run automatically, and the devices at it. Opening and closing lanes is on
 * the gate screen.
 */
export function Lanes() {
  const { api, t, terminal } = useApp();
  const base = `/api/terminals/${terminal.terminalId}/gate/lanes`;
  const list = useLoad(() => api.get<Lane[]>(base), [base]);
  const [error, setError] = useState<string | null>(null);
  const [done, setDone] = useState<string | null>(null);
  const changed = (message?: string) => {
    if (message) setDone(message);
    list.reload();
  };

  return (
    <section className="screen lanes">
      <header className="screen-head">
        <h1>{t('lanes.title')}</h1>
        <p className="hint">{t('lanes.hint')}</p>
      </header>
      {done && <Notice kind="ok" onClose={() => setDone(null)}>{done}</Notice>}
      {error && <Notice kind="error" onClose={() => setError(null)}>{error}</Notice>}
      {list.error && <LoadError error={list.error} retry={list.reload} />}
      {list.data && list.data.length === 0 && <p className="empty">{t('lanes.none')}</p>}
      <div className="lane-cards">
        {(list.data ?? []).map(l => <LaneCard key={l.laneId} lane={l} base={`${base}/${l.laneId}`} onChanged={changed} onError={setError} />)}
      </div>
      <AddLane base={base} onChanged={changed} onError={setError} />
    </section>
  );
}

function LaneCard({ lane, base, onChanged, onError }: {
  lane: Lane; base: string; onChanged: (m?: string) => void; onError: (m: string) => void;
}) {
  const { api, t } = useApp();
  const [editing, setEditing] = useState<LaneDevice | 'new' | null>(null);
  const needs = lane.automaticProblems.map(p => t(problemText[p])).join(t('lanes.and'));
  const setMode = (mode: LaneMode) => async () => {
    await api.put(base, { mode });
    onChanged(t(mode === 'Automatic' ? 'lanes.nowAutomatic' : 'lanes.nowManual', { lane: lane.laneCode }));
  };

  return (
    <article className={`lane-card${lane.isActive ? '' : ' inactive'}`} aria-labelledby={`lane-${lane.laneId}`}>
      <header className="lane-card-head">
        <h2 id={`lane-${lane.laneId}`}><bdi>{lane.laneCode}</bdi></h2>
        <span className="muted">{t('lanes.gate', { gate: lane.gateCode })} · {t(lane.direction === 'In' ? 'lanes.in' : 'lanes.out')}</span>
        <span className={`lane-pill lane-${lane.isActive ? lane.state : 'Retired'}`}>
          {lane.isActive ? t(laneStateText[lane.state]) : t('lanes.retired')}
        </span>
      </header>
      {lane.stateNote && <p className="lane-note" dir="auto">{lane.stateNote}</p>}

      <div className="lane-facts">
        <div className="lane-mode" role="group" aria-label={t('lanes.modeLabel')}>
          <span className="lane-fact-label">{t('lanes.modeLabel')}</span>
          {(['Manual', 'Automatic'] as const).map(m => (
            m === lane.mode
              ? <span key={m} className="seg seg-on" aria-current="true">{t(modeText[m])}</span>
              : <ActionButton key={m} onError={onError} disabled={!lane.isActive || (m === 'Automatic' && lane.automaticProblems.length > 0)}
                  title={m === 'Automatic' && lane.automaticProblems.length > 0 ? t('lanes.forAutomatic', { needs }) : undefined}
                  onRun={setMode(m)}>{t(modeText[m])}</ActionButton>
          ))}
        </div>
        <div>
          <span className="lane-fact-label">{t('lanes.identifies')}</span>
          {lane.identifies.length === 0
            ? <span className="warn-text">{t('lanes.identifiesNone')}</span>
            : <ul className="chips inline">{lane.identifies.map((i: LaneIdentification) => <li key={i}>{t(identText[i])}</li>)}</ul>}
        </div>
      </div>
      {lane.mode === 'Manual' && lane.automaticProblems.length > 0 && lane.isActive && (
        <p className="lane-needs">{t('lanes.forAutomatic', { needs })}</p>
      )}

      {lane.devices.length === 0
        ? <p className="muted">{t('lanes.noDevices')}</p>
        : (
          <div className="table-wrap">
            <table className="grid compact">
              <thead><tr>
                <th>{t('lanes.device')}</th><th>{t('lanes.kind')}</th><th>{t('lanes.reads')}</th>
                <th>{t('lanes.vendor')}</th><th>{t('lanes.address')}</th><th />
              </tr></thead>
              <tbody>
                {lane.devices.map(d => (
                  <tr key={d.deviceId} className={d.isActive ? undefined : 'inactive'}>
                    <td className="nowrap"><bdi>{d.deviceCode}</bdi>{!d.isActive && <span className="muted"> · {t('lanes.off')}</span>}</td>
                    <td>{t(kindText[d.kind])}</td>
                    <td>{d.reads.map(r => t(readText[r])).join(t('lanes.listSep')) || '—'}</td>
                    <td dir="auto">{[d.vendor, d.model].filter(Boolean).join(' ') || '—'}</td>
                    <td className="nowrap"><bdi>{d.address ?? '—'}</bdi></td>
                    <td className="cell-action">
                      <button type="button" className="link-btn" onClick={() => setEditing(d)}>{t('lanes.edit')}</button>
                      <RemoveDevice url={`${base}/devices/${d.deviceId}`} code={d.deviceCode} onChanged={onChanged} onError={onError} />
                    </td>
                  </tr>
                ))}
              </tbody>
            </table>
          </div>
        )}

      {editing
        ? <DeviceForm device={editing === 'new' ? null : editing} base={base} onError={onError}
            onDone={m => { setEditing(null); onChanged(m); }} onCancel={() => setEditing(null)} />
        : (
          <div className="lane-card-foot">
            <button type="button" className="btn btn-secondary" onClick={() => setEditing('new')}>{t('lanes.addDevice')}</button>
            <button type="button" className="link-btn" onClick={async () => {
              try {
                await api.put(base, { isActive: !lane.isActive });
                onChanged();
              } catch (e) {
                onError(e instanceof ApiError ? e.message : t('common.offline'));
              }
            }}>{t(lane.isActive ? 'lanes.retire' : 'lanes.restore')}</button>
          </div>
        )}
    </article>
  );
}

/** Remove asks once more, inline, before the device goes. */
function RemoveDevice({ url, code, onChanged, onError }: { url: string; code: string; onChanged: (m?: string) => void; onError: (m: string) => void }) {
  const { api, t } = useApp();
  const [asking, setAsking] = useState(false);
  if (!asking) return <button type="button" className="link-btn" onClick={() => setAsking(true)}>{t('lanes.remove')}</button>;
  return (
    <span className="inline-ask">
      <button type="button" className="btn btn-danger-quiet" onClick={async () => {
        try {
          await api.del(url);
          onChanged(t('lanes.removed', { device: code }));
        } catch (e) {
          setAsking(false);
          onError(e instanceof ApiError ? e.message : t('common.offline'));
        }
      }}>{t('lanes.removeYes', { device: code })}</button>
      <button type="button" className="link-btn" onClick={() => setAsking(false)}>{t('lanes.keep')}</button>
    </span>
  );
}

function DeviceForm({ device, base, onDone, onCancel, onError }: {
  device: LaneDevice | null; base: string; onDone: (m: string) => void; onCancel: () => void; onError: (m: string) => void;
}) {
  const { api, t } = useApp();
  const [form, setForm] = useState({
    deviceCode: device?.deviceCode ?? '',
    kind: device?.kind ?? ('Camera' as LaneDeviceKind),
    reads: device?.reads ?? ([] as LaneRead[]),
    vendor: device?.vendor ?? '',
    model: device?.model ?? '',
    address: device?.address ?? '',
    isActive: device?.isActive ?? true,
    notes: device?.notes ?? '',
  });
  const possible = readsFor(form.kind);
  const toggle = (r: LaneRead) =>
    setForm({ ...form, reads: form.reads.includes(r) ? form.reads.filter(x => x !== r) : [...form.reads, r] });

  return (
    <form className="device-form" onSubmit={e => e.preventDefault()}>
      <h3>{device ? t('lanes.editDevice', { device: device.deviceCode }) : t('lanes.addDevice')}</h3>
      <div className="inline-form">
        <label className="field field-short"><span>{t('lanes.device')}</span>
          <input dir="ltr" maxLength={30} value={form.deviceCode} placeholder="IN1-CAM-FRONT"
            onChange={e => setForm({ ...form, deviceCode: e.target.value.toUpperCase() })} /></label>
        <label className="field field-short"><span>{t('lanes.kind')}</span>
          <select value={form.kind} onChange={e => {
            const kind = e.target.value as LaneDeviceKind;
            setForm({ ...form, kind, reads: form.reads.filter(r => readsFor(kind).includes(r)) });
          }}>
            {kinds.map(k => <option key={k} value={k}>{t(kindText[k])}</option>)}
          </select></label>
        <label className="field field-short"><span>{t('lanes.vendor')}</span>
          <input maxLength={60} value={form.vendor} onChange={e => setForm({ ...form, vendor: e.target.value })} /></label>
        <label className="field field-short"><span>{t('lanes.model')}</span>
          <input maxLength={60} value={form.model} onChange={e => setForm({ ...form, model: e.target.value })} /></label>
        <label className="field"><span>{t('lanes.address')}</span>
          <input dir="ltr" maxLength={200} value={form.address} placeholder="10.0.4.21" onChange={e => setForm({ ...form, address: e.target.value })} /></label>
      </div>
      {possible.length > 0 && (
        <fieldset className="reads">
          <legend>{t('lanes.reads')}</legend>
          {possible.map(r => (
            <label key={r} className="check"><input type="checkbox" checked={form.reads.includes(r)} onChange={() => toggle(r)} /> {t(readText[r])}</label>
          ))}
        </fieldset>
      )}
      <div className="inline-form">
        <label className="field grow"><span>{t('lanes.notes')}</span>
          <input maxLength={200} value={form.notes} onChange={e => setForm({ ...form, notes: e.target.value })} /></label>
        <label className="check"><input type="checkbox" checked={form.isActive} onChange={e => setForm({ ...form, isActive: e.target.checked })} /> {t('lanes.inUse')}</label>
      </div>
      <div className="actions">
        <ActionButton kind="primary" disabled={!form.deviceCode.trim()} onError={onError} onRun={async () => {
          const body = { ...form, deviceCode: form.deviceCode.trim(), vendor: form.vendor || null, model: form.model || null, address: form.address || null, notes: form.notes || null };
          if (device) await api.put<AdminResponse<Lane>>(`${base}/devices/${device.deviceId}`, body);
          else await api.post<AdminResponse<Lane>>(`${base}/devices`, body);
          onDone(t('lanes.saved', { device: body.deviceCode }));
        }}>{t('lanes.save')}</ActionButton>
        <button type="button" className="link-btn" onClick={onCancel}>{t('lanes.cancel')}</button>
      </div>
    </form>
  );
}

function AddLane({ base, onChanged, onError }: { base: string; onChanged: (m?: string) => void; onError: (m: string) => void }) {
  const { api, t } = useApp();
  const [form, setForm] = useState({ gateCode: 'MAIN', laneCode: '', direction: 'In' as 'In' | 'Out' });
  return (
    <form className="inline-form add-lane" onSubmit={e => e.preventDefault()}>
      <h2 className="section-title">{t('lanes.addLane')}</h2>
      <label className="field field-short"><span>{t('onboard.gateCode')}</span>
        <input dir="ltr" maxLength={20} value={form.gateCode} onChange={e => setForm({ ...form, gateCode: e.target.value.toUpperCase() })} /></label>
      <label className="field field-short"><span>{t('onboard.laneCode')}</span>
        <input dir="ltr" maxLength={20} value={form.laneCode} placeholder="IN-3" onChange={e => setForm({ ...form, laneCode: e.target.value.toUpperCase() })} /></label>
      <label className="field field-short"><span>{t('onboard.direction')}</span>
        <select value={form.direction} onChange={e => setForm({ ...form, direction: e.target.value as 'In' | 'Out' })}>
          <option value="In">{t('onboard.direction.In')}</option>
          <option value="Out">{t('onboard.direction.Out')}</option>
        </select></label>
      <ActionButton kind="secondary" disabled={!form.gateCode.trim() || !form.laneCode.trim()} onError={onError} onRun={async () => {
        await api.post(base, { gateCode: form.gateCode.trim(), laneCode: form.laneCode.trim(), direction: form.direction });
        setForm({ ...form, laneCode: '' });
        onChanged(t('lanes.added', { lane: form.laneCode.trim() }));
      }}>{t('onboard.addLane')}</ActionButton>
    </form>
  );
}
