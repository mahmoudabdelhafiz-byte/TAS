# Planning capacity

For planners and terminal admins: how many trucks the terminal takes each hour, for which moves, and how it went.

## The weekly plan

**Capacity setup** → **Weekly plans** holds the terminal's week: for each day and hour, **Trucks per slot**, an
**Overbooking %** (to make up for no-shows) and a **Limit per move type** (for example at most 15 import pickups in an
hour). An empty limit means the move type can use the whole hour.

1. Drag across the grid to pick hours, or click a day or an hour for a whole column or row.
2. Set the numbers on the right and **Apply to … slots**, or **Close these hours**.
3. **Save plan**. The slots for the coming days are updated at once; a slot that now has more bookings than places is
   flagged **Over capacity** on the slot board (bookings are never removed).

**All year (default)** applies whenever no seasonal plan covers the day. **New seasonal plan** (Ramadan, Hajj season,
a vessel peak) replaces it between two dates; when two seasonal plans overlap, the higher **Priority** wins.

## Reserved places and rules

- **Reserved places** — hold places for a company or as a **Walk-in and emergency reserve**. Unused places are
  released to everyone the set hours before the slot.
- **Release of unused limits** — hours before the slot when a move type's unused places become free for any move.
- **Booking rules** — how early and late a truck may arrive, until when companies may cancel or move, and the
  appointments per truck per day.

Every change is kept in the change history with who made it and the reason.

## The slot board

**Slot board** shows each hour of a day: booked against places, by move type. To change one day without changing the
plan, open an hour and **Close the hour**, **Set capacity to** or **Add or remove trucks** (for one move type or all),
with a reason. Trucks already booked in a closed hour keep their booking and are told the hour is closed.
**Changes on this day** lists them; **Remove change** returns to the plan.

## KPIs

**KPIs** for the last 7 or 30 days, or the next 7:

- **Slot use** — places booked against places offered, by day and by hour of the day.
- **Appointments**, **No-shows**, **On time** and **Walk-ins**.
- **Turn time** — from arrival to gate-out, with the waiting at the gate.
- **Inside the terminal** — gate to block, time at the block, block to gate-out, and the blocks slowest first.

**Show the numbers as a table** gives the figures behind each chart.
