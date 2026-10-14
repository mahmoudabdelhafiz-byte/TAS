import { fireEvent, render, screen, waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { App } from '../App';
import { resetClientState } from '../api/client';
import type { Category, SlotAvailability } from '../api/types';
import { SlotBoard } from '../booking/SlotBoard';
import { I18nProvider } from '../i18n/I18n';
import { todayAt } from '../terminals/Terminals';
import { canChange } from './AppointmentDetail';
import { moveToItem, type MoveDraft } from './Book';

function json(status: number, body: unknown) {
  return new Response(JSON.stringify(body), { status, headers: { 'Content-Type': 'application/json' } });
}

const dispatcher = { userId: 7, email: 'k@alnoor.example', fullName: 'Khalid', role: 'CompanyAdmin', companyId: 1, companyName: 'Al Noor Transport', terminalId: null, preferredLanguage: 'en' };
const terminal = { terminalId: 1, code: 'DEMO', name: 'Demo Terminal', terminalType: 'Multipurpose', timeZone: 'Asia/Riyadh', slotMinutes: 60, bookingHorizonDays: 14 };
const categories: Category[] = [
  { categoryId: 1, code: 'IMP_PICKUP', nameEn: 'Import pickup', nameAr: 'استلام حاوية وارد', mode: 'Container', unitType: 'Move', sortOrder: 1, isActive: true },
  { categoryId: 5, code: 'BB_DELIVERY', nameEn: 'Breakbulk delivery', nameAr: 'استلام بضائع عامة', mode: 'Cargo', unitType: 'Trip', sortOrder: 5, isActive: true },
];
const today = todayAt('Asia/Riyadh');
const slot = (id: number, hour: number, available: number, isClosed = false): SlotAvailability => ({
  slotId: id,
  startAt: `${today}T${String(hour - 3).padStart(2, '0')}:00:00Z`,
  endAt: `${today}T${String(hour - 2).padStart(2, '0')}:00:00Z`,
  isClosed,
  available,
});
const slots = [slot(10, 10, 5), slot(11, 11, 0), slot(12, 12, 0, true), slot(13, 13, 2)];

let fetchMock: ReturnType<typeof vi.fn>;
beforeEach(() => {
  resetClientState();
  fetchMock = vi.fn();
  vi.stubGlobal('fetch', fetchMock);
  window.history.replaceState(null, '', '/');
});
afterEach(() => vi.unstubAllGlobals());

describe('moves', () => {
  const move = (change: Partial<MoveDraft>): MoveDraft => ({ key: 1, category: 'IMP_PICKUP', containerNo: '', bookingRef: '', cargoRef: '', ...change });

  it('turns a draft into what the API expects, or says what is missing', () => {
    expect(moveToItem(move({ containerNo: 'mscu 1234566' }), categories[0])).toEqual({ item: { category: 'IMP_PICKUP', containerNo: 'MSCU1234566', bookingRef: undefined } });
    expect(moveToItem(move({ bookingRef: ' BKG1001 ' }), categories[0])).toEqual({ item: { category: 'IMP_PICKUP', containerNo: undefined, bookingRef: 'BKG1001' } });
    expect(moveToItem(move({}), categories[0]).problem).toBe('missing');
    expect(moveToItem(move({ containerNo: 'MSCU1234567' }), categories[0]).problem).toBe('checkDigit');
    expect(moveToItem(move({ category: 'BB_DELIVERY', cargoRef: 'BL-STEEL-001' }), categories[1])).toEqual({ item: { category: 'BB_DELIVERY', cargoRef: 'BL-STEEL-001' } });
    expect(moveToItem(move({ category: 'BB_DELIVERY' }), categories[1]).problem).toBe('missing');
  });

  it('allows changes only before the gate', () => {
    expect(canChange('Confirmed')).toBe(true);
    expect(canChange('Arrived')).toBe(true);
    expect(canChange('GateIn')).toBe(false);
    expect(canChange('Cancelled')).toBe(false);
  });
});

describe('slot board', () => {
  it('offers only open slots and moves with the arrow keys', async () => {
    const onSelect = vi.fn();
    render(
      <I18nProvider initial="en">
        <SlotBoard slots={slots} selected={null} onSelect={onSelect} timeZone="Asia/Riyadh" label="When" />
      </I18nProvider>,
    );
    const radios = screen.getAllByRole('radio');
    expect(radios.map((r) => r.textContent)).toEqual(['10:005 left', '11:00Full', '12:00Closed', '13:002 left']);
    expect(radios[1]).toHaveAttribute('aria-disabled', 'true');

    await userEvent.click(radios[1]!);
    expect(onSelect).not.toHaveBeenCalled();
    await userEvent.click(radios[0]!);
    expect(onSelect).toHaveBeenLastCalledWith(10);
    fireEvent.keyDown(radios[0]!, { key: 'ArrowRight' });
    expect(onSelect).toHaveBeenLastCalledWith(13); // skips full and closed
  });
});

describe('booking a visit', () => {
  function mockApi(onBook: (body: unknown) => Response) {
    fetchMock.mockImplementation(async (url: string, init: RequestInit) => {
      if (url === '/api/auth/login') return json(200, { accessToken: 'a', refreshToken: 'r', accessTokenExpiresAt: '', refreshTokenExpiresAt: '', user: dispatcher });
      if (url === '/api/terminals') return json(200, [terminal]);
      if (url === '/api/terminals/1/categories') return json(200, categories);
      if (url.startsWith('/api/terminals/1/availability')) return json(200, slots);
      if (url === '/api/companies/1/trucks') return json(200, [{ truckId: 3, companyId: 1, plateNumber: '1234 ABJ', isBlocked: false }]);
      if (url === '/api/companies/1/drivers') return json(200, [{ driverId: 4, companyId: 1, fullName: 'Abdullah', idNumber: '1', mobile: '+966500000001', isBlocked: false }]);
      if (url === '/api/terminals/1/appointments' && init.method === 'POST') return onBook(JSON.parse(String(init.body)));
      if (url.startsWith('/api/terminals/1/appointments/')) return json(404, { detail: 'not found' });
      if (url.startsWith('/api/terminals/1/appointments')) return json(200, []);
      return json(404, {});
    });
  }

  async function openBooking() {
    render(<App language="en" />);
    await userEvent.type(await screen.findByLabelText('Email'), 'k@alnoor.example');
    await userEvent.type(screen.getByLabelText('Password'), 'Demo-Pass-2026');
    await userEvent.click(screen.getByRole('button', { name: 'Sign in' }));
    await userEvent.click((await screen.findAllByRole('link', { name: 'Book a visit' }))[0]!);
    expect(await screen.findByRole('heading', { name: 'Book a truck visit' })).toBeInTheDocument();
  }

  it('checks the container number while typing and sends a complete booking', async () => {
    let sent: unknown;
    mockApi((body) => {
      sent = body;
      return json(201, { appointmentNo: 'DEMO-1', items: [] });
    });
    await openBooking();

    const container = screen.getByLabelText('Container number');
    await userEvent.type(container, 'MSCU1234567');
    expect(await screen.findByText('The last digit doesn’t match the rest. Check the number for a typo.')).toBeInTheDocument();
    await userEvent.clear(container);
    await userEvent.type(container, 'mscu 123456-6');
    expect(screen.queryByText(/doesn’t match/)).not.toBeInTheDocument();

    await userEvent.click(await screen.findByRole('radio', { name: /13:00/ }));
    await userEvent.selectOptions(screen.getByLabelText('Truck'), '3');
    await userEvent.selectOptions(screen.getByLabelText('Driver'), '4');
    expect(screen.getByText(/1 move on .* at 13:00/)).toBeInTheDocument();
    await userEvent.click(screen.getByRole('button', { name: 'Book the visit' }));

    await waitFor(() => expect(sent).toBeDefined());
    expect(sent).toEqual({ slotId: 13, truckId: 3, driverId: 4, items: [{ category: 'IMP_PICKUP', containerNo: 'MSCU1234566' }] });
  });

  it('shows what the terminal refused, in the user’s language', async () => {
    mockApi(() =>
      json(422, {
        title: 'Not cleared for booking',
        detail: 'Customs hold on MSCU7654329',
        checks: [
          { item: 0, type: 'Availability', result: 'Pass', message: null, source: 'N4' },
          { item: 0, type: 'CustomsRelease', result: 'Fail', message: 'Customs hold on MSCU7654329', source: 'N4' },
        ],
      }),
    );
    await openBooking();
    await userEvent.type(screen.getByLabelText('Container number'), 'MSCU7654329');
    await userEvent.click(await screen.findByRole('radio', { name: /10:00/ }));
    await userEvent.click(screen.getByRole('button', { name: 'Book the visit' }));

    const alert = await screen.findByRole('alert');
    expect(alert).toHaveTextContent('The terminal can’t accept this visit yet:');
    const failed = within(alert).getByText('Customs release').closest('li')!;
    expect(failed).toHaveAttribute('data-result', 'Fail');
    expect(failed).toHaveTextContent('Customs hold on MSCU7654329');
    expect(within(alert).queryByText('Container available')).not.toBeInTheDocument(); // passes aren't repeated
  });

  it('asks for what is missing before sending', async () => {
    mockApi(() => json(500, {}));
    await openBooking();
    await userEvent.click(screen.getByRole('button', { name: 'Book the visit' }));
    expect(await screen.findByText('Enter a container number, or a booking reference for an empty pickup.')).toBeInTheDocument();
    expect(screen.getAllByText('Choose a time.').length).toBeGreaterThan(0);
    expect(fetchMock.mock.calls.some(([url, init]) => url === '/api/terminals/1/appointments' && init?.method === 'POST')).toBe(false);
  });
});
