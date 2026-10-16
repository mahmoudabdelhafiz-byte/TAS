import { useEffect, useMemo, useRef, useState, type FormEvent, type KeyboardEvent } from 'react';
import { ApiError } from '../lib/api.ts';
import type { TextKey } from '../lib/i18n.ts';
import { parseMarkdown } from '../lib/markdown.ts';
import { isCompanyRole } from '../lib/routes.ts';
import { useApp } from './context.tsx';
import { BlockView } from './Markdown.tsx';

export interface AssistantInfo { enabled: boolean; liveData: boolean; dailyQuestions: number }
interface ChatAnswer { text: string; lookedUp: string[]; questionsLeft: number }
interface Turn { role: 'user' | 'assistant'; text: string; lookedUp?: string[]; error?: boolean }

const toolText: Record<string, TextKey> = {
  get_appointment: 'assistant.tool.get_appointment',
  list_appointments: 'assistant.tool.list_appointments',
  get_free_hours: 'assistant.tool.get_free_hours',
  track_containers: 'assistant.tool.track_containers',
  list_move_types: 'assistant.tool.list_move_types',
  terminal_now: 'assistant.tool.terminal_now',
};

/** The AI assistant: a chat panel on every page. The conversation lives in this tab only. */
export function Assistant({ info }: { info: AssistantInfo }) {
  const { api, t, lang, user, terminal } = useApp();
  const [open, setOpen] = useState(false);
  const [turns, setTurns] = useState<Turn[]>([]);
  const [text, setText] = useState('');
  const [busy, setBusy] = useState(false);
  const [left, setLeft] = useState<number | null>(null);
  const input = useRef<HTMLTextAreaElement>(null);
  const end = useRef<HTMLDivElement>(null);

  const suggestions: TextKey[] = isCompanyRole(user.role)
    ? ['assistant.s.c1', 'assistant.s.c2', 'assistant.s.c3']
    : ['assistant.s.s1', 'assistant.s.s2', 'assistant.s.s3'];

  useEffect(() => { if (open) input.current?.focus(); }, [open]);
  useEffect(() => { end.current?.scrollIntoView({ block: 'end' }); }, [turns, busy]);

  async function ask(question: string) {
    const q = question.trim();
    if (!q || busy) return;
    const history = [...turns.filter(x => !x.error), { role: 'user' as const, text: q }];
    setTurns(x => [...x, { role: 'user', text: q }]);
    setText('');
    setBusy(true);
    try {
      const answer = await api.post<ChatAnswer>(`/api/terminals/${terminal.terminalId}/assistant`, {
        lang,
        messages: history.slice(-20).map(x => ({ role: x.role, text: x.text })),
      });
      setTurns(x => [...x, { role: 'assistant', text: answer.text, lookedUp: answer.lookedUp }]);
      setLeft(answer.questionsLeft);
    } catch (err) {
      setTurns(x => [...x, { role: 'assistant', text: err instanceof ApiError ? err.message : t('common.offline'), error: true }]);
    } finally {
      setBusy(false);
    }
  }

  function submit(e: FormEvent) {
    e.preventDefault();
    void ask(text);
  }

  function keys(e: KeyboardEvent<HTMLTextAreaElement>) {
    if (e.key === 'Enter' && !e.shiftKey && !e.nativeEvent.isComposing) {
      e.preventDefault();
      void ask(text);
    }
  }

  if (!open) {
    return (
      <button type="button" className="assistant-fab no-print" onClick={() => setOpen(true)} aria-haspopup="dialog">
        <ChatIcon /><span>{t('assistant.open')}</span>
      </button>
    );
  }

  return (
    <section className="assistant no-print" role="dialog" aria-label={t('assistant.title')}
      onKeyDown={e => { if (e.key === 'Escape') setOpen(false); }}>
      <header className="assistant-head">
        <h2><ChatIcon />{t('assistant.title')}</h2>
        <div className="assistant-head-end">
          {turns.length > 0 && <button type="button" className="link-btn" onClick={() => { setTurns([]); setLeft(null); }}>{t('assistant.new')}</button>}
          <button type="button" className="assistant-close" onClick={() => setOpen(false)} aria-label={t('common.close')}>×</button>
        </div>
      </header>

      <div className="assistant-body" aria-live="polite">
        {turns.length === 0 && (
          <div className="assistant-start">
            <p className="hint">{info.liveData ? t('assistant.note') : t('assistant.noteDocsOnly')}</p>
            <ul className="assistant-suggestions">
              {suggestions.map(s => <li key={s}><button type="button" onClick={() => void ask(t(s))}>{t(s)}</button></li>)}
            </ul>
          </div>
        )}
        {turns.map((turn, i) => <TurnView key={i} turn={turn} />)}
        {busy && <p className="assistant-busy">{t('assistant.thinking')}</p>}
        <div ref={end} />
      </div>

      <form className="assistant-form" onSubmit={submit}>
        <textarea ref={input} rows={2} value={text} maxLength={4000} placeholder={t('assistant.placeholder')} dir="auto"
          onChange={e => setText(e.target.value)} onKeyDown={keys} aria-label={t('assistant.placeholder')} />
        <button type="submit" className="btn btn-primary" disabled={busy || !text.trim()}>{t('assistant.send')}</button>
      </form>
      {left !== null && <p className="assistant-left">{t('assistant.left', { n: left })}</p>}
    </section>
  );
}

function TurnView({ turn }: { turn: Turn }) {
  const { t } = useApp();
  const blocks = useMemo(() => (turn.role === 'assistant' && !turn.error ? parseMarkdown(turn.text) : []), [turn]);
  if (turn.role === 'user') return <p className="assistant-turn assistant-user" dir="auto">{turn.text}</p>;
  if (turn.error) return <p className="assistant-turn assistant-error" role="alert">{turn.text}</p>;
  const looked = [...new Set(turn.lookedUp ?? [])].map(x => (toolText[x] ? t(toolText[x]) : x));
  return (
    <div className="assistant-turn assistant-reply" dir="auto">
      {blocks.map((b, i) => <BlockView key={i} block={b} />)}
      {looked.length > 0 && <p className="assistant-looked">{t('assistant.lookedUp', { what: looked.join(t('common.sep')) })}</p>}
    </div>
  );
}

function ChatIcon() {
  return (
    <svg width="20" height="20" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="1.7" strokeLinecap="round" strokeLinejoin="round" aria-hidden="true">
      <path d="M4 5h16v11H9l-5 4z" /><path d="M8 9.5h8M8 12.5h5" />
    </svg>
  );
}
