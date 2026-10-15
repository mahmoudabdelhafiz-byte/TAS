// What the gate clerk typed: an appointment number (DEMO-261020-7KQ4M) or a plate (1234 ABJ).

export function readLookup(text: string): { appointmentNo?: string; plateRead?: string } {
  const clean = text.trim().toUpperCase();
  if (!clean) return {};
  return /^[A-Z0-9]+-\d{6}-[A-Z0-9]+$/.test(clean) ? { appointmentNo: clean } : { plateRead: clean };
}
