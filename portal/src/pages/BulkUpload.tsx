import { useState, type FormEvent } from 'react';
import { useQuery, useQueryClient } from '@tanstack/react-query';
import { Link } from 'react-router';
import { api, download } from '../api/client';
import type { BulkResult, BulkVisitResult, Company } from '../api/types';
import { isCompanyRole, useUser } from '../auth/Auth';
import { CheckList } from '../booking/CheckList';
import { Notice } from '../components/ui';
import { useI18n } from '../i18n/I18n';
import type { MessageKey } from '../i18n/en';
import { useTerminal } from '../terminals/Terminals';

const resultKeys: Record<BulkVisitResult['status'], MessageKey> = {
  WouldBook: 'wouldBook',
  Booked: 'bookedResult',
  Failed: 'failedResult',
};

/**
 * Upload a booking sheet: first a dry run that checks every visit (TOS, capacity, fleet) and books nothing,
 * then the real run with the same file. Each visit is booked on its own, so one problem doesn't block the rest.
 */
export function BulkUploadPage() {
  const { t } = useI18n();
  const user = useUser();
  const terminal = useTerminal();
  const queries = useQueryClient();
  const staff = !isCompanyRole(user.role);
  const base = `/api/terminals/${terminal.terminalId}/appointments/bulk`;

  const [companyId, setCompanyId] = useState<number | null>(user.companyId);
  const [file, setFile] = useState<File | null>(null);
  const [inputKey, setInputKey] = useState(0);
  const [result, setResult] = useState<BulkResult | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [busy, setBusy] = useState<'check' | 'book' | null>(null);

  const companies = useQuery({ queryKey: ['companies'], queryFn: () => api<Company[]>('/api/companies'), enabled: staff });

  async function run(dryRun: boolean) {
    if (!file || companyId === null) return;
    setError(null);
    setBusy(dryRun ? 'check' : 'book');
    const form = new FormData();
    form.append('file', file);
    try {
      const next = await api<BulkResult>(base, { form, query: { dryRun, companyId: staff ? companyId : undefined } });
      setResult(next);
      if (!dryRun) await queries.invalidateQueries({ queryKey: ['appointments'] });
    } catch (err) {
      setError((err as Error).message);
    } finally {
      setBusy(null);
    }
  }

  function reset() {
    setResult(null);
    setFile(null);
    setError(null);
    setInputKey((k) => k + 1);
  }

  const submit = (event: FormEvent) => {
    event.preventDefault();
    void run(true);
  };

  return (
    <>
      <div className="page-head">
        <div>
          <h1>{t('uploadSheet')}</h1>
          <p className="muted" style={{ maxInlineSize: '60ch' }}>
            {t('uploadIntro')}
          </p>
        </div>
        <Link to="/book">{t('bookTitle')}</Link>
      </div>

      {!result && (
        <section className="panel">
          <form className="panel-body" onSubmit={submit}>
            <div className="form-actions" style={{ alignItems: 'center' }}>
              <span className="muted">{t('downloadTemplate')}:</span>
              <button type="button" className="button quiet" onClick={() => void download(`${base}/template?format=xlsx`, 'yardsyncs-booking-template.xlsx').catch((e: Error) => setError(e.message))}>
                {t('templateExcel')}
              </button>
              <button type="button" className="button quiet" onClick={() => void download(`${base}/template?format=csv`, 'yardsyncs-booking-template.csv').catch((e: Error) => setError(e.message))}>
                {t('templateCsv')}
              </button>
            </div>
            {staff && (
              <div className="field" style={{ maxInlineSize: '24rem' }}>
                <label htmlFor="bulk-company">{t('company')}</label>
                <select id="bulk-company" value={companyId ?? ''} onChange={(e) => setCompanyId(e.target.value ? Number(e.target.value) : null)}>
                  <option value="">{t('chooseCompany')}</option>
                  {(companies.data ?? [])
                    .filter((c) => c.status === 'Active')
                    .map((c) => (
                      <option key={c.companyId} value={c.companyId}>
                        {c.name}
                      </option>
                    ))}
                </select>
              </div>
            )}
            <div className="field" style={{ maxInlineSize: '32rem' }}>
              <label htmlFor="bulk-file">{t('chooseFile')}</label>
              <input
                key={inputKey}
                id="bulk-file"
                type="file"
                accept=".xlsx,.csv,application/vnd.openxmlformats-officedocument.spreadsheetml.sheet,text/csv"
                onChange={(e) => setFile(e.target.files?.[0] ?? null)}
              />
            </div>
            {error && <Notice kind="error">{error}</Notice>}
            <div>
              <button type="submit" className="button" disabled={!file || companyId === null || busy !== null}>
                {busy === 'check' ? t('checkingSheet') : t('checkSheet')}
              </button>
            </div>
          </form>
        </section>
      )}

      {result && <BulkResultView result={result} busy={busy} error={error} onBook={() => void run(false)} onReset={reset} />}
    </>
  );
}

function BulkResultView({
  result,
  busy,
  error,
  onBook,
  onReset,
}: {
  result: BulkResult;
  busy: 'check' | 'book' | null;
  error: string | null;
  onBook: () => void;
  onReset: () => void;
}) {
  const { t } = useI18n();
  const ready = result.results.filter((r) => r.status === 'WouldBook').length;

  if (result.fileErrors.length > 0 && result.visits === 0) {
    return (
      <>
        <Notice kind="error">
          <p>{t('sheetProblems')}</p>
          <ul style={{ margin: '0.5rem 0 0', paddingInlineStart: '1.25rem' }}>
            {result.fileErrors.map((e, i) => (
              <li key={i}>
                {e.row > 0 && <strong>{t('rowN', { n: e.row })}: </strong>}
                <span lang="en">{e.message}</span>
              </li>
            ))}
          </ul>
        </Notice>
        <div>
          <button type="button" className="button secondary" onClick={onReset}>
            {t('uploadAnother')}
          </button>
        </div>
      </>
    );
  }

  return (
    <>
      <div className="page-head">
        <h2>{result.dryRun ? t('previewTitle') : t('resultSummary', { ok: result.succeeded, total: result.visits })}</h2>
        {result.dryRun && <p>{t('previewSummary', { ok: result.succeeded, total: result.visits })}</p>}
      </div>
      {error && <Notice kind="error">{error}</Notice>}
      <div className="table-wrap">
        <table className="stack-simple">
          <thead>
            <tr>
              <th scope="col">{t('colVisit')}</th>
              <th scope="col">{t('colRows')}</th>
              <th scope="col">{t('colSlot')}</th>
              <th scope="col">{t('colResult')}</th>
            </tr>
          </thead>
          <tbody>
            {result.results.map((r) => (
              <tr key={r.visit} data-result={r.status}>
                <td data-label={t('colVisit')}>
                  {r.appointmentNo ? (
                    <Link className="code" to={`/appointments/${r.appointmentNo}`}>
                      {r.appointmentNo}
                    </Link>
                  ) : (
                    <span className="code">{r.visit}</span>
                  )}
                </td>
                <td data-label={t('colRows')} className="code">
                  {r.rows.join(', ')}
                </td>
                <td data-label={t('colSlot')} className="code">
                  {r.slot}
                </td>
                <td data-label={t('colResult')}>
                  <span className={`tag ${r.status === 'Failed' ? 'off' : 'ok'}`}>{t(resultKeys[r.status])}</span>
                  {/* Failed checks say it by name; plain errors only when there is no check behind them. */}
                  {r.checks.some((c) => c.result !== 'Pass') ? (
                    <CheckList checks={r.checks} onlyProblems />
                  ) : (
                    r.errors.map((e, i) => (
                      <span key={i} className="sub" lang="en">
                        {e}
                      </span>
                    ))
                  )}
                </td>
              </tr>
            ))}
          </tbody>
        </table>
      </div>
      <div className="form-actions">
        {result.dryRun && ready > 0 && (
          <button type="button" className="button" disabled={busy !== null} onClick={onBook}>
            {busy === 'book' ? t('bookingReady') : t('bookReady', { n: ready })}
          </button>
        )}
        <button type="button" className="button secondary" onClick={onReset}>
          {t('uploadAnother')}
        </button>
      </div>
    </>
  );
}
