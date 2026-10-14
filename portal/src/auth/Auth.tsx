import { createContext, useCallback, useContext, useEffect, useMemo, useState, type ReactNode } from 'react';
import { useQueryClient } from '@tanstack/react-query';
import * as client from '../api/client';
import type { UserProfile, UserRole } from '../api/types';

type AuthState =
  | { status: 'loading' }
  | { status: 'signed-out'; reason?: 'session-ended' }
  | { status: 'signed-in'; user: UserProfile };

interface AuthValue {
  state: AuthState;
  user: UserProfile | null;
  signIn: (email: string, password: string) => Promise<void>;
  signOut: () => Promise<void>;
  /** After a change to the account (e.g. new password, which returns a new session). */
  setUser: (user: UserProfile) => void;
}

const AuthContext = createContext<AuthValue | null>(null);

export function AuthProvider({ children }: { children: ReactNode }) {
  const queries = useQueryClient();
  const [state, setState] = useState<AuthState>(() => (client.hasStoredSession() ? { status: 'loading' } : { status: 'signed-out' }));

  // Resume a stored session on load.
  useEffect(() => {
    if (state.status !== 'loading') return;
    let cancelled = false;
    client
      .refreshSession()
      .then((user) => {
        if (!cancelled) setState(user ? { status: 'signed-in', user } : { status: 'signed-out' });
      })
      .catch(() => {
        if (!cancelled) setState({ status: 'signed-out' });
      });
    return () => {
      cancelled = true;
    };
  }, [state.status]);

  useEffect(
    () =>
      client.onSessionEnded(() => {
        queries.clear();
        setState({ status: 'signed-out', reason: 'session-ended' });
      }),
    [queries],
  );

  const signIn = useCallback(async (email: string, password: string) => {
    const user = await client.signIn(email, password);
    setState({ status: 'signed-in', user });
  }, []);

  const signOut = useCallback(async () => {
    await client.signOut();
    queries.clear();
    setState({ status: 'signed-out' });
  }, [queries]);

  const setUser = useCallback((user: UserProfile) => setState({ status: 'signed-in', user }), []);

  const value = useMemo<AuthValue>(
    () => ({ state, user: state.status === 'signed-in' ? state.user : null, signIn, signOut, setUser }),
    [state, signIn, signOut, setUser],
  );
  return <AuthContext.Provider value={value}>{children}</AuthContext.Provider>;
}

export function useAuth(): AuthValue {
  const value = useContext(AuthContext);
  if (!value) throw new Error('useAuth must be used inside <AuthProvider>');
  return value;
}

/** The signed-in user; only for pages behind <RequireAuth>. */
export function useUser(): UserProfile {
  const { user } = useAuth();
  if (!user) throw new Error('useUser needs a signed-in user');
  return user;
}

export const isCompanyRole = (role: UserRole) => role === 'CompanyAdmin' || role === 'CompanyUser';
export const isTerminalStaff = (role: UserRole) => !isCompanyRole(role);
export const canManageUsers = (role: UserRole) => role === 'CompanyAdmin' || role === 'TerminalAdmin' || role === 'SysAdmin';

/** Roles the user may give to people they invite (mirrors AccessRules.CanCreateUser on the server). */
export function invitableRoles(role: UserRole): UserRole[] {
  switch (role) {
    case 'CompanyAdmin':
      return ['CompanyUser', 'CompanyAdmin'];
    case 'TerminalAdmin':
      return ['GateClerk', 'Planner', 'TerminalAdmin', 'CompanyUser', 'CompanyAdmin'];
    case 'SysAdmin':
      return ['GateClerk', 'Planner', 'TerminalAdmin', 'CompanyUser', 'CompanyAdmin'];
    default:
      return [];
  }
}
