# Connecting another TOS

Yardsyncs checks every move against the terminal operating system (TOS): is the import container in the yard and
released, is the export booking open, is the cargo released. Yardsyncs ships with a Navis N4 adapter and a demo TOS.
For another TOS, a developer writes an adapter in C#. This page is the kit: what the adapter does, the rules its
answers must keep, and the tests it must pass.

## The interface

An adapter implements `ITosAdapter` (`src/Yardsyncs.Application/Tos/ITosAdapter.cs`):

| Member | What it does |
|---|---|
| `Source` | A short name stored with every check, e.g. `N4` (at most 20 characters) |
| `ValidateAsync(terminalId, items)` | The checks for each move of a visit; the heart of the adapter |
| `FindBlContainersAsync(terminalId, blNo)` | The containers on an import bill of lading, or `null` when the TOS doesn't know it |
| `YardPositionsAsync(terminalId, items)` | Where each move happens in the yard (block, row / bay / tier), for the driver's directions |

Each item (`TosItemQuery`) has its own number (`Index`), the move type code (e.g. `IMP_PICKUP`), the mode (container
or cargo), and the container, booking, EDO or cargo reference. It also carries the slot start, for cut-offs. Each check
(`TosCheck`) names the item, the type of check, the result and, unless it passes, the reason in plain words. The reason
is what the dispatcher and the gate clerk read.

## The rules

| Move | Checks it must get |
|---|---|
| Import pickup (`IMP…`) | `Availability`; unless that fails, also `CustomsRelease`, `LineRelease`, `TerminalHold` and `Payment` |
| Export drop-off (`EXP…`) | `BookingValid`; unless that fails, also `Cutoff` (`Vgm` when the TOS knows the container) |
| Empty pickup (`MTY_PICKUP`) | `BookingValid` |
| Other container moves (empty return…) | `TerminalHold` |
| Cargo (BL / DO) | `CargoRelease` |

- **Checks:**
  - Return checks only for the items you were asked about.
  - Never return `ContainerNumber` or `TosUnavailable`; Yardsyncs writes those itself.
  - A `Warn` or `Fail` check has a message of at most 400 characters. A hold gives one `Fail` per hold.
  - Match container numbers, bookings and BLs whatever their case.
- **Lists:**
  - BL containers: upper case, each once.
  - Yard positions: at most one per item, trimmed, `null` for an unknown part. Leave out an item you know nothing about.
- **When the TOS can't answer** (timeout, error, unreadable reply), throw `TosUnavailableException`. Never return an
  empty or partial answer: the booking then goes ahead with a warning (or is refused, by setting) and is re-checked.
- **Read-only:** nothing is written to the TOS.

Yardsyncs holds every live answer to these rules (`TosContract`). An answer that breaks them is treated as "the TOS
could not be reached" and logged as an error. A container on hold can't get through because an adapter forgot a check.

## The tests

`TosAdapterConformance` (`tests/Yardsyncs.Infrastructure.Tests/TosKit/`) holds the tests every adapter must pass:

- every kind of move gets its checks
- a ready import passes, a customs hold fails with its reason, an unknown container fails
- container numbers match whatever their case
- bookings and cargo releases are checked
- no moves give no checks
- a bill of lading lists its containers
- yard positions keep the rules
- an unreachable TOS throws `TosUnavailableException`

To test a new adapter:

1. Derive a test class from `TosAdapterConformance`.
2. Give it the adapter connected to a test TOS: a stub of its web service, or a test system. The test TOS holds the
   units, bookings and bills of lading of `TosScenario.Standard`, or your own scenario.
3. Give it the same adapter when its TOS is down.

`FakeTosConformanceTests` and `N4ConformanceTests` are the examples; the N4 one simulates the TasLookup code extension.

## Plugging it in

1. Put the adapter in `src/Yardsyncs.Infrastructure/Tos/<Name>/` with its options section (connection per terminal code,
   secrets from environment variables).
2. Register it in `DependencyInjection.cs`.
3. Add its name to `TerminalProfiles.TosAdapters` and to `TosRouter`.
4. Choose it per terminal: `Terminals__<code>__Tos=<Name>`.
5. Run the conformance tests in CI, then against the customer's test TOS before go-live.

## Recording gate passages (optional)

A terminal can have Yardsyncs record each gate passage in its TOS. It turns this on with **Record each gate-in and
gate-out in the TOS** under Capacity setup → Gate (setting `tos_gate_write`). An adapter that supports this also
implements `ITosGateWriter` (`src/Yardsyncs.Application/Tos/TosGate.cs`):

| Member | What it does |
|---|---|
| `RecordAsync(terminalId, request)` | Gate-in: create the truck visit with one transaction per move and pass the in-gate. Gate-out: pass the out-gate for the visit |

The request carries the appointment number, truck, driver, company, gate and lane, the time, and the moves (move type,
container and ISO type, booking / EDO / cargo reference, seals seen at the gate). At gate-out it also carries
`VisitReference`, the TOS's own key that the gate-in returned. The answer is one of three:

- `Done(visitReference)`: the TOS recorded the passage.
- `Refused(message)`: the TOS said no, and sending again won't help. The record waits for a person.
- `Unreachable(message)`: the TOS couldn't be reached. The record is tried again after 1, 2, 4… minutes (at most an
  hour apart), up to ten tries.

The gate never waits for the TOS. Each passage is queued in the same save as the passage itself, and a worker sends it
(`TosGate__IntervalSeconds`, 10). A gate-out goes only after its gate-in. Records that weren't taken show under **Gate
records in the TOS** on the Lanes screen, with **Send again**, and on `GET /api/terminals/{id}/gate/tos-records`.

**Navis N4.** `N4GateWriter` sends N4 gate web-service documents through ArgoService `genericInvoke`:
`process-truck` at the in-gate stage with the truck transactions, and at the out-gate stage by `tv-key`. Set the gate
per terminal: `N4__Terminals__<code>__GateId`, `…__IngateStage` (`ingate`), `…__OutgateStage` (`outgate`), and
`…__TranTypes__<move type>` where the defaults don't fit. The defaults are IMP_PICKUP → DI, EXP_DROP → RE,
MTY_RETURN → RM, MTY_PICKUP → DM, BB_DELIVERY / BULK_DELIVERY → DB. The N4 user needs gate rights.

The documents in `N4GateRequests` follow the N4 gate API, but N4 versions and gate configurations differ. Check them
on the terminal's N4 test system before turning `tos_gate_write` on.

