import { createContext, useContext, useMemo, useState, type ReactNode } from 'react';
import { useQuery } from '@tanstack/react-query';
import { api } from '../api/client';
import type { Terminal } from '../api/types';

const STORAGE_KEY = 'tas.terminal';

interface TerminalValue {
  terminals: Terminal[];
  terminal: Terminal | null;
  select: (terminalId: number) => void;
  isLoading: boolean;
  error: unknown;
}

const TerminalContext = createContext<TerminalValue | null>(null);

function storedTerminalId(): number | null {
  try {
    const value = Number(localStorage.getItem(STORAGE_KEY));
    return Number.isInteger(value) && value > 0 ? value : null;
  } catch {
    return null;
  }
}

/** The terminals the user can work at, and the one they're looking at now (remembered per browser). */
export function TerminalProvider({ children }: { children: ReactNode }) {
  const query = useQuery({ queryKey: ['terminals'], queryFn: () => api<Terminal[]>('/api/terminals'), staleTime: 5 * 60_000 });
  const [selectedId, setSelectedId] = useState<number | null>(storedTerminalId);

  const value = useMemo<TerminalValue>(() => {
    const terminals = query.data ?? [];
    const terminal = terminals.find((t) => t.terminalId === selectedId) ?? terminals[0] ?? null;
    return {
      terminals,
      terminal,
      isLoading: query.isLoading,
      error: query.error,
      select: (terminalId) => {
        setSelectedId(terminalId);
        try {
          localStorage.setItem(STORAGE_KEY, String(terminalId));
        } catch {
          // ignore
        }
      },
    };
  }, [query.data, query.isLoading, query.error, selectedId]);

  return <TerminalContext.Provider value={value}>{children}</TerminalContext.Provider>;
}

export function useTerminals(): TerminalValue {
  const value = useContext(TerminalContext);
  if (!value) throw new Error('useTerminals must be used inside <TerminalProvider>');
  return value;
}

/** The selected terminal; only for pages rendered once one exists (the shell guarantees it). */
export function useTerminal(): Terminal {
  const { terminal } = useTerminals();
  if (!terminal) throw new Error('No terminal selected');
  return terminal;
}

/** Today's date (yyyy-mm-dd) in the terminal's time zone. */
export function todayAt(timeZone: string, now: Date = new Date()): string {
  return new Intl.DateTimeFormat('en-CA', { timeZone, year: 'numeric', month: '2-digit', day: '2-digit' }).format(now);
}

/** yyyy-mm-dd ± days, calendar arithmetic without time zones. */
export function addDays(date: string, days: number): string {
  const [y, m, d] = date.split('-').map(Number) as [number, number, number];
  const next = new Date(Date.UTC(y, m - 1, d + days));
  return next.toISOString().slice(0, 10);
}
