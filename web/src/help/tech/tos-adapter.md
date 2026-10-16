# Connecting another TOS

Yardsyncs checks every move against the terminal operating system (TOS): is the import container in the yard and
released, is the export booking open, is the cargo released. Yardsyncs ships with a Navis N4 adapter and a demo TOS.
For another TOS, a developer writes an adapter in C#. This page is the kit: what the adapter does, the rules its
answers must keep, and the tests it must pass.

## The interface

An adapter implements `ITosAdapter` (`src/Tas.Application/Tos/ITosAdapter.cs`):

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

`TosAdapterConformance` (`tests/Tas.Infrastructure.Tests/TosKit/`) holds the tests every adapter must pass:

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

1. Put the adapter in `src/Tas.Infrastructure/Tos/<Name>/` with its options section (connection per terminal code,
   secrets from environment variables).
2. Register it in `DependencyInjection.cs`.
3. Add its name to `TerminalProfiles.TosAdapters` and to `TosRouter`.
4. Choose it per terminal: `Terminals__<code>__Tos=<Name>`.
5. Run the conformance tests in CI, then against the customer's test TOS before go-live.
