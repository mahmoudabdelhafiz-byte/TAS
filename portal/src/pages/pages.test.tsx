import { render, screen, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { App } from '../App';
import { resetClientState } from '../api/client';
import type { Appointment, UserView } from '../api/types';
import { invitableRoles } from '../auth/Auth';
import { navFor } from '../layout/nav';
import { addDays, todayAt } from '../terminals/Terminals';
import { countByLight } from './Appointments';
import { readTokenFromHash } from './SetPassword';
import { userState } from './Users';

function json(status: number, body: unknown) {
  return new Response(JSON.stringify(body), { status, headers: { 'Content-Type': 'application/json' } });
}

const dispatcher = { userId: 7, email: 'k@alnoor.example', fullName: 'Khalid', role: 'CompanyAdmin', companyId: 1, companyName: 'Al Noor Transport', terminalId: null, preferredLanguage: 'ar' };
const terminal = { terminalId: 1, code: 'DEMO', name: 'Demo Terminal', terminalType: 'Multipurpose', timeZone: 'Asia/Riyadh', slotMinutes: 60, bookingHorizonDays: 14 };

function appointment(no: string, light: Appointment['trafficLight'], hour: number): Appointment {
  const start = new Date(Date.UTC(2026, 9, 20, hour - 3)).toISOString();
  const end = new Date(Date.UTC(2026, 9, 20, hour - 2)).toISOString();
  return {
    appointmentNo: no, status: 'Confirmed', slotId: hour, slotStart: start, slotEnd: end, slotLocal: '', companyId: 1, companyName: 'Al Noor Transport',
    truckId: 1, truckPlate: '1234 ABJ', driverId: 1, driverName: 'Abdullah', isDual: false, trafficLight: light, trafficReason: light === 'Red' ? 'Customs hold on MSCU7654329' : '',
    items: [{ category: 'IMP_PICKUP', containerNo: light === 'Red' ? 'MSCU7654329' : 'MSCU1234566', isoType: null, bookingRef: null, edoRef: null, cargoRef: null, plannedTonnage: null, validationStatus: 'Pass' }],
    createdAt: start,
  };
}

let fetchMock: ReturnType<typeof vi.fn>;
beforeEach(() => {
  resetClientState();
  fetchMock = vi.fn();
  vi.stubGlobal('fetch', fetchMock);
  window.history.replaceState(null, '', '/');
});
afterEach(() => vi.unstubAllGlobals());

describe('sign in', () => {
  it('opens in Arabic, right to left, and explains a wrong password', async () => {
    fetchMock.mockResolvedValue(json(401, { detail: 'Wrong email or password.' }));
    render(<App />);

    expect(await screen.findByRole('heading', { name: 'تسجيل الدخول' })).toBeInTheDocument();
    expect(document.documentElement.dir).toBe('rtl');
    expect(document.documentElement.lang).toBe('ar');

    await userEvent.type(screen.getByLabelText('البريد الإلكتروني'), 'k@alnoor.example');
    await userEvent.type(screen.getByLabelText('كلمة المرور'), 'wrong-one-1');
    await userEvent.click(screen.getByRole('button', { name: 'دخول' }));
    expect(await screen.findByRole('alert')).toHaveTextContent('البريد الإلكتروني أو كلمة المرور غير صحيحة.');
  });

  it('switches to English, left to right', async () => {
    render(<App />);
    await userEvent.click(await screen.findByRole('button', { name: 'English' }));
    expect(screen.getByRole('heading', { name: 'Sign in' })).toBeInTheDocument();
    expect(document.documentElement.dir).toBe('ltr');
    expect(localStorage.getItem('tas.lang')).toBe('en');
  });
});

describe('appointments', () => {
  it('lists the day with a light per row and counts by light', async () => {
    fetchMock.mockImplementation(async (url: string) => {
      if (url === '/api/auth/login') return json(200, { accessToken: 'a', refreshToken: 'r', accessTokenExpiresAt: '', refreshTokenExpiresAt: '', user: dispatcher });
      if (url === '/api/terminals') return json(200, [terminal]);
      if (url.startsWith('/api/terminals/1/appointments')) return json(200, [appointment('DEMO-2', 'Green', 11), appointment('DEMO-1', 'Red', 10)]);
      return json(404, {});
    });
    render(<App language="en" />);

    await userEvent.type(await screen.findByLabelText('Email'), 'k@alnoor.example');
    await userEvent.type(screen.getByLabelText('Password'), 'Demo-Pass-2026');
    await userEvent.click(screen.getByRole('button', { name: 'Sign in' }));

    expect(await screen.findByRole('heading', { name: 'Appointments' })).toBeInTheDocument();
    const rows = await screen.findAllByRole('row');
    expect(rows).toHaveLength(3); // header + 2, earliest first
    expect(rows[1]).toHaveAttribute('data-light', 'Red');
    expect(rows[1]).toHaveTextContent('10:00–11:00');
    expect(rows[1]).toHaveTextContent('Customs hold on MSCU7654329');
    expect(screen.queryByRole('columnheader', { name: 'Company' })).not.toBeInTheDocument(); // own company only
    expect(screen.getByRole('list', { name: 'Appointments by status' })).toHaveTextContent('1Don’t set off');

    // A company admin sees Users; the appointments request asked for the terminal's today.
    expect(screen.getByRole('link', { name: 'Users' })).toBeInTheDocument();
    await waitFor(() => expect(fetchMock.mock.calls.some(([url]) => String(url).includes(`date=${todayAt('Asia/Riyadh')}`))).toBe(true));
  });
});

describe('rules', () => {
  it('reads only a well-formed token from the link fragment', () => {
    expect(readTokenFromHash('#token=abcdefghijklmnopqrstuvwxyz_-0123456789')).toBe('abcdefghijklmnopqrstuvwxyz_-0123456789');
    expect(readTokenFromHash('#token=short')).toBeNull();
    expect(readTokenFromHash('#token=<script>alert(1)</script>xxxxxxxxxxxx')).toBeNull();
    expect(readTokenFromHash('')).toBeNull();
  });

  it('shows each role only its sections and invitable roles', () => {
    expect(navFor('CompanyUser').map((i) => i.path)).toEqual(['/appointments', '/book', '/account']);
    expect(navFor('Planner').map((i) => i.path)).toEqual(['/appointments', '/book', '/account']);
    expect(navFor('GateClerk').map((i) => i.path)).toEqual(['/appointments', '/account']);
    expect(navFor('TerminalAdmin').map((i) => i.path)).toEqual(['/appointments', '/book', '/users', '/account']);
    expect(invitableRoles('CompanyAdmin')).toEqual(['CompanyUser', 'CompanyAdmin']);
    expect(invitableRoles('GateClerk')).toEqual([]);
  });

  it('names a user’s state', () => {
    const base: UserView = { userId: 1, email: 'x@y.example', fullName: 'X', role: 'CompanyUser', companyId: 1, terminalId: null, mobile: null, preferredLanguage: 'ar', isActive: true, lastLoginAt: null, lockedUntil: null, passwordSet: true };
    const now = new Date('2026-10-20T10:00:00Z');
    expect(userState(base, now).key).toBe('stateActive');
    expect(userState({ ...base, passwordSet: false }, now).key).toBe('stateInvited');
    expect(userState({ ...base, isActive: false, passwordSet: false }, now).key).toBe('stateDeactivated');
    expect(userState({ ...base, lockedUntil: '2026-10-20T10:10:00Z' }, now).key).toBe('stateLocked');
    expect(userState({ ...base, lockedUntil: '2026-10-20T09:00:00Z' }, now).key).toBe('stateActive');
  });

  it('counts lights and steps days across month ends', () => {
    expect(countByLight([appointment('A', 'Red', 9), appointment('B', 'Red', 9), appointment('C', 'Grey', 9)])).toEqual({ Red: 2, Amber: 0, Green: 0, Grey: 1 });
    expect(addDays('2026-10-31', 1)).toBe('2026-11-01');
    expect(addDays('2026-03-01', -1)).toBe('2026-02-28');
    expect(todayAt('Asia/Riyadh', new Date('2026-10-19T22:30:00Z'))).toBe('2026-10-20');
  });
});
