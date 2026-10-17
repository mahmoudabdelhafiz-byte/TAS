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
- **At the gate** — how the gate finds a truck's appointment: the driver's gate pass always, and the appointment
  number, the plate or a container number unless you turn them off. Also how strictly the driver is checked:
  - not at all
  - the appointment must name its driver
  - the named driver shows the gate pass from Yardsyncs mobile

  Trucks that don't meet the rules go to the trouble desk as **Driver not identified**. With Yardsyncs Autogate,
  **Seals at the entry** can also hold a truck whose seal, read by the lane cameras, isn't the one the company declared
  (**Seal doesn't match**); a seal not read at all never holds a truck.
- **Fees** (terminal admins) — booking, no-show and late-cancellation fees in the terminal's currency, and how many
  hours before the slot a cancellation counts as late. **Charges** lists what was charged per company. Download it for
  billing, or **Waive** a charge with a reason.

Every change is kept in the change history with who made it and the reason.

## The slot board

**Slot board** shows each hour of a day: booked against places, by move type. To change one day without changing the
plan, open an hour and **Close the hour**, **Set capacity to** or **Add or remove trucks** (for one move type or all),
with a reason. Trucks already booked in a closed hour keep their booking and are told the hour is closed.
**Changes on this day** lists them; **Remove change** returns to the plan.

On a public holiday of the terminal's country the board says so. Slots stay open unless you choose **Close the whole
day**; the dates of Eid and other lunar holidays are expected dates and follow the moon sighting, so check the official
announcement.

## KPIs

**KPIs** for the last 7 or 30 days, or the next 7:

- **Slot use** — places booked against places offered, by day and by hour of the day.
- **Appointments**, **No-shows**, **On time** and **Walk-ins**.
- **Turn time** — from arrival to gate-out, with the waiting at the gate.
- **Inside the terminal** — gate to block, time at the block, block to gate-out, and the blocks slowest first.
- **Lanes** (with Yardsyncs Autogate) — per automatic lane: **Time at the lane** from the first camera read to through
  the barrier, trucks **Without a clerk**, **Exceptions** and **Why clerks helped**, and how often the cameras got it
  right (**Plates read**, **Containers read**: read and not corrected by a clerk).

**Show the numbers as a table** gives the figures behind each chart.

## Companies

At the bottom of **KPIs**, **Companies** lists every trucking company of the period, the most no-shows first. Open one
to see its trucks, drivers, holds and charges, the same report the company sees for itself.
**Download Excel** saves the KPIs (summary, days, hours, companies); **Appointments in Excel** every appointment of
the period with its gate times, waiting and turn time.

## Reports by email

Under **Reports by email** at the bottom of **KPIs**, add the people who should get the reports (they need no portal
account), each in English or Arabic, and tick **Daily** and / or **Weekly**. The daily report covers yesterday and
today's bookings; the weekly one the seven days before; both come with the figures as an Excel file. Choose the hour
(**Sent at**) and the day of the weekly one (**Weekly report on**). The installation must have email set up.
