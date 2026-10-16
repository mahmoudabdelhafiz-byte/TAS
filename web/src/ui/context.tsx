import { createContext, useContext } from 'react';
import type { ApiClient } from '../lib/api.ts';
import type { Brand } from '../lib/brand.ts';
import type { TextKey } from '../lib/i18n.ts';
import type { Route, Screen } from '../lib/routes.ts';
import type { Lang } from '../lib/time.ts';
import type { Terminal, UserProfile } from '../lib/types.ts';

export interface AppState {
  api: ApiClient;
  user: UserProfile;
  lang: Lang;
  t: (key: TextKey, values?: Record<string, string | number>) => string;
  /** Text for an API enum value: tx('status', 'EnRoute'). */
  tx: (group: string, value: string) => string;
  terminal: Terminal;
  route: Route;
  go: (screen: Screen, params?: Record<string, string | undefined>) => void;
  /** Today's date at the terminal, "YYYY-MM-DD". */
  today: string;
  /** The brand of the terminal: its operator's, else the installation's. */
  brand: Brand;
}

export const AppContext = createContext<AppState | null>(null);

export function useApp(): AppState {
  const state = useContext(AppContext);
  if (!state) throw new Error('useApp outside AppContext');
  return state;
}
