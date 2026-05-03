# CanScanmatik

WinForms application for reading and transmitting classic CAN frames through `Scanmatik` over the `J2534` PassThru interface.

## Features

- auto-detects installed `SM2` / `SM3` J2534 drivers from `PassThruSupport.04.04`
- opens the registered Scanmatik J2534 DLL directly
- connects to raw CAN at the selected bitrate
- reads `11-bit` or `29-bit` frames
- switches physical CAN routes for `CAN1 6-14`, `CAN2 3-11`, `CAN3 12-13`, `CAN4 1-9`, `CAN5 2-10`
- keeps separate workspaces/logs per selected CAN route
- sorts the table by `ID` or by recent activity
- highlights bytes that changed in the latest frame for the same ID
- shows last frame, count, period, and driver timestamp per ID
- writes session logs and exports the current table to CSV
- saves the current CAN ID list to a text file and writes `last_ids` automatically on disconnect
- sends custom CAN frames with `Send Once` or periodic `Start TX`
- stores multiple TX frames in a list and can cycle through the whole list with `Start TX`
- sends the whole TX list one time with `Send List`
- saves and loads the TX list as a JSON file with `Save TX` / `Load TX`
- supports byte sweep mode for brute-force style stepping of `D0..D7`
- captures the selected RX row into the TX editor with `Capture Row` or by double-clicking a frame

## Notes

- the project is built as `x86` because the installed Scanmatik J2534 driver is registered as `32-bit`
- this version targets classic raw CAN and uses the standard J2534 CAN channel for `CAN1 6-14`
- alternate routes use `J2534-2` pin selection via `CAN_PS + J1962_PINS`
- `CAN3` is currently mapped as `12H-13L`
- Scanmatik hardware support still depends on the actual adapter:
  - plain `SM2` officially supports `CAN1 6-14` and `CAN2 3-11`
  - `CAN3 12-13` requires `SM2-PRO` or `SM3`
  - `CAN4 1-9` and `CAN5 2-10` are `SM3`-class routes

## Build



## Run

1. Connect the Scanmatik adapter.
2. Start the application.
3. Select adapter, bus route, bitrate, and frame format.
4. Click `Connect`.
5. Use the `Transmit` block to send your own frame, add multiple frames into the TX list, run cyclic list TX, or sweep one byte over a range.
