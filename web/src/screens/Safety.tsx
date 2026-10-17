import { useState } from 'react';
import { canChangeCapacity } from '../lib/routes.ts';
import type { AdminResponse, Company } from '../lib/types.ts';
import { useApp } from '../ui/context.tsx';
import { ActionButton, LoadError, Notice, useLoad } from '../ui/kit.tsx';

const LANGS = ['en', 'ar', 'ur', 'hi'] as const;
type DriverLang = typeof LANGS[number];
const langName: Record<DriverLang, string> = { en: 'English', ar: 'العربية', ur: 'اردو', hi: 'हिन्दी' };
const rtl = (l: DriverLang) => l === 'ar' || l === 'ur';

interface LocalizedText { title: string; body: string }
type Texts = Partial<Record<DriverLang, LocalizedText>>;

interface Campaign {
  campaignId: number;
  texts: Texts;
  imageUrl: string | null;
  startsOn: string;
  endsOn: string;
  companyIds: number[] | null;
  requireAck: boolean;
  archived: boolean;
  active: boolean;
  confirmed: number;
}

interface QuestionText { question: string; options: string[] }
interface Question { texts: Partial<Record<DriverLang, QuestionText>>; correct: number }
interface Induction {
  pages: { texts: Texts }[];
  questions: Question[];
  validMonths: number;
  version: number;
  updatedAt: string;
  validPasses: number;
}

/** Safety campaigns for drivers and the terminal's safety induction (planners and admins edit; other staff look). */
export function Safety() {
  const { t, user } = useApp();
  const edit = canChangeCapacity(user.role);
  return (
    <section className="screen safety">
      <header className="screen-head"><h1>{t('safety.title')}</h1></header>
      <p className="hint">{t('safety.lead')}</p>
      <Campaigns edit={edit} />
      <InductionEditor edit={edit} />
    </section>
  );
}

/** Language buttons for editing one language at a time; English first (required). */
function LangTabs({ value, onChange, filled }: { value: DriverLang; onChange: (l: DriverLang) => void; filled: (l: DriverLang) => boolean }) {
  return (
    <div className="segmented lang-tabs" role="tablist">
      {LANGS.map(l => (
        <button key={l} type="button" role="tab" aria-selected={value === l} className={value === l ? 'seg active' : 'seg'} onClick={() => onChange(l)}>
          {langName[l]}{filled(l) ? '' : ' ·'}
        </button>
      ))}
    </div>
  );
}

const cleanTexts = (texts: Texts): Texts =>
  Object.fromEntries(Object.entries(texts).filter(([, v]) => v && (v.title.trim() || v.body.trim()))) as Texts;

// ================================================================ campaigns

function Campaigns({ edit }: { edit: boolean }) {
  const { api, t, lang, terminal } = useApp();
  const base = `/api/terminals/${terminal.terminalId}/safety/campaigns`;
  const list = useLoad(() => api.get<Campaign[]>(base), [base]);
  const [editing, setEditing] = useState<Campaign | 'new' | null>(null);
  const [error, setError] = useState<string | null>(null);
  const shown = (c: Campaign) => c.texts[lang === 'ar' ? 'ar' : 'en'] ?? c.texts.en!;

  return (
    <section className="safety-section">
      <div className="section-head">
        <h2 className="section-title">{t('safety.campaigns')}</h2>
        {edit && editing === null && <button type="button" className="btn btn-primary" onClick={() => setEditing('new')}>{t('safety.newCampaign')}</button>}
      </div>
      {error && <Notice kind="error" onClose={() => setError(null)}>{error}</Notice>}
      {list.error && <LoadError error={list.error} retry={list.reload} />}
      {editing !== null && (
        <CampaignForm campaign={editing === 'new' ? null : editing} path={editing === 'new' ? base : `${base}/${editing.campaignId}`}
          onDone={() => { setEditing(null); list.reload(); }} onCancel={() => setEditing(null)} />
      )}
      {list.data && list.data.length === 0 && <p className="empty">{t('safety.noCampaigns')}</p>}
      <ul className="campaigns">
        {(list.data ?? []).map(c => (
          <li key={c.campaignId} className={c.active ? 'campaign' : 'campaign campaign-off'}>
            <div className="campaign-head">
              <strong dir="auto">{shown(c).title}</strong>
              <span className={c.active ? 'standing standing-Warning' : 'muted'}>{c.archived ? t('safety.archived') : c.active ? t('safety.running') : t('safety.notRunning')}</span>
              <span className="num muted">{c.startsOn} – {c.endsOn}</span>
            </div>
            <p dir="auto">{shown(c).body}</p>
            <p className="muted small">
              {c.companyIds ? t('safety.someCompanies', { n: c.companyIds.length }) : t('safety.everyone')}
              {' · '}{c.requireAck ? t('safety.confirmedN', { n: c.confirmed }) : t('safety.noAck')}
              {' · '}{LANGS.filter(l => c.texts[l]).map(l => langName[l]).join(', ')}
            </p>
            {edit && !c.archived && (
              <div className="decide-buttons">
                <button type="button" className="link-btn" onClick={() => setEditing(c)}>{t('safety.edit')}</button>
                <ActionButton kind="secondary" onError={setError} onRun={async () => { await api.post(`${base}/${c.campaignId}/archive`); list.reload(); }}>
                  {t('safety.end')}
                </ActionButton>
              </div>
            )}
          </li>
        ))}
      </ul>
    </section>
  );
}

function CampaignForm({ campaign, path, onDone, onCancel }: { campaign: Campaign | null; path: string; onDone: () => void; onCancel: () => void }) {
  const { api, t, today } = useApp();
  const companies = useLoad(() => api.get<Company[]>('/api/companies'), []);
  const [texts, setTexts] = useState<Texts>(campaign?.texts ?? { en: { title: '', body: '' } });
  const [tab, setTab] = useState<DriverLang>('en');
  const [imageUrl, setImageUrl] = useState(campaign?.imageUrl ?? '');
  const [startsOn, setStartsOn] = useState(campaign?.startsOn ?? today);
  const [endsOn, setEndsOn] = useState(campaign?.endsOn ?? today);
  const [requireAck, setRequireAck] = useState(campaign?.requireAck ?? true);
  const [some, setSome] = useState<number[] | null>(campaign?.companyIds ?? null);
  const [error, setError] = useState<string | null>(null);
  const current = texts[tab] ?? { title: '', body: '' };
  const set = (part: Partial<LocalizedText>) => setTexts({ ...texts, [tab]: { ...current, ...part } });

  return (
    <form className="safety-form form-stack" onSubmit={e => e.preventDefault()}>
      {error && <Notice kind="error" onClose={() => setError(null)}>{error}</Notice>}
      <LangTabs value={tab} onChange={setTab} filled={l => !!texts[l]?.title.trim()} />
      <label className="field"><span>{t('safety.noticeTitle')}</span>
        <input value={current.title} maxLength={120} dir={rtl(tab) ? 'rtl' : 'ltr'} onChange={e => set({ title: e.target.value })} /></label>
      <label className="field"><span>{t('safety.noticeBody')}</span>
        <textarea rows={4} maxLength={2000} value={current.body} dir={rtl(tab) ? 'rtl' : 'ltr'} onChange={e => set({ body: e.target.value })} /></label>
      <p className="hint">{t('safety.langHint')}</p>
      <div className="add-incident-row">
        <label className="field"><span>{t('safety.startsOn')}</span><input type="date" value={startsOn} onChange={e => setStartsOn(e.target.value)} /></label>
        <label className="field"><span>{t('safety.endsOn')}</span><input type="date" value={endsOn} min={startsOn} onChange={e => setEndsOn(e.target.value)} /></label>
        <label className="field safety-url"><span>{t('safety.picture')}</span>
          <input type="url" value={imageUrl} dir="ltr" placeholder="https://…" onChange={e => setImageUrl(e.target.value)} /></label>
      </div>
      <label className="check"><input type="checkbox" checked={requireAck} onChange={e => setRequireAck(e.target.checked)} /> {t('safety.requireAck')}</label>
      <fieldset className="plain-fieldset">
        <legend>{t('safety.who')}</legend>
        <label className="radio"><input type="radio" checked={some === null} onChange={() => setSome(null)} /> {t('safety.everyone')}</label>
        <label className="radio"><input type="radio" checked={some !== null} onChange={() => setSome([])} /> {t('safety.chosen')}</label>
        {some !== null && (
          <div className="company-picks">
            {(companies.data ?? []).map(c => (
              <label key={c.companyId} className="check">
                <input type="checkbox" checked={some.includes(c.companyId)}
                  onChange={e => setSome(e.target.checked ? [...some, c.companyId] : some.filter(x => x !== c.companyId))} /> {c.name}
              </label>
            ))}
          </div>
        )}
      </fieldset>
      <div className="decide-buttons">
        <ActionButton kind="primary" onError={setError} onRun={async () => {
          const body = { texts: cleanTexts(texts), imageUrl: imageUrl.trim() || null, startsOn, endsOn, companyIds: some, requireAck };
          await (campaign ? api.put<AdminResponse<Campaign>>(path, body) : api.post<AdminResponse<Campaign>>(path, body));
          onDone();
        }}>{campaign ? t('safety.save') : t('safety.publish')}</ActionButton>
        <button type="button" className="btn btn-quiet" onClick={onCancel}>{t('fleet.cancel')}</button>
      </div>
    </form>
  );
}

// ================================================================ induction

const emptyQuestion = (): Question => ({ texts: { en: { question: '', options: ['', ''] } }, correct: 0 });

function InductionEditor({ edit }: { edit: boolean }) {
  const { api, t, terminal } = useApp();
  const path = `/api/terminals/${terminal.terminalId}/safety/induction`;
  const settingsPath = `/api/terminals/${terminal.terminalId}/settings`;
  const induction = useLoad(() => api.get<Induction | null>(path).catch(() => null), [path]);
  const settings = useLoad(() => api.get<Record<string, number>>(settingsPath), [settingsPath]);
  const [draft, setDraft] = useState<Pick<Induction, 'pages' | 'questions' | 'validMonths'> | null>(null);
  const [tab, setTab] = useState<DriverLang>('en');
  const [error, setError] = useState<string | null>(null);
  const [done, setDone] = useState<string | null>(null);
  const saved = induction.data;
  const d = draft ?? (saved ? { pages: saved.pages, questions: saved.questions, validMonths: saved.validMonths } : null);
  const mode = settings.data?.induction_mode ?? 0;

  const setPage = (i: number, part: Partial<LocalizedText>) => {
    const pages = d!.pages.map((p, n) => (n === i ? { texts: { ...p.texts, [tab]: { ...(p.texts[tab] ?? { title: '', body: '' }), ...part } } } : p));
    setDraft({ ...d!, pages });
  };
  const setQuestion = (i: number, q: Question) => setDraft({ ...d!, questions: d!.questions.map((x, n) => (n === i ? q : x)) });

  return (
    <section className="safety-section">
      <div className="section-head">
        <h2 className="section-title">{t('safety.induction')}</h2>
        {saved && <span className="muted">{t('safety.inductionState', { version: saved.version, passes: saved.validPasses })}</span>}
      </div>
      <p className="hint">{t('safety.inductionLead')}</p>
      {error && <Notice kind="error" onClose={() => setError(null)}>{error}</Notice>}
      {done && <Notice kind="ok" onClose={() => setDone(null)}>{done}</Notice>}

      {settings.data && (
        <fieldset className="plain-fieldset">
          <legend>{t('safety.atGate')}</legend>
          {[0, 1, 2].map(m => (
            <label key={m} className="radio">
              <input type="radio" name="induction_mode" checked={mode === m} disabled={!edit} onChange={async () => {
                try {
                  await api.put(settingsPath, { values: { induction_mode: m }, reason: null });
                  settings.reload();
                } catch (e) {
                  setError(e instanceof Error ? e.message : t('common.offline'));
                }
              }} /> {t(m === 0 ? 'safety.mode0' : m === 1 ? 'safety.mode1' : 'safety.mode2')}
            </label>
          ))}
        </fieldset>
      )}

      {!d && edit && (
        <button type="button" className="btn btn-primary" onClick={() => setDraft({ pages: [{ texts: { en: { title: '', body: '' } } }], questions: [emptyQuestion()], validMonths: 12 })}>
          {t('safety.createInduction')}
        </button>
      )}
      {!d && !edit && <p className="empty">{t('safety.noInduction')}</p>}

      {d && (
        <form className="safety-form form-stack" onSubmit={e => e.preventDefault()}>
          <LangTabs value={tab} onChange={setTab} filled={l => d.pages.every(p => !!p.texts[l]?.title.trim())} />
          <h3 className="section-title">{t('safety.pages')}</h3>
          {d.pages.map((p, i) => (
            <div key={i} className="induction-item">
              <label className="field"><span>{t('safety.pageN', { n: i + 1 })}</span>
                <input value={p.texts[tab]?.title ?? ''} disabled={!edit} dir={rtl(tab) ? 'rtl' : 'ltr'} placeholder={t('safety.noticeTitle')}
                  onChange={e => setPage(i, { title: e.target.value })} /></label>
              <textarea rows={3} value={p.texts[tab]?.body ?? ''} disabled={!edit} dir={rtl(tab) ? 'rtl' : 'ltr'} aria-label={t('safety.noticeBody')}
                onChange={e => setPage(i, { body: e.target.value })} />
              {edit && d.pages.length > 1 && (
                <button type="button" className="link-btn" onClick={() => setDraft({ ...d, pages: d.pages.filter((_, n) => n !== i) })}>{t('safety.remove')}</button>
              )}
            </div>
          ))}
          {edit && d.pages.length < 20 && (
            <button type="button" className="btn btn-secondary btn-small" onClick={() => setDraft({ ...d, pages: [...d.pages, { texts: { en: { title: '', body: '' } } }] })}>{t('safety.addPage')}</button>
          )}

          <h3 className="section-title">{t('safety.questions')}</h3>
          {d.questions.map((q, i) => {
            const en = q.texts.en ?? { question: '', options: ['', ''] };
            const text = q.texts[tab] ?? { question: '', options: en.options.map(() => '') };
            const setText = (part: Partial<QuestionText>) => setQuestion(i, { ...q, texts: { ...q.texts, [tab]: { ...text, ...part } } });
            return (
              <div key={i} className="induction-item">
                <label className="field"><span>{t('safety.questionN', { n: i + 1 })}</span>
                  <input value={text.question} disabled={!edit} dir={rtl(tab) ? 'rtl' : 'ltr'} onChange={e => setText({ question: e.target.value })} /></label>
                {en.options.map((_, o) => (
                  <label key={o} className="radio option-row">
                    <input type="radio" name={`correct-${i}`} checked={q.correct === o} disabled={!edit} onChange={() => setQuestion(i, { ...q, correct: o })}
                      aria-label={t('safety.rightAnswer')} />
                    <input value={text.options[o] ?? ''} disabled={!edit} dir={rtl(tab) ? 'rtl' : 'ltr'} placeholder={t('safety.answerN', { n: o + 1 })}
                      onChange={e => setText({ options: en.options.map((__, k) => (k === o ? e.target.value : text.options[k] ?? '')) })} />
                  </label>
                ))}
                {edit && tab === 'en' && (
                  <div className="decide-buttons">
                    {en.options.length < 6 && <button type="button" className="link-btn" onClick={() => setQuestion(i, {
                      ...q, texts: Object.fromEntries(Object.entries(q.texts).map(([l, v]) => [l, { ...v!, options: [...v!.options, ''] }])),
                    })}>{t('safety.addAnswer')}</button>}
                    {d.questions.length > 1 && <button type="button" className="link-btn" onClick={() => setDraft({ ...d, questions: d.questions.filter((_, n) => n !== i) })}>{t('safety.remove')}</button>}
                  </div>
                )}
              </div>
            );
          })}
          <p className="hint">{t('safety.rightHint')}</p>
          {edit && d.questions.length < 20 && (
            <button type="button" className="btn btn-secondary btn-small" onClick={() => setDraft({ ...d, questions: [...d.questions, emptyQuestion()] })}>{t('safety.addQuestion')}</button>
          )}
          <label className="field field-inline"><span>{t('safety.validMonths')}</span>
            <input type="number" min={1} max={60} value={d.validMonths} disabled={!edit} onChange={e => setDraft({ ...d, validMonths: Number(e.target.value) })} /></label>
          {edit && (
            <ActionButton kind="primary" disabled={draft === null} onError={setError} onRun={async () => {
              await api.put<AdminResponse<Induction>>(path, {
                pages: d.pages.map(p => ({ texts: cleanTexts(p.texts) })),
                questions: d.questions.map(q => ({
                  correct: q.correct,
                  texts: Object.fromEntries(Object.entries(q.texts).filter(([, v]) => v && v.question.trim())),
                })),
                validMonths: d.validMonths,
              });
              setDraft(null);
              induction.reload();
              setDone(t('safety.inductionSaved'));
            }}>{t('safety.saveInduction')}</ActionButton>
          )}
        </form>
      )}
    </section>
  );
}
