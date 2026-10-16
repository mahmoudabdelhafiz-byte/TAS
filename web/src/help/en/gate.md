# Working the gate

The **Gate** screen is for the gate clerks: trucks at the gate, trucks inside, and the trouble desk.

## A truck at the gate

1. Choose your **Lane**.
2. Scan the driver's gate code (the scanner types it into the box), or type the **Appointment number or plate**.
3. Press **Check truck**.

Yardsyncs checks the appointment: right terminal, right time (with the early and late minutes), containers cleared,
truck and driver not blocked.

- All good: **Let in: …** — the truck goes in, the driver's app shows the in-gate ticket and the first block.
- Otherwise the truck is **Sent to the trouble desk** with the reason:

| Reason | Usual action |
|---|---|
| **No appointment** | Walk-in: let in if there is room and the terminal allows it, or turn away |
| **Too early** / **Too late** | Ask the driver to wait for the hour, or let in if the yard has room |
| **Not cleared** | A container is on hold: turn away, or wait for the release |
| **Truck or driver blocked** | Turn away; the terminal admin knows why |
| **Already inside** | The appointment was used already: check the plate |
| **Appointment cancelled** | Turn away; the company can book again |

## Lanes

The row of lanes on top shows every lane: **Open**, **Closed** or **Out of order**, and whether it works automatically.
To take a lane out of use, choose **Close lane**, pick **Closed** or **Out of order**, and write the reason: everyone at
the gate sees it, and a truck checked at that lane is refused with that reason. **Open lane** puts it back. Only open
lanes are offered for checking trucks in and out.

A lane with connected equipment also shows its latest read (plate and containers; a container number underlined in
amber may be misread) and any device that is **Not working**. **Open barrier** raises the lane's barrier from here,
for a truck you have admitted.

At an **Automatic** lane the lane decides for itself, as you would: a truck on time gets green and the barrier opens;
otherwise it waits on red with the reason on the lane display, and the tile shows **Waiting for a clerk**. Its case is
on the trouble desk: **Let in** there opens that lane's barrier.

## Remote clerk

**Remote clerk** is for handling automatic lanes from a desk. The lane tiles are on top, and **Waiting for a clerk**
lists the trucks the lanes referred, oldest first. Choose one to see what the cameras read, with their photos; a
container number underlined in amber may be misread.

- **Correct the reads**: fix the plate or the containers, or type the appointment number the driver gives you, then
  **Check again**. The lane checks the truck as before: if it is in order the barrier opens, otherwise it waits with
  the new reason. Corrections are counted for the cameras' read rate.
- **Decide**: write a note, then **Let in** (the barrier opens) or **Turn away** (the display tells the driver).
- **The driver**: **Call the driver** connects the lane intercom, **Open barrier** raises the barrier, and a message
  can be shown on the lane display.

Cases from manual lanes stay on the gate screen's trouble desk.

## Trouble desk

Every truck sent to the trouble desk waits in **Trouble desk** with its reason. Write a **Note** (required), then
**Let in**, **Turn away** (the appointment is closed and its place freed) or **Close case** (nothing to do). Notes are
kept with the visit.

## Gate out

Trucks inside are listed with how long they have been in. **Gate out** when the truck leaves: the visit is completed
and the turn time counted.

**Gate ticket** shows the visit's interchange receipt: company, truck, driver, times and lanes, the moves with their
seals, damage seen at the gate, and the TOS visit once recorded. **Print** prints only the ticket. After a gate out,
the link below the list opens that truck's ticket.

## The figures on top

**At the gate**, **In the terminal**, **Completed today**, **Average turn time** and **At the trouble desk** are for
the whole terminal, updated every few seconds.
