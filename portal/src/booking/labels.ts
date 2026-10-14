import type { Category, Check, CheckType } from '../api/types';
import type { Language } from '../i18n/I18n';
import type { MessageKey } from '../i18n/en';

export const checkTypeKeys: Record<CheckType, MessageKey> = {
  ContainerNumber: 'checkContainerNumber',
  Availability: 'checkAvailability',
  CustomsRelease: 'checkCustomsRelease',
  LineRelease: 'checkLineRelease',
  TerminalHold: 'checkTerminalHold',
  Payment: 'checkPayment',
  BookingValid: 'checkBookingValid',
  Vgm: 'checkVgm',
  Cutoff: 'checkCutoff',
  CargoRelease: 'checkCargoRelease',
  TosUnavailable: 'checkTosUnavailable',
};

export const checkResultKeys: Record<Check['result'], MessageKey> = {
  Pass: 'checkPass',
  Warn: 'checkWarn',
  Fail: 'checkFail',
};

export function categoryName(categories: Category[] | undefined, code: string, language: Language): string {
  const category = categories?.find((c) => c.code === code);
  if (!category) return code;
  return language === 'ar' ? category.nameAr : category.nameEn;
}
