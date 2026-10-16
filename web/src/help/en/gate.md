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

## Trouble desk

Every truck sent to the trouble desk waits in **Trouble desk** with its reason. Write a **Note** (required), then
**Let in**, **Turn away** (the appointment is closed and its place freed) or **Close case** (nothing to do). Notes are
kept with the visit.

## Gate out

Trucks inside are listed with how long they have been in. **Gate out** when the truck leaves: the visit is completed
and the turn time counted.

## The figures on top

**At the gate**, **In the terminal**, **Completed today**, **Average turn time** and **At the trouble desk** are for
the whole terminal, updated every few seconds.
