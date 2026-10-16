# Terminal administration

For terminal admins and operator admins: companies and users, operators, the terminal map, personal data and the system's health.

## Setting up a terminal

**Terminal set-up** shows what the terminal has and what it still needs, step by step. Trucks can book once the first
four steps are done: move types, weekly plan, bookable slots and gate lanes. Each step opens with its quick actions:

- **Add the usual move types**
- **Make a starter plan**: gate hours, days and trucks per slot
- **Make the slots now**
- **Add a lane**
- **Test the connection** to the terminal operating system

System admins and operator admins can **Add a terminal** there. It starts with the usual move types, an in and an out
lane and the usual booking rules.

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

## Operators: several terminals, one team

When one company runs several terminals on the same Yardsyncs, the system admin groups them under an operator:
**Fleet & users** → **Operators** → **Add an operator** (a code and a name), then **Add a terminal** for each of its
terminals and **Add an operator admin**.

An **Operator admin** manages every terminal of the operator, as a terminal admin would at each one, and picks the
terminal at the top of the screen. They can add fellow operator admins and staff for any of the operator's terminals,
but see nothing of terminals outside it. Taking a terminal out (**Take out**) or deactivating the operator counts at
once.

## Look and name

The name, main colour and logo show at sign-in, in the portal, in the drivers' app and in messages. A system admin
sets **This installation's look and name** under **Fleet & users** → **Operators**. Each operator can have its own
under **Look and name**, which shows for its terminals; operator admins can change their own. Empty fields take the
installation's, then Yardsyncs.

- **Main colour**: buttons show white text on it, so a colour that is too light is refused.
- **Logo**: PNG, JPEG or WebP up to 256 KB.
- **Own web address**: the operator's people sign in there and see its look from the first page. A system admin sets
  it after the DNS name and certificate are in place.

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

## Lanes and devices

**Lanes** lists each gate lane with the equipment at it: cameras, OCR, RFID readers, driver kiosks, barriers, traffic
lights, displays, ticket printers, intercoms and weighbridges. For each device record its code, what it reads (plates,
container numbers, ISO codes, seals, damage photos, tags), the vendor and model, and the address the lane controller
uses. Any make works: the kind of device decides its role.

**Finds the appointment by** shows how the lane can identify trucks, from its devices and the terminal's gate rules.
A lane works **Manual** (a clerk at the lane) or **Automatic**. Automatic needs a barrier and a way to identify trucks
without a clerk — a kiosk, or a camera reading plates or container numbers; the screen says what is missing. While a
lane is automatic, its barrier and its last way to identify trucks can't be removed. **Retire lane** takes a lane away
for good; opening and closing for the day is on the gate screen.

To connect a device, choose **Device key** and give the key to whoever sets up the device or its vendor software (it
is shown only once; choosing it again replaces it). **Connection** then shows each device **Online**, **Fault** (with
what the device reports), **Offline** (silent for two minutes) or **Not connected**. The technical page *Connecting
lane devices* is for the integrator.

If the terminal records gate passages in its TOS (Capacity setup → Gate), **Gate records in the TOS** at the bottom
of **Lanes** lists what is waiting or was not taken, with the TOS's reason. **Send again** once the cause is fixed;
**Send now** doesn't wait for the next round.

## Personal data

Drivers' **Copy of data** and **Erase personal data** are in **Fleet & users** → **Drivers** (see the guide Your
fleet). Old positions, message addresses and sessions are removed automatically; the periods are set per
installation.

## System status

For the IT team: `/health/ready` tells whether the server and database are up, and `/api/ops/status` (signed in as a
terminal admin) shows the version, the database, messages and events waiting to be sent, and settings that still
need attention. Install, upgrade and backups are in the server guide that comes with the installation.
