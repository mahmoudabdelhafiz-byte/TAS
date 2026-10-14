import { writeFileSync } from 'node:fs';
import { request, type FullConfig } from '@playwright/test';

// Seeds the demo terminal once and books four visits: green (now), amber (payment warning, dual), grey (later).
interface Seed { terminalId: number; truckIds: number[]; driverIds: number[] }
interface Slot { slotId: number; isClosed: boolean; available: number }

function riyadhDate(offsetDays: number): string {
  return new Intl.DateTimeFormat('en-CA', { timeZone: 'Asia/Riyadh', year: 'numeric', month: '2-digit', day: '2-digit' }).format(
    new Date(Date.now() + offsetDays * 86_400_000),
  );
}

export default async function globalSetup(config: FullConfig) {
  const baseURL = config.projects[0]!.use.baseURL!;
  const api = await request.newContext({ baseURL });

  const seed = (await (await api.post('/api/dev/seed')).json()) as Seed;
  const login = await api.post('/api/auth/login', { data: { email: 'dispatcher@alnoor.example', password: 'Demo-Pass-2026' } });
  if (!login.ok()) throw new Error(`Login failed: ${login.status()} ${await login.text()}`);
  const headers = { Authorization: `Bearer ${(await login.json()).accessToken}` };

  for (const offset of [0, 1]) {
    const day = riyadhDate(offset);
    const slots = ((await (
      await api.get(`/api/terminals/${seed.terminalId}/availability`, { params: { date: day, category: 'IMP_PICKUP' }, headers })
    ).json()) as Slot[]).filter((s) => !s.isClosed && s.available > 0);
    if (slots.length < 4) continue;

    const visits = [
      { slot: slots[0]!, truck: seed.truckIds[0], driver: seed.driverIds[0], containers: ['MSCU1234566'] },
      { slot: slots[1]!, truck: seed.truckIds[1], driver: seed.driverIds[1], containers: ['CMAU5100028', 'TGHU3000017'] },
      { slot: slots[2]!, truck: null, driver: null, containers: ['MAEU4001110'] },
      { slot: slots[3]!, truck: seed.truckIds[0], driver: seed.driverIds[0], containers: ['HLXU8700018'] },
    ];
    for (const v of visits) {
      const response = await api.post(`/api/terminals/${seed.terminalId}/appointments`, {
        headers,
        data: { slotId: v.slot.slotId, truckId: v.truck, driverId: v.driver, items: v.containers.map((c) => ({ category: 'IMP_PICKUP', containerNo: c })) },
      });
      if (response.status() !== 201) throw new Error(`Booking failed: ${response.status()} ${await response.text()}`);
    }

    writeFileSync('e2e/.state.json', JSON.stringify({ bookedDay: day }));
    await api.dispose();
    return;
  }
  throw new Error('No day with four open slots to book the demo visits.');
}
