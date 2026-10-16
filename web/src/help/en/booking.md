# Booking trucks and following appointments

## Book one truck

1. Open **Book a truck**.
2. **What is the truck doing?** Choose the move: import pickup, export drop-off, empty return, empty pickup or cargo.
3. Pick the day and an **Arrival hour**. Each hour shows how many places are free; **Full** and **Closed** hours
   can't be booked.
4. Under **Moves on this truck** enter the **Container number** (for an empty pickup a **Booking reference** is
   enough; for cargo the **BL / delivery order** and the **Planned tonnes**). **Add a move** puts a second move on the
   same truck, for example an export drop-off and an import pickup.
5. Choose the **Truck** and **Driver**, or **Assign later**.
6. Press **Book**. Yardsyncs checks every container with the terminal before it books.

The answer is **Booked** with the appointment number, or **The terminal did not accept this booking** with the reason
for each container (not in the yard yet, customs hold, line not released, charges unpaid…). Nothing is booked when
the terminal refuses; fix the reason and try again.

## Book several trucks (by bill of lading)

For many containers at once — an import bill of lading, or a list pasted from Excel:

1. In **Book a truck** choose **Several trucks (by BL or container list)**.
2. Search by **Bill of lading** or paste **Container numbers** (one per line), then **Find containers**.
3. The list shows each container's status. **Select all ready** picks those that can be collected.
4. **Two 20′ containers on one truck** pairs 20-foot containers, so fewer trucks are needed.
5. Pick one or more **Arrival hours**. Trucks fill them in order.
6. Press **Book … trucks**. Each truck becomes its own appointment; the result shows which were booked and why
   any were not. **Try the … not booked in other hours** tries again with the hours still free.

Assign the truck and driver on each appointment before it leaves.

## Bulk upload (Excel)

For a day's plan prepared in a spreadsheet:

1. **Bulk upload** → **Download the template** (Excel or CSV). One row per move: `date`, `time`, `category`,
   `container_no` and, when known, `truck_plate` and `driver_id` (the driver's ID or iqama number). Rows with the same
   `visit` go on the same truck.
2. **Choose file** and **Check the file**. Checking books nothing: every row is checked and errors are shown by row.
3. **Book** books every visit that passed; the others stay listed with the reason. Up to 500 rows and 2 MB per file.

## Your appointments

**Appointments** lists the day's visits with their hour, truck, driver, moves, status and **Ready to go?**. Open one
to see everything about it.

| Ready to go? | What to do |
|---|---|
| **Clear to go** | All containers are cleared. The truck can leave at its time. |
| **Check first** | A warning, for example storage charges start today. The truck may go; read the note. |
| **On hold** | Something blocks the move (customs, line, terminal hold, payment). Don't send the truck: it will be turned away. |
| **Not checked** | The terminal's system couldn't be reached; it is checked again before arrival. |

The terminal checks every booked container again on its own (every 15 minutes in the hours before the visit). When a
container goes on hold or is cleared again, the driver gets a message; you see it under **Ready to go?**.

In an appointment you can:

- **Re-check now** — ask the terminal again, for example after paying charges.
- **Truck and driver** — assign or change them. The driver sees the appointment in the app at once.
- **Move to another hour** — choose a free hour; the old place is freed.
- **Cancel appointment** — with a reason. Cancel or move only up to the time the terminal set (for example an hour)
  before the slot; after that, ask the terminal.
- **Truck is leaving** — tells the terminal the truck is on the way (the driver normally does this in the app).
- **Messages sent** — every SMS, app or email message about this appointment and whether it was delivered.
- **Gate code** — the QR code the driver shows at the gate, also for printing when a driver has no smartphone.

## Arriving

Trucks should arrive within their hour. The terminal allows a few minutes early and late (set by the terminal; for
example 30 minutes). A truck that doesn't come by the end of its hour and the late minutes is marked **No-show** and
its place is freed; no-shows are counted per company.

At the gate the clerk scans the gate code or types the plate. A truck with a valid appointment that is **Clear to go**
is let in; anything else goes to the trouble desk.
