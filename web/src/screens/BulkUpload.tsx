import { useRef, useState } from 'react';
import { ApiError } from '../lib/api.ts';
import { isCompanyRole } from '../lib/routes.ts';
import type { Check, Company } from '../lib/types.ts';
import { useApp } from '../ui/context.tsx';
import { ActionButton, Notice, useLoad } from '../ui/kit.tsx';

interface BulkVisit {
  visit: string;
  rows: number[];
  slot: string;
  status: 'Booked' | 'WouldBook' | 'Failed';
  appointmentNo: string | null;
  errors: string[];
  checks: Check[];
}

interface BulkResult {
  dryRun: boolean;
  visits: number;
  succeeded: number;
  failed: number;
  fileErrors: { row: number; message: string }[];
  results: BulkVisit[];
}

/** Upload a sheet of truck visits: check it first (nothing booked), then book. */
export function BulkUpload() {
  const { api, t, user, terminal } = useApp();
  const base = `/api/terminals/${terminal.terminalId}/appointments/bulk`;
  const companyUser = isCompanyRole(user.role);
  const companies = useLoad(() => (companyUser ? Promise.resolve([] as Company[]) : api.get<Company[]>('/api/companies')), [companyUser]);
  const [companyId, setCompanyId] = useState<string>(user.companyId ? String(user.companyId) : '');
  const [file, setFile] = useState<File | null>(null);
  const [result, setResult] = useState<BulkResult | null>(null);
  const [error, setError] = useState<string | null>(null);
  const input = useRef<HTMLInputElement>(null);

  async function send(dryRun: boolean) {
    if (!file) return;
    setError(null);
    const form = new FormData();
    form.append('file', file, file.name);
    const query = `dryRun=${dryRun}${companyUser ? '' : `&companyId=${companyId}`}`;
    setResult(await api.postForm<BulkResult>(`${base}?${query}`, form));
  }

  async function template(format: 'xlsx' | 'csv') {
    try {
      const { blob, fileName } = await api.download(`${base}/template?format=${format}`);
      const url = URL.createObjectURL(blob);
      const a = document.createElement('a');
      a.href = url;
      a.download = fileName ?? `yardsyncs-bulk-booking.${format}`;
      a.click();
      setTimeout(() => URL.revokeObjectURL(url), 1000);
    } catch (e) {
      setError(e instanceof ApiError ? e.message : t('common.offline'));
    }
  }

  const booked = result && !result.dryRun;

  return (
    <section className="screen bulk">
      <header className="screen-head">
        <h1>{t('bulk.title')}</h1>
      </header>

      <ol className="bulk-steps">
        <li>
          <h2>{t('bulk.step1')}</h2>
          <p className="hint">{t('bulk.step1Hint')}</p>
          <div className="actions">
            <button type="button" className="btn btn-secondary" onClick={() => template('xlsx')}>{t('bulk.excel')}</button>
            <button type="button" className="btn btn-quiet" onClick={() => template('csv')}>{t('bulk.csv')}</button>
          </div>
        </li>
        <li>
          <h2>{t('bulk.step2')}</h2>
          {!companyUser && (
            <label className="field field-inline">
              <span>{t('common.company')}</span>
              <select value={companyId} onChange={e => { setCompanyId(e.target.value); setResult(null); }}>
                <option value="">{t('common.chooseCompany')}</option>
                {(companies.data ?? []).filter(c => c.status === 'Active').map(c => <option key={c.companyId} value={c.companyId}>{c.name}</option>)}
              </select>
            </label>
          )}
          <div className="file-pick">
            <input ref={input} type="file" accept=".xlsx,.csv,.txt" className="visually-hidden" id="bulk-file"
              onChange={e => { setFile(e.target.files?.[0] ?? null); setResult(null); }} />
            <label htmlFor="bulk-file" className="btn btn-secondary">{t('bulk.choose')}</label>
            <span className={file ? 'file-name' : 'muted'}>{file ? file.name : t('bulk.noFile')}</span>
          </div>
          <div className="actions">
            <ActionButton kind="primary" disabled={!file || !companyId} onError={setError} onRun={() => send(true)}>{t('bulk.check')}</ActionButton>
          </div>
        </li>
        <li>
          <h2>{t('bulk.step3')}</h2>
          <p className="hint">{t('bulk.step3Hint')}</p>
          <ActionButton kind="primary" disabled={!result || !result.dryRun || result.succeeded === 0} onError={setError} onRun={() => send(false)}>
            {result?.dryRun && result.succeeded > 0 ? t('bulk.book', { n: result.succeeded }) : t('bulk.bookDisabled')}
          </ActionButton>
        </li>
      </ol>

      {error && <Notice kind="error" onClose={() => setError(null)}>{error}</Notice>}

      {result && (
        <section className="bulk-result">
          <h2>
            {booked
              ? t('bulk.doneBooked', { ok: result.succeeded, n: result.visits })
              : t('bulk.doneChecked', { ok: result.succeeded, n: result.visits })}
          </h2>
          {result.fileErrors.length > 0 && (
            <Notice kind="error">
              <ul className="plain">{result.fileErrors.map((e, i) => <li key={i}>{e.row > 0 ? t('bulk.row', { n: e.row }) + ': ' : ''}{e.message}</li>)}</ul>
            </Notice>
          )}
          {result.results.length > 0 && (
            <div className="table-wrap">
              <table className="grid compact">
                <thead>
                  <tr><th>{t('bulk.rows')}</th><th>{t('bulk.visit')}</th><th>{t('many.hour')}</th><th>{t('many.result')}</th></tr>
                </thead>
                <tbody>
                  {result.results.map((v, i) => (
                    <tr key={i}>
                      <td className="num">{v.rows.join(', ')}</td>
                      <td><bdi>{v.visit}</bdi></td>
                      <td className="num nowrap"><bdi>{v.slot}</bdi></td>
                      <td>
                        {v.status === 'Failed' ? (
                          <span className="check-fail" dir="auto">
                            {[...v.errors, ...v.checks.filter(c => c.result === 'Fail').map(c => c.message ?? c.type)].join(' ')}
                          </span>
                        ) : v.status === 'Booked' ? (
                          <span className="ok-text"><bdi>{v.appointmentNo}</bdi></span>
                        ) : (
                          <span className="ok-text">{t('bulk.wouldBook')}</span>
                        )}
                      </td>
                    </tr>
                  ))}
                </tbody>
              </table>
            </div>
          )}
        </section>
      )}
    </section>
  );
}
