import { readFileSync } from 'node:fs';
import { expect, test, type Page } from '@playwright/test';

// Demo data from POST /api/dev/seed (Development only). Screenshots go to e2e/screenshots for review.
const PASSWORD = 'Demo-Pass-2026';
const DISPATCHER = 'dispatcher@alnoor.example';
const ADMIN = 'admin@demo-terminal.example';

// Written by global-setup.ts: the day the demo visits were booked on. Read lazily (files load before setup runs).
const bookedDay = () => (JSON.parse(readFileSync('e2e/.state.json', 'utf8')) as { bookedDay: string }).bookedDay;

async function signIn(page: Page, email: string) {
  await page.goto('/sign-in');
  await page.getByLabel(/البريد الإلكتروني|Email/).fill(email);
  await page.getByLabel(/^(كلمة المرور|Password)$/).fill(PASSWORD);
  await page.getByRole('button', { name: /^(دخول|Sign in)$/ }).click();
}

const shot = (page: Page, name: string) =>
  page.screenshot({ path: `e2e/screenshots/${test.info().project.name}-${name}.png`, fullPage: true });

test('a dispatcher signs in (Arabic first) and sees the day with traffic lights', async ({ page }) => {
  await page.goto('/sign-in');
  await expect(page.locator('html')).toHaveAttribute('dir', 'rtl');
  await expect(page.getByRole('heading', { name: 'تسجيل الدخول' })).toBeVisible();
  await shot(page, '01-sign-in-ar');

  await signIn(page, DISPATCHER);
  await expect(page.getByRole('heading', { name: 'المواعيد' })).toBeVisible();
  await page.goto(`/appointments?day=${bookedDay()}`);
  const rows = page.locator('tbody tr');
  await expect(rows).toHaveCount(4);
  await expect(page.locator('tbody tr[data-light="Amber"]')).toHaveCount(1);
  await expect(page.getByText('CMAU5100028', { exact: true })).toBeVisible();
  await shot(page, '02-appointments-ar');

  await page.getByRole('button', { name: 'English' }).click();
  await expect(page.locator('html')).toHaveAttribute('dir', 'ltr');
  await expect(page.getByRole('heading', { name: 'Appointments' })).toBeVisible();
  await shot(page, '03-appointments-en');

  // Survives a reload: the session is resumed with the stored refresh token.
  await page.reload();
  await expect(page.getByRole('heading', { name: 'Appointments' })).toBeVisible();
});

test('a terminal admin invites a user, who then shows as pending', async ({ page }) => {
  await page.goto('/sign-in');
  await page.getByRole('button', { name: 'English' }).click();
  await signIn(page, ADMIN);
  await page.getByRole('link', { name: 'Users' }).click();
  await expect(page.getByRole('heading', { name: 'Users' })).toBeVisible();

  const email = `clerk-${test.info().project.name}-${Date.now()}@demo-terminal.example`;
  await page.getByRole('button', { name: 'Invite a user' }).click();
  await page.getByLabel('Full name', { exact: true }).fill('Gate Clerk Three');
  await page.getByLabel('Email', { exact: true }).fill(email);
  await page.getByLabel('Role', { exact: true }).selectOption('GateClerk');
  await shot(page, '04-invite-form-en');
  await page.getByRole('button', { name: 'Send invitation' }).click();

  await expect(page.getByText(`Invitation sent to ${email}.`)).toBeVisible();
  const row = page.locator('tr', { hasText: email });
  await expect(row.getByText('Invitation pending')).toBeVisible();
  await shot(page, '05-users-en');
});

test('password pages: forgot password and a bad link', async ({ page }) => {
  await page.goto('/forgot-password');
  await page.getByLabel('البريد الإلكتروني').fill(DISPATCHER);
  await page.getByRole('button', { name: 'أرسل الرابط' }).click();
  await expect(page.getByRole('heading', { name: 'تحقق من بريدك' })).toBeVisible();
  await shot(page, '06-link-sent-ar');

  await page.goto('/reset-password#token=not-a-real-token-0123456789');
  await expect(page.getByText('هذا الرابط غير صالح أو مستخدم أو منتهي الصلاحية.')).toBeVisible();
  await expect(page).not.toHaveURL(/token=/); // removed from the address bar
});

test('signing out returns to sign-in and protected pages need a session', async ({ page }) => {
  await signIn(page, DISPATCHER);
  await expect(page.getByRole('heading', { name: 'المواعيد' })).toBeVisible();
  await page.getByRole('button', { name: 'تسجيل الخروج' }).click();
  await expect(page.getByRole('heading', { name: 'تسجيل الدخول' })).toBeVisible();
  await page.goto('/users');
  await expect(page).toHaveURL(/\/sign-in$/);
});

test('a dispatcher books a visit on the slot board, then moves and cancels it', async ({ page }) => {
  // Each browser project books its own container (one container can't be in two live visits).
  const container = test.info().project.name === 'phone' ? 'TCLU3334442' : 'TCLU1112220';
  await signIn(page, DISPATCHER);
  await expect(page.getByRole('heading', { name: 'المواعيد' })).toBeVisible();
  await page.goto(`/book?day=${bookedDay()}`);
  await expect(page.getByRole('heading', { name: 'حجز زيارة شاحنة' })).toBeVisible();

  // A typo in the check digit is caught before anything is sent.
  await page.getByLabel('رقم الحاوية').fill('TCLU1112229');
  await expect(page.getByText('الرقم الأخير لا يطابق بقية الرقم. تحقق من وجود خطأ في الكتابة.')).toBeVisible();
  await page.getByLabel('رقم الحاوية').fill(container);

  const open = page.locator('.slot[data-state="open"]');
  await open.nth(-2).click();
  await page.getByLabel('الشاحنة', { exact: true }).selectOption({ label: '5678 HDR' });
  await shot(page, '07-book-ar');
  await page.getByRole('button', { name: 'احجز الزيارة' }).click();

  await expect(page.getByText(/تم الحجز. رقم الموعد/)).toBeVisible();
  await expect(page.getByText(container).first()).toBeVisible();
  await expect(page.getByText('الفسح الجمركي')).toBeVisible();

  await page.getByRole('button', { name: 'English' }).click();
  await shot(page, '08-appointment-en');

  // Move it to another open time — a late one: cancelling closes 60 minutes before the slot.
  await page.getByRole('button', { name: 'Move to another time' }).click();
  await page.locator('.slot[data-state="open"]').last().click();
  await page.getByRole('button', { name: /^Move to \d\d:\d\d$/ }).click();
  await expect(page.getByText(/^Moved to /)).toBeVisible();

  // And cancel it.
  await page.getByRole('button', { name: 'Cancel the visit' }).click();
  await page.getByLabel('Reason (optional)').fill('Vessel delayed');
  await page.getByRole('button', { name: 'Yes, cancel the visit' }).click();
  await expect(page.getByText('The visit is cancelled and its place is free again.')).toBeVisible();
  await expect(page.getByText('Cancelled', { exact: true })).toBeVisible();
});
