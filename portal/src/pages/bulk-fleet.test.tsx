import { render, screen, waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { App } from '../App';
import { resetClientState } from '../api/client';
import type { BulkResult } from '../api/types';
import { canBlockFleet, canEditFleet } from './Fleet';

function json(status: number, body: unknown) {
  return new Response(JSON.stringify(body), { status, headers: { 'Content-Type': 'application/json' } });
}

const terminal = { terminalId: 1, code: 'DEMO', name: 'Demo Terminal', terminalType: 'Multipurpose', timeZone: 'Asia/Riyadh', slotMinutes: 60, bookingHorizonDays: 14 };
const person = (role: string) => ({ userId: 7, email: 'k@alnoor.example', fullName: 'Khalid', role, companyId: 1, companyName: 'Al Noor Transport', terminalId: null, preferredLanguage: 'en' });

const preview: BulkResult = {
  dryRun: true,
  visits: 2,
  succeeded: 1,
  failed: 1,
  fileErrors: [],
  results: [
    { visit: 'V1', rows: [2, 3], slot: '2026-10-20 10:00', status: 'WouldBook', appointmentNo: null, errors: [], checks: [] },
    {
      visit: 'row 4',
      rows: [4],
      slot: '2026-10-20 11:00',
      status: 'Failed',
      appointmentNo: null,
      errors: ['Customs hold on MSCU7654329'],
      checks: [{ item: 0, type: 'CustomsRelease', result: 'Fail', message: 'Customs hold on MSCU7654329', source: 'N4' }],
    },
  ],
};

let fetchMock: ReturnType<typeof vi.fn>;
beforeEach(() => {
  resetClientState();
  fetchMock = vi.fn();
  vi.stubGlobal('fetch', fetchMock);
  window.history.replaceState(null, '', '/');
});
afterEach(() => vi.unstubAllGlobals());

async function signInAs(role: string, path: string) {
  render(<App language="en" />);
  await userEvent.type(await screen.findByLabelText('Email'), 'k@alnoor.example');
  await userEvent.type(screen.getByLabelText('Password'), 'Demo-Pass-2026');
  await userEvent.click(screen.getByRole('button', { name: 'Sign in' }));
  await screen.findByRole('heading', { name: 'Appointments' });
  window.history.pushState(null, '', path);
  window.dispatchEvent(new PopStateEvent('popstate'));
  void role;
}

function common(role: string) {
  return async (url: string, init: RequestInit = {}) => {
    if (url === '/api/auth/login') return json(200, { accessToken: 'a', refreshToken: 'r', accessTokenExpiresAt: '', refreshTokenExpiresAt: '', user: person(role) });
    if (url === '/api/terminals') return json(200, [terminal]);
    if (url.startsWith('/api/terminals/1/appointments?')) return json(200, []);
    void init;
    return null;
  };
}

describe('bulk upload', () => {
  it('previews the sheet, then books the ready visits with the same file', async () => {
    const bodies: { url: string; body: unknown; contentType: string | null }[] = [];
    const base = common('CompanyAdmin');
    fetchMock.mockImplementation(async (url: string, init: RequestInit = {}) => {
      const answer = await base(url, init);
      if (answer) return answer;
      if (url.startsWith('/api/terminals/1/appointments/bulk?')) {
        bodies.push({ url, body: init.body, contentType: new Headers(init.headers).get('Content-Type') });
        return url.includes('dryRun=false')
          ? json(200, { ...preview, dryRun: false, results: [{ ...preview.results[0]!, status: 'Booked', appointmentNo: 'DEMO-7' }, preview.results[1]] })
          : json(200, preview);
      }
      return json(404, {});
    });
    await signInAs('CompanyAdmin', '/book/upload');

    expect(await screen.findByRole('heading', { name: 'Upload a booking sheet' })).toBeInTheDocument();
    const file = new File(['date,time,category,container_no\n'], 'visits.csv', { type: 'text/csv' });
    await userEvent.upload(screen.getByLabelText(/Sheet/), file);
    await userEvent.click(screen.getByRole('button', { name: 'Check the sheet' }));

    expect(await screen.findByText('1 of 2 visits can be booked.')).toBeInTheDocument();
    const failed = screen.getAllByRole('row')[2]!;
    expect(failed).toHaveAttribute('data-result', 'Failed');
    expect(within(failed).getByText('Customs release')).toBeInTheDocument();

    await userEvent.click(screen.getByRole('button', { name: 'Book the 1 visits that are ready' }));
    expect(await screen.findByRole('heading', { name: '1 of 2 visits booked.' })).toBeInTheDocument();
    expect(screen.getByRole('link', { name: 'DEMO-7' })).toHaveAttribute('href', '/appointments/DEMO-7');

    expect(bodies.map((b) => new URL(b.url, 'http://x').searchParams.get('dryRun'))).toEqual(['true', 'false']);
    expect(bodies.every((b) => b.body instanceof FormData && (b.body.get('file') as File).name === 'visits.csv')).toBe(true);
    expect(bodies.every((b) => b.contentType === null)).toBe(true); // the browser sets the multipart boundary
  });

  it('lists problems with the sheet itself by row', async () => {
    const base = common('CompanyUser');
    fetchMock.mockImplementation(async (url: string, init: RequestInit = {}) =>
      (await base(url, init)) ??
      (url.startsWith('/api/terminals/1/appointments/bulk?')
        ? json(200, { dryRun: true, visits: 0, succeeded: 0, failed: 0, fileErrors: [{ row: 0, message: 'Missing column: date' }, { row: 7, message: "'25:00' is not a time" }], results: [] })
        : json(404, {})),
    );
    await signInAs('CompanyUser', '/book/upload');
    await userEvent.upload(await screen.findByLabelText(/Sheet/), new File(['x'], 'bad.xlsx'));
    await userEvent.click(screen.getByRole('button', { name: 'Check the sheet' }));

    const alert = await screen.findByRole('alert');
    expect(alert).toHaveTextContent('The sheet can’t be read as it is:');
    expect(alert).toHaveTextContent('Missing column: date');
    expect(alert).toHaveTextContent("Row 7: '25:00' is not a time");
  });
});

describe('fleet', () => {
  it('lets a company admin add a truck; dispatchers only look', async () => {
    let trucks = [{ truckId: 1, companyId: 1, plateNumber: '1234 ABJ', plateNormalized: '1234ABJ', isBlocked: false }];
    const base = common('CompanyAdmin');
    fetchMock.mockImplementation(async (url: string, init: RequestInit = {}) => {
      const answer = await base(url, init);
      if (answer) return answer;
      if (url === '/api/companies/1/trucks' && init.method === 'POST') {
        const added = { truckId: 2, companyId: 1, plateNumber: JSON.parse(String(init.body)).plateNumber, plateNormalized: '4321ABJ', isBlocked: false };
        trucks = [...trucks, added];
        return json(201, { data: added, regeneratedSlots: null });
      }
      if (url === '/api/companies/1/trucks') return json(200, trucks);
      if (url === '/api/companies/1/drivers') return json(200, []);
      return json(404, {});
    });
    await signInAs('CompanyAdmin', '/fleet');

    expect(await screen.findByText('1234 ABJ')).toBeInTheDocument();
    await userEvent.click(screen.getByRole('button', { name: 'Add a truck' }));
    await userEvent.type(screen.getByLabelText('Plate number'), 'أ ب ح 4321');
    await userEvent.click(screen.getByRole('button', { name: 'Save' }));
    expect(await screen.findByText('Truck أ ب ح 4321 added.')).toBeInTheDocument();
    await waitFor(() => expect(screen.getByText('أ ب ح 4321', { selector: '.code' })).toBeInTheDocument());
    expect(screen.queryByRole('button', { name: 'Block' })).not.toBeInTheDocument(); // terminal decision
    expect(screen.getByText('No drivers yet.')).toBeInTheDocument();
  });

  it('follows the API’s rules for who may change and block', () => {
    expect(canEditFleet('CompanyAdmin')).toBe(true);
    expect(canEditFleet('CompanyUser')).toBe(false);
    expect(canEditFleet('Planner')).toBe(false);
    expect(canBlockFleet('TerminalAdmin')).toBe(true);
    expect(canBlockFleet('CompanyAdmin')).toBe(false);
  });
});
