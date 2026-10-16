// The brand people see: the installation's, or the operator's for its terminals and at its own web address.
// GET /api/branding (public, by web address) and GET /api/terminals/{id}/branding. The main colour replaces the
// Yardsyncs teal everywhere it is used (buttons, links, the selected tab, the first move type).

export interface Brand {
  name: string;
  /** "#rrggbb"; the server only accepts colours that carry white text (contrast at least 4.5:1). */
  color: string;
  logoUrl: string | null;
  operatorId: number | null;
}

export const defaultBrand: Brand = { name: 'Yardsyncs', color: '#0e7c86', logoUrl: null, operatorId: null };

function rgb(hex: string): [number, number, number] {
  const n = Number.parseInt(hex.slice(1), 16);
  return [(n >> 16) & 255, (n >> 8) & 255, n & 255];
}

const toHex = (c: number[]) => `#${c.map(v => Math.round(Math.min(255, Math.max(0, v))).toString(16).padStart(2, '0')).join('')}`;

/** The colour, a darker one for hover and pressed states, and a pale tint for selected rows and backgrounds. */
export function shades(color: string): { main: string; dark: string; tint: string } {
  const main = /^#[0-9a-f]{6}$/i.test(color) ? color.toLowerCase() : defaultBrand.color;
  const c = rgb(main);
  return { main, dark: toHex(c.map(v => v * 0.78)), tint: toHex(c.map(v => v + (255 - v) * 0.89)) };
}

/** WCAG contrast of the colour against white (the same check as the server's). */
export function contrastWithWhite(color: string): number {
  const channel = (v: number) => {
    const c = v / 255;
    return c <= 0.03928 ? c / 12.92 : ((c + 0.055) / 1.055) ** 2.4;
  };
  const [r, g, b] = rgb(color);
  return 1.05 / (0.2126 * channel(r) + 0.7152 * channel(g) + 0.0722 * channel(b) + 0.05);
}

/** Puts the brand's colours on the page and its name in the browser tab. */
export function applyBrand(brand: Brand, root: { style: { setProperty(name: string, value: string): void } } = document.documentElement): void {
  const { main, dark, tint } = shades(brand.color);
  root.style.setProperty('--teal', main);
  root.style.setProperty('--teal-dark', dark);
  root.style.setProperty('--teal-tint', tint);
  root.style.setProperty('--tone-1', main);
  if (typeof document !== 'undefined') document.title = brand.name;
}
