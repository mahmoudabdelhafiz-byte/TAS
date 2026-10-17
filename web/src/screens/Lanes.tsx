import { useState } from 'react';
import type { AdminResponse, Lane, LaneDevice, LaneDeviceKind, LaneIdentification, LaneMode, LaneRead } from '../lib/types.ts';
import { ApiError } from '../lib/api.ts';
import { stamp } from '../lib/time.ts';
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
 * The gate's lanes for terminal admins, and the keys for the terminal's own gate system. With Yardsyncs Autogate, each
 * lane also has its way of working (manual or automatic), how it identifies trucks, what it still needs to run
 * automatically, and the devices at it. Opening and closing lanes is on the gate screen.
 */
export function Lanes() {
  const { api, t, terminal } = useApp();
  const autogate = terminal.autogate ?? false;
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
        <h1>{t(autogate ? 'lanes.title' : 'lanes.titleTas')}</h1>
        <p className="hint">{t(autogate ? 'lanes.hint' : 'lanes.hintTas')}</p>
      </header>
      {done && <Notice kind="ok" onClose={() => setDone(null)}>{done}</Notice>}
      {error && <Notice kind="error" onClose={() => setError(null)}>{error}</Notice>}
      {list.error && <LoadError error={list.error} retry={list.reload} />}
      {list.data && list.data.length === 0 && <p className="empty">{t('lanes.none')}</p>}
      <div className="lane-cards">
        {(list.data ?? []).map(l => <LaneCard key={l.laneId} lane={l} autogate={autogate} base={`${base}/${l.laneId}`} onChanged={changed} onError={setError} />)}
      </div>
      <AddLane base={base} onChanged={changed} onError={setError} />
      <GateSystems onDone={setDone} onError={setError} />
      {autogate && <TosRecords onDone={setDone} onError={setError} />}
    </section>
  );
}

function LaneCard({ lane, autogate, base, onChanged, onError }: {
  lane: Lane; autogate: boolean; base: string; onChanged: (m?: string) => void; onError: (m: string) => void;
}) {
  const { api, t } = useApp();
  const [editing, setEditing] = useState<LaneDevice | 'new' | null>(null);
  const [key, setKey] = useState<DeviceKey | null>(null);
  async function issueKey(d: LaneDevice) {
    try {
      setKey((await api.post<AdminResponse<DeviceKey>>(`${base}/devices/${d.deviceId}/key`)).data);
      onChanged();
    } catch (e) {
      onError(e instanceof ApiError ? e.message : t('common.offline'));
    }
  }
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

      {autogate && <>
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
      </>}

      {!autogate ? null : lane.devices.length === 0
        ? <p className="muted">{t('lanes.noDevices')}</p>
        : (
          <div className="table-wrap">
            <table className="grid compact">
              <thead><tr>
                <th>{t('lanes.device')}</th><th>{t('lanes.kind')}</th><th>{t('lanes.reads')}</th>
                <th>{t('lanes.vendor')}</th><th>{t('lanes.address')}</th><th>{t('lanes.connection')}</th><th />
              </tr></thead>
              <tbody>
                {lane.devices.map(d => (
                  <tr key={d.deviceId} className={d.isActive ? undefined : 'inactive'}>
                    <td className="nowrap"><bdi>{d.deviceCode}</bdi>{!d.isActive && <span className="muted"> · {t('lanes.off')}</span>}</td>
                    <td>{t(kindText[d.kind])}</td>
                    <td>{d.reads.map(r => t(readText[r])).join(t('lanes.listSep')) || '—'}</td>
                    <td dir="auto">{[d.vendor, d.model].filter(Boolean).join(' ') || '—'}</td>
                    <td className="nowrap"><bdi>{d.address ?? '—'}</bdi></td>
                    <td><Connection device={d} /></td>
                    <td className="cell-action">
                      <button type="button" className="link-btn" onClick={() => issueKey(d)}>{t('lanes.key')}</button>
                      <button type="button" className="link-btn" onClick={() => setEditing(d)}>{t('lanes.edit')}</button>
                      <RemoveDevice url={`${base}/devices/${d.deviceId}`} code={d.deviceCode} onChanged={onChanged} onError={onError} />
                    </td>
                  </tr>
                ))}
              </tbody>
            </table>
          </div>
        )}

      {key && (
        <div className="device-key" role="status">
          <p><strong>{t('lanes.keyFor', { device: key.deviceCode })}</strong> {t('lanes.keyOnce')}</p>
          <code dir="ltr">{key.key}</code>
          <div className="actions">
            <button type="button" className="btn btn-secondary" onClick={() => navigator.clipboard?.writeText(key.key)}>{t('lanes.copy')}</button>
            <button type="button" className="link-btn" onClick={() => setKey(null)}>{t('lanes.keyDone')}</button>
          </div>
        </div>
      )}

      {editing
        ? <DeviceForm device={editing === 'new' ? null : editing} base={base} onError={onError}
            onDone={m => { setEditing(null); onChanged(m); }} onCancel={() => setEditing(null)} />
        : (
          <div className="lane-card-foot">
            {autogate && <button type="button" className="btn btn-secondary" onClick={() => setEditing('new')}>{t('lanes.addDevice')}</button>}
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

interface DeviceKey { deviceCode: string; key: string }

const connectionText = {
  NotConnected: 'lanes.conn.NotConnected', Online: 'lanes.conn.Online', Fault: 'lanes.conn.Fault', Offline: 'lanes.conn.Offline',
} as const;

/** Whether the device is talking to Yardsyncs, with when it was last heard from or what fault it reports. */
function Connection({ device }: { device: LaneDevice }) {
  const { t, lang, terminal } = useApp();
  const when = device.lastSeenAt ? stamp(device.lastSeenAt, terminal.timeZone, lang) : null;
  return (
    <span className={`conn conn-${device.connection}`} title={when ? t('lanes.lastSeen', { when }) : undefined}>
      {t(connectionText[device.connection])}
      {device.connection === 'Fault' && device.statusDetail && <span className="muted" dir="auto"> · {device.statusDetail}</span>}
      {device.connection === 'Offline' && when && <span className="muted"> · {when}</span>}
    </span>
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

interface GateSystem {
  keyId: number;
  name: string;
  keyHint: string;
  createdAt: string;
  lastUsedAt: string | null;
  revokedAt: string | null;
}

interface GateSystemKey { system: GateSystem; key: string }

/**
 * Keys for the terminal's own gate system (an OCR portal, kiosks, a gate operating system) to use the gate API. Each
 * key acts as a gate clerk of this terminal; the key is shown once.
 */
function GateSystems({ onDone, onError }: { onDone: (m: string) => void; onError: (m: string) => void }) {
  const { api, t, lang, terminal } = useApp();
  const base = `/api/terminals/${terminal.terminalId}/gate/systems`;
  const list = useLoad(() => api.get<GateSystem[]>(base), [base]);
  const [name, setName] = useState('');
  const [issued, setIssued] = useState<GateSystemKey | null>(null);
  const [asking, setAsking] = useState<number | null>(null);
  const rows = list.data ?? [];
  return (
    <section className="gate-systems" aria-labelledby="gate-systems">
      <h2 className="section-title" id="gate-systems">{t('gatesys.title')}</h2>
      <p className="hint">{t('gatesys.hint')}</p>
      {list.error && <LoadError error={list.error} retry={list.reload} />}
      {list.data && rows.length === 0 && <p className="muted">{t('gatesys.none')}</p>}
      {rows.length > 0 && (
        <div className="table-wrap">
          <table className="grid compact">
            <thead><tr><th>{t('gatesys.name')}</th><th>{t('gatesys.key')}</th><th>{t('gatesys.lastUsed')}</th><th /></tr></thead>
            <tbody>
              {rows.map(s => (
                <tr key={s.keyId} className={s.revokedAt ? 'inactive' : undefined}>
                  <td dir="auto">{s.name}</td>
                  <td className="nowrap"><bdi>{s.keyHint}</bdi></td>
                  <td className="num">{s.lastUsedAt ? stamp(s.lastUsedAt, terminal.timeZone, lang) : t('gatesys.never')}</td>
                  <td className="cell-action">
                    {s.revokedAt
                      ? <span className="muted">{t('gatesys.revoked', { when: stamp(s.revokedAt, terminal.timeZone, lang) })}</span>
                      : asking === s.keyId
                        ? (
                          <span className="inline-ask">
                            <ActionButton kind="danger" onError={onError} onRun={async () => {
                              await api.del(`${base}/${s.keyId}`);
                              setAsking(null);
                              list.reload();
                              onDone(t('gatesys.revokedDone', { name: s.name }));
                            }}>{t('gatesys.revokeYes', { name: s.name })}</ActionButton>
                            <button type="button" className="link-btn" onClick={() => setAsking(null)}>{t('lanes.keep')}</button>
                          </span>
                        )
                        : <button type="button" className="link-btn" onClick={() => setAsking(s.keyId)}>{t('gatesys.revoke')}</button>}
                  </td>
                </tr>
              ))}
            </tbody>
          </table>
        </div>
      )}
      {issued && (
        <div className="device-key" role="status">
          <p><strong>{t('gatesys.keyFor', { name: issued.system.name })}</strong> {t('gatesys.keyOnce')}</p>
          <code dir="ltr">{issued.key}</code>
          <div className="actions">
            <button type="button" className="btn btn-secondary" onClick={() => navigator.clipboard?.writeText(issued.key)}>{t('lanes.copy')}</button>
            <button type="button" className="link-btn" onClick={() => setIssued(null)}>{t('lanes.keyDone')}</button>
          </div>
        </div>
      )}
      <form className="inline-form" onSubmit={e => e.preventDefault()}>
        <label className="field"><span>{t('gatesys.name')}</span>
          <input dir="auto" maxLength={60} value={name} placeholder={t('gatesys.namePlaceholder')} onChange={e => setName(e.target.value)} /></label>
        <ActionButton kind="secondary" disabled={!name.trim()} onError={onError} onRun={async () => {
          const result = await api.post<AdminResponse<GateSystemKey>>(base, { name: name.trim() });
          setIssued(result.data);
          setName('');
          list.reload();
        }}>{t('gatesys.add')}</ActionButton>
      </form>
    </section>
  );
}

interface TosRecord {
  messageId: number;
  appointmentNo: string;
  stage: 'GateIn' | 'GateOut';
  laneCode: string | null;
  occurredAt: string;
  status: 'Queued' | 'Sent' | 'Failed';
  attempts: number;
  nextAttemptAt: string;
  sentAt: string | null;
  tosReference: string | null;
  lastError: string | null;
}

const recordStatusText = { Queued: 'tosrec.Queued', Sent: 'tosrec.Sent', Failed: 'tosrec.Failed' } as const;

/** Gate passages recorded in the TOS (terminals that record them): what is waiting or failed, sent again on demand. */
function TosRecords({ onDone, onError }: { onDone: (m: string) => void; onError: (m: string) => void }) {
  const { api, t, lang, terminal } = useApp();
  const base = `/api/terminals/${terminal.terminalId}/gate/tos-records`;
  const list = useLoad(() => api.get<TosRecord[]>(base), [base], 30_000);
  const rows = list.data ?? [];
  if (rows.length === 0) return null;
  const problems = rows.filter(r => r.status !== 'Sent').length;
  return (
    <section className="tos-records" aria-labelledby="tos-records">
      <div className="section-row">
        <h2 className="section-title" id="tos-records">{t('tosrec.title')}</h2>
        <ActionButton onError={onError} onRun={async () => {
          const result = await api.post<{ tried: number }>(`${base}/send`);
          list.reload();
          onDone(t('tosrec.sent', { n: result.tried }));
        }}>{t('tosrec.sendNow')}</ActionButton>
      </div>
      <p className={problems > 0 ? 'warn-text' : 'hint'}>{problems > 0 ? t('tosrec.problems', { n: problems }) : t('tosrec.allSent')}</p>
      <div className="table-wrap">
        <table className="grid compact">
          <thead><tr><th>{t('clerk.when')}</th><th>{t('tosrec.appointment')}</th><th>{t('tosrec.stage')}</th><th>{t('tosrec.status')}</th><th>{t('tosrec.reference')}</th><th /></tr></thead>
          <tbody>
            {rows.map(r => (
              <tr key={r.messageId}>
                <td className="num">{stamp(r.occurredAt, terminal.timeZone, lang)}</td>
                <td><bdi>{r.appointmentNo}</bdi>{r.laneCode && <span className="muted"> · <bdi>{r.laneCode}</bdi></span>}</td>
                <td>{t(r.stage === 'GateIn' ? 'tosrec.in' : 'tosrec.out')}</td>
                <td className={r.status === 'Failed' ? 'check-fail' : undefined}>{t(recordStatusText[r.status])}{r.status === 'Queued' && r.attempts > 0 ? ` (${r.attempts})` : ''}</td>
                <td dir="auto">{r.status === 'Sent' ? <bdi>{r.tosReference ?? '—'}</bdi> : r.lastError ?? '—'}</td>
                <td className="cell-action">
                  {r.status === 'Failed' && (
                    <ActionButton onError={onError} onRun={async () => {
                      await api.post(`${base}/${r.messageId}/retry`);
                      list.reload();
                    }}>{t('tosrec.retry')}</ActionButton>
                  )}
                </td>
              </tr>
            ))}
          </tbody>
        </table>
      </div>
    </section>
  );
}
