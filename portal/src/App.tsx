import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { BrowserRouter, Navigate, Route, Routes } from 'react-router';
import { ApiError } from './api/client';
import { AuthProvider, useAuth } from './auth/Auth';
import { I18nProvider, type Language } from './i18n/I18n';
import { RequireAuth } from './layout/AppShell';
import { AccountPage } from './pages/Account';
import { AppointmentsPage } from './pages/Appointments';
import { ForgotPasswordPage } from './pages/ForgotPassword';
import { SetPasswordPage } from './pages/SetPassword';
import { SignInPage } from './pages/SignIn';
import { UsersPage } from './pages/Users';
import type { ReactNode } from 'react';

export function createQueryClient() {
  return new QueryClient({
    defaultOptions: {
      queries: {
        // Don't hammer the API with retries for answers that won't change (403, 404, 400…).
        retry: (count, error) => count < 2 && !(error instanceof ApiError && error.status < 500),
        refetchOnWindowFocus: true,
      },
    },
  });
}

/** Signed-out pages send a signed-in person to the app instead. */
function SignedOutOnly({ children }: { children: ReactNode }) {
  const { state } = useAuth();
  if (state.status === 'signed-in') return <Navigate to="/" replace />;
  return <>{children}</>;
}

export function AppRoutes() {
  return (
    <Routes>
      <Route path="/sign-in" element={<SignedOutOnly><SignInPage /></SignedOutOnly>} />
      <Route path="/forgot-password" element={<ForgotPasswordPage />} />
      {/* Links from emails work whether or not someone is signed in on this browser. */}
      <Route path="/reset-password" element={<SetPasswordPage />} />
      <Route path="/accept-invite" element={<SetPasswordPage />} />
      <Route element={<RequireAuth />}>
        <Route index element={<Navigate to="/appointments" replace />} />
        <Route path="/appointments" element={<AppointmentsPage />} />
        <Route path="/users" element={<UsersPage />} />
        <Route path="/account" element={<AccountPage />} />
        <Route path="*" element={<Navigate to="/appointments" replace />} />
      </Route>
    </Routes>
  );
}

export function App({ queryClient = createQueryClient(), language }: { queryClient?: QueryClient; language?: Language }) {
  return (
    <I18nProvider initial={language}>
      <QueryClientProvider client={queryClient}>
        <AuthProvider>
          <BrowserRouter>
            <AppRoutes />
          </BrowserRouter>
        </AuthProvider>
      </QueryClientProvider>
    </I18nProvider>
  );
}
