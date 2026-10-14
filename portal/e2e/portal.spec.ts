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

/** Screenshot for review — and every page must fit the screen width (no sideways scrolling, in either direction). */
async function shot(page: Page, name: string) {
  const { overflow, culprits } = await page.evaluate(() => {
    const width = document.documentElement.clientWidth;
    // Elements sticking out of the screen whose children don't (the innermost cause), widest first.
    const outside = [...document.querySelectorAll<HTMLElement>('body *')].filter((el) => {
      const r = el.getBoundingClientRect();
      return r.width > 0 && (r.right > width + 1 || r.left < -1);
    });
    const innermost = outside.filter((el) => !outside.some((other) => other !== el && el.contains(other) && el.tagName !== 'TABLE'));
    return {
      overflow: document.documentElement.scrollWidth - width,
      culprits: innermost
        .sort((a, b) => b.getBoundingClientRect().width - a.getBoundingClientRect().width)
        .slice(0, 4)
        .map((el) => `${el.tagName.toLowerCase()}.${el.className} [${Math.round(el.getBoundingClientRect().left)}..${Math.round(el.getBoundingClientRect().right)}] "${(el.textContent ?? '').slice(0, 40)}"`),
    };
  });
  await page.screenshot({ path: `e2e/screenshots/${test.info().project.name}-${name}.png`, fullPage: true });
  expect(overflow, `${name} is ${overflow}px wider than the screen: ${culprits.join(' | ')}`).toBeLessThanOrEqual(0);
}

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
  await page.getByRole('button', { name: 'Send invitation', exact: true }).click();

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

  await shot(page, '07-book-ar');
  const open = page.locator('.slot[data-state="open"]');
  await open.nth(-2).click();
  await page.getByLabel('الشاحنة', { exact: true }).selectOption({ label: '5678 HDR' });
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
  await expect(page.getByText('This visit can’t be changed any more (Cancelled).')).toBeVisible();
});

test('a dispatcher uploads a booking sheet, previews it and books the ready visits', async ({ page }) => {
  // Per project its own containers; the last row has a wrong check digit and must fail on its own.
  const [a, b, c, bad] =
    test.info().project.name === 'phone'
      ? ['TRIU5000011', 'TRIU5000027', 'SEGU3000019', 'TRIU5000012']
      : ['SEGU2000014', 'SEGU2000020', 'SEGU3000024', 'SEGU3000025'];
  const day = bookedDay();
  const csv = [
    'visit,date,time,category,container_no',
    `V1,${day},22:00,IMP_PICKUP,${a}`,
    `V1,${day},22:00,IMP_PICKUP,${b}`,
    `V2,${day},22:00,IMP_PICKUP,${c}`,
    `V3,${day},22:00,IMP_PICKUP,${bad}`,
  ].join('\n');

  await page.goto('/sign-in');
  await page.getByRole('button', { name: 'English' }).click();
  await signIn(page, DISPATCHER);
  await page.getByRole('link', { name: 'Book a visit' }).first().click();
  await page.getByRole('link', { name: 'Many visits? Upload a sheet' }).click();
  await expect(page.getByRole('heading', { name: 'Upload a booking sheet' })).toBeVisible();

  await page.getByLabel(/^Sheet/).setInputFiles({ name: 'visits.csv', mimeType: 'text/csv', buffer: Buffer.from(csv) });
  await page.getByRole('button', { name: 'Check the sheet' }).click();
  await expect(page.getByText('2 of 3 visits can be booked.')).toBeVisible();
  await expect(page.locator('tr[data-result="Failed"]')).toHaveCount(1);
  await shot(page, '09-upload-preview-en');

  await page.getByRole('button', { name: 'Book the 2 visits that are ready' }).click();
  await expect(page.getByRole('heading', { name: '2 of 3 visits booked.' })).toBeVisible();
  await expect(page.locator('tr[data-result="Booked"] a')).toHaveCount(2);
});

test('a company admin adds a truck (Arabic plate) and a driver', async ({ page }) => {
  const digits = String(Date.now() % 9000 + 1000);
  const plate = test.info().project.name === 'phone' ? `د ر س ${digits}` : `أ ب ح ${digits}`;
  const id = `10${String(Date.now()).slice(-8)}`;

  await signIn(page, DISPATCHER);
  await page.getByRole('link', { name: 'الشاحنات والسائقون' }).click();
  await expect(page.getByRole('heading', { name: 'الشاحنات والسائقون', level: 1 })).toBeVisible();

  await page.getByRole('button', { name: 'أضف شاحنة' }).click();
  await page.getByLabel('رقم اللوحة').fill(plate);
  await page.getByRole('button', { name: 'احفظ' }).click();
  await expect(page.getByText(/تمت إضافة الشاحنة/)).toBeVisible();

  await page.getByRole('button', { name: 'أضف سائقًا' }).click();
  await page.getByLabel('الاسم الكامل').fill('سعيد القحطاني');
  await page.getByLabel('رقم الهوية / الإقامة').fill(id);
  await page.getByLabel('الجوال').fill('0551234567');
  await page.getByRole('button', { name: 'احفظ' }).click();
  await expect(page.getByText('تمت إضافة سعيد القحطاني.')).toBeVisible();
  await expect(page.getByText('+966551234567').first()).toBeVisible(); // mobile stored in international form
  await shot(page, '10-fleet-ar');
});
