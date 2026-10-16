# Terminal administration

For terminal admins: companies and users, the terminal map, personal data and the system's health.

## Bringing a trucking company on board

1. **Fleet & users** → **Companies** → **Register a company**: name, CR number and type.
2. **Users** → choose the company → **Add a user** with the role **Company admin**. They get an email to choose
   their password.
3. Send them the link to the portal and point them to **Help** (Getting started, Booking trucks, Your fleet).
4. The company admin adds the company's trucks, drivers and dispatchers, and gives drivers the Yardsyncs mobile guide.

A company that breaks the rules can be **Suspend**ed (no new bookings); trucks and drivers can be **Block**ed with a
reason, which the gate shows.

## Terminal staff

**Users** → **Terminal staff** → **Add a user**: **Gate clerk**, **Planner** or **Terminal admin**. Each sees only this
terminal.

## The terminal map

**Terminal map** is what the drivers' app uses inside the terminal: the yard blocks, gate, exit, pre-gate and the
roads between them.

- **Draw area** — click the corners of a block, gate, exit or parking. A yard block needs its **Block in the TOS**,
  exactly as N4 names it, so trucks are guided to the block where their container is. **Entering this area checks
  the truck in** on the pre-gate.
- **Draw road** — click along the roads trucks drive on; mark **One-way** roads in the drawn direction.
- **Import from a drawing** — a GeoJSON file exported from CAD or GIS.
- **Try a route** — click a start and an end to see the route a truck would take.
- **Ready for the driver app?** lists what is missing (a block that can't be reached from the gate, roads that don't
  meet…). **Save map** when it says every block can be reached.

## Personal data

Drivers' **Copy of data** and **Erase personal data** are in **Fleet & users** → **Drivers** (see the guide Your
fleet). Old positions, message addresses and sessions are removed automatically; the periods are set per
installation.

## System status

For the IT team: `/health/ready` tells whether the server and database are up, and `/api/ops/status` (signed in as a
terminal admin) shows the version, the database, messages and events waiting to be sent, and settings that still
need attention. Install, upgrade and backups are in the server guide that comes with the installation.
