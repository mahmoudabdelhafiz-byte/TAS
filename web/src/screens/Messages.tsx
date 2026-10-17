import { useEffect, useRef, useState } from 'react';
import type { AdminResponse, ChatMessage, Conversation, QuickMessage } from '../lib/types.ts';
import { stamp } from '../lib/time.ts';
import { useApp } from '../ui/context.tsx';
import { LoadError, Notice, messageOf, useLoad } from '../ui/kit.tsx';

/**
 * Messages between the control room and drivers, one conversation per visit: the visits with messages (unread
 * first) and the open one. Ready-made messages reach the driver in their language; texts as written.
 */
export function Messages() {
  const { api, t, tx, lang, terminal, route, go } = useApp();
  const base = `/api/terminals/${terminal.terminalId}/messages`;
  const list = useLoad(() => api.get<Conversation[]>(base), [base], 20_000);
  const open = route.params.no ?? null;
  const [number, setNumber] = useState('');

  const choose = (no: string | null) => go('messages', { no: no ?? undefined });

  return (
    <section className="screen msg-screen">
      <header className="screen-head">
        <h1>{t('msg.title')}</h1>
        <form className="msg-open" onSubmit={e => { e.preventDefault(); if (number.trim()) { choose(number.trim().toUpperCase()); setNumber(''); } }}>
          <label className="field field-inline"><span>{t('msg.openNo')}</span>
            <input value={number} onChange={e => setNumber(e.target.value)} dir="ltr" autoComplete="off" /></label>
          <button type="submit" className="btn btn-secondary" disabled={!number.trim()}>{t('msg.open')}</button>
        </form>
      </header>

      {list.error && <LoadError error={list.error} retry={list.reload} />}
      <div className="msg-grid">
        <nav className="msg-list" aria-label={t('msg.title')}>
          {list.data && list.data.length === 0 && <p className="empty">{t('msg.none')}</p>}
          <ul>
            {(list.data ?? []).map(c => (
              <li key={c.appointmentNo}>
                <button type="button" className={c.appointmentNo === open ? 'msg-row active' : 'msg-row'} aria-current={c.appointmentNo === open ? 'true' : undefined}
                  onClick={() => choose(c.appointmentNo)}>
                  <span className="msg-row-head">
                    <span className="plate"><bdi>{c.truckPlate ?? c.appointmentNo}</bdi></span>
                    {c.unread > 0 && <span className="msg-unread">{t('msg.unread', { n: c.unread })}</span>}
                    <span className="msg-when num">{stamp(c.last.sentAt, terminal.timeZone, lang)}</span>
                  </span>
                  <span className="msg-row-who" dir="auto">{c.driverName ?? '—'} · {c.companyName}</span>
                  <span className={c.last.type === 'CallRequest' ? 'msg-row-text msg-call-text' : 'msg-row-text'} dir="auto">
                    {c.last.direction === 'ToDriver' ? `${t('msg.controlRoom')}: ` : ''}{c.last.text}
                  </span>
                  <span className="visually-hidden">{tx('status', c.status)}</span>
                </button>
              </li>
            ))}
          </ul>
        </nav>

        <div className="msg-thread-col">
          {open
            ? <Thread key={open} base={base} appointmentNo={open} conversation={list.data?.find(c => c.appointmentNo === open) ?? null} onSent={list.reload} />
            : <p className="empty">{t('msg.pick')}</p>}
        </div>
      </div>
    </section>
  );
}

function Thread({ base, appointmentNo, conversation, onSent }: {
  base: string;
  appointmentNo: string;
  conversation: Conversation | null;
  onSent: () => void;
}) {
  const { api, t, lang, terminal } = useApp();
  const path = `${base}/${encodeURIComponent(appointmentNo)}`;
  const thread = useLoad(() => api.get<AdminResponse<ChatMessage[]>>(path).then(r => r.data), [path], 15_000);
  const quick = useLoad(() => api.get<QuickMessage[]>(`${base}/templates`), [base]);
  const [text, setText] = useState('');
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const end = useRef<HTMLDivElement>(null);
  const count = thread.data?.length ?? 0;

  useEffect(() => { end.current?.scrollIntoView({ block: 'nearest' }); }, [count]);
  // Opening the conversation marked the driver's messages read: refresh the list's counts.
  useEffect(() => { if (thread.data) onSent(); }, [thread.data !== null]); // eslint-disable-line react-hooks/exhaustive-deps

  const send = async (body: { templateCode?: string; text?: string }) => {
    setBusy(true);
    setError(null);
    try {
      await api.post<AdminResponse<ChatMessage>>(path, body);
      if (body.text) setText('');
      thread.reload();
      onSent();
    } catch (e) {
      setError(messageOf(e, t('common.offline')));
    } finally {
      setBusy(false);
    }
  };

  const callRequested = thread.data?.some(m => m.type === 'CallRequest' && m.direction === 'FromDriver');

  return (
    <section className="msg-thread" aria-label={appointmentNo}>
      <header className="msg-thread-head">
        <h2><bdi>{appointmentNo}</bdi></h2>
        {conversation && (
          <p className="muted" dir="auto">
            {conversation.driverName ?? '—'} · <bdi>{conversation.truckPlate ?? '—'}</bdi> · {conversation.companyName}
          </p>
        )}
        {callRequested && conversation?.driverMobile && (
          <a className="btn btn-secondary msg-call" href={`tel:${conversation.driverMobile}`}>
            {t('msg.call', { mobile: '' }).trim()} <bdi dir="ltr">{conversation.driverMobile}</bdi>
          </a>
        )}
      </header>

      {thread.error && <LoadError error={thread.error} retry={thread.reload} />}
      <ol className="msg-bubbles">
        {thread.data && thread.data.length === 0 && <li className="empty">{t('msg.noneYet')}</li>}
        {(thread.data ?? []).map(m => (
          <li key={m.messageId} className={m.direction === 'ToDriver' ? 'msg-bubble from-room' : 'msg-bubble from-driver'}>
            <span className="msg-bubble-who">
              {m.direction === 'ToDriver' ? (m.senderName ?? t('msg.controlRoom')) : t('msg.driver')}
              {m.type === 'CallRequest' && <strong className="msg-call-text"> · {t('msg.callRequest')}</strong>}
            </span>
            <span className="msg-bubble-text" dir="auto">{m.text}</span>
            {m.point && (
              <a className="msg-map-link" href={`https://www.openstreetmap.org/?mlat=${m.point[1]}&mlon=${m.point[0]}#map=18/${m.point[1]}/${m.point[0]}`}
                target="_blank" rel="noreferrer">{t('msg.onMap')}</a>
            )}
            <span className="msg-bubble-when num">{stamp(m.sentAt, terminal.timeZone, lang)}</span>
          </li>
        ))}
      </ol>
      <div ref={end} />

      <div className="msg-compose">
        {error && <Notice kind="error" onClose={() => setError(null)}>{error}</Notice>}
        {quick.data && quick.data.length > 0 && (
          <div className="msg-quick" role="group" aria-label={t('msg.quick')}>
            {quick.data.map(q => (
              <button key={q.code} type="button" className="btn btn-secondary btn-small" dir="auto" disabled={busy} onClick={() => send({ templateCode: q.code })}>
                {q.text}
              </button>
            ))}
          </div>
        )}
        <form className="msg-write" onSubmit={e => { e.preventDefault(); if (text.trim()) void send({ text: text.trim() }); }}>
          <label className="field">
            <span>{t('msg.write')}</span>
            <textarea value={text} maxLength={500} rows={2} dir="auto" onChange={e => setText(e.target.value)}
              onKeyDown={e => { if (e.key === 'Enter' && !e.shiftKey) { e.preventDefault(); if (text.trim()) void send({ text: text.trim() }); } }} />
          </label>
          <button type="submit" className="btn btn-primary" disabled={busy || !text.trim()}>{t('msg.send')}</button>
        </form>
        <p className="hint">{t('msg.sentToPhone')}</p>
      </div>
    </section>
  );
}
