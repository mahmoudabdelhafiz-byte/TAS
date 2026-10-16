// Guide texts, bundled into the portal by Vite.

import adminAr from './ar/admin.md?raw';
import bookingAr from './ar/booking.md?raw';
import controlAr from './ar/control.md?raw';
import driverAr from './ar/driver.md?raw';
import fleetAr from './ar/fleet.md?raw';
import gateAr from './ar/gate.md?raw';
import planningAr from './ar/planning.md?raw';
import startAr from './ar/start.md?raw';
import trackAr from './ar/track.md?raw';
import adminEn from './en/admin.md?raw';
import bookingEn from './en/booking.md?raw';
import controlEn from './en/control.md?raw';
import driverEn from './en/driver.md?raw';
import fleetEn from './en/fleet.md?raw';
import gateEn from './en/gate.md?raw';
import planningEn from './en/planning.md?raw';
import startEn from './en/start.md?raw';
import trackEn from './en/track.md?raw';
import apiTech from './tech/api.md?raw';
import assistantTech from './tech/assistant.md?raw';
import deviceAdaptersTech from './tech/device-adapters.md?raw';
import integrationTech from './tech/integration.md?raw';
import operationsTech from './tech/operations.md?raw';
import overviewTech from './tech/overview.md?raw';
import securityTech from './tech/security.md?raw';
import tosAdapterTech from './tech/tos-adapter.md?raw';
import type { Lang } from '../lib/time.ts';

export const guideText: Record<Lang, Record<string, string>> = {
  en: { start: startEn, booking: bookingEn, track: trackEn, fleet: fleetEn, driver: driverEn, gate: gateEn, control: controlEn, planning: planningEn, admin: adminEn },
  ar: { start: startAr, booking: bookingAr, track: trackAr, fleet: fleetAr, driver: driverAr, gate: gateAr, control: controlAr, planning: planningAr, admin: adminAr },
};

/** Pages for IT teams and integrators (English). */
export const techText: Record<string, string> = {
  overview: overviewTech, api: apiTech, integration: integrationTech, 'tos-adapter': tosAdapterTech, 'device-adapters': deviceAdaptersTech, operations: operationsTech, security: securityTech, assistant: assistantTech,
};

/** The guides in a language: English for languages without guides of their own. */
export const guidesIn = (lang: Lang): Record<string, string> => guideText[lang] ?? guideText.en;
