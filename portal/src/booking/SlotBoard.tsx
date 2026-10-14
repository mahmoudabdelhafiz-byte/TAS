import { useI18n } from '../i18n/I18n';
import type { SlotAvailability } from '../api/types';

interface SlotBoardProps {
  slots: SlotAvailability[];
  selected: number | null;
  onSelect: (slotId: number) => void;
  timeZone: string;
  label: string;
  /** The slot the appointment is in now (when moving it); shown but not selectable. */
  current?: number | null;
}

/**
 * The day as a row of hour slots — the same strip as on the sign-in page, now to pick from. Each slot shows
 * how many more trucks of the chosen type it takes. A radio group: arrow keys move between slots.
 */
export function SlotBoard({ slots, selected, onSelect, timeZone, label, current }: SlotBoardProps) {
  const { t, formatDateTime } = useI18n();
  const time = (iso: string) => formatDateTime(iso, { hour: '2-digit', minute: '2-digit', hourCycle: 'h23' }, timeZone);
  const selectable = slots.filter((s) => !s.isClosed && s.available > 0 && s.slotId !== current);

  function move(from: number, step: number) {
    const index = selectable.findIndex((s) => s.slotId === from);
    const next = selectable[(index + step + selectable.length) % selectable.length];
    if (next) {
      onSelect(next.slotId);
      document.getElementById(`slot-${next.slotId}`)?.focus();
    }
  }

  return (
    <div className="slot-board" role="radiogroup" aria-label={label}>
      {slots.map((slot) => {
        const open = !slot.isClosed && slot.available > 0 && slot.slotId !== current;
        const isSelected = slot.slotId === selected;
        const state = slot.isClosed ? t('slotClosed') : slot.available > 0 ? t('slotLeft', { n: slot.available }) : t('slotFull');
        const focusable = isSelected || (selected === null && slot.slotId === selectable[0]?.slotId);
        return (
          <button
            key={slot.slotId}
            id={`slot-${slot.slotId}`}
            type="button"
            role="radio"
            aria-checked={isSelected}
            aria-disabled={!open}
            tabIndex={focusable ? 0 : -1}
            className="slot"
            data-state={slot.slotId === current ? 'current' : slot.isClosed ? 'closed' : slot.available > 0 ? 'open' : 'full'}
            onClick={() => open && onSelect(slot.slotId)}
            onKeyDown={(e) => {
              const forward = document.documentElement.dir === 'rtl' ? 'ArrowLeft' : 'ArrowRight';
              const back = document.documentElement.dir === 'rtl' ? 'ArrowRight' : 'ArrowLeft';
              if (e.key === forward || e.key === 'ArrowDown') (e.preventDefault(), move(slot.slotId, 1));
              if (e.key === back || e.key === 'ArrowUp') (e.preventDefault(), move(slot.slotId, -1));
            }}
          >
            <span className="slot-time code">{time(slot.startAt)}</span>
            <span className="slot-state">{state}</span>
          </button>
        );
      })}
    </div>
  );
}
