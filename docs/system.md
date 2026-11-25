# System

What the machine is doing, and what takes the space on its disks. Best effort: what can be read depends on the
platform and the hardware.

## Readings

| Card | What it shows |
|---|---|
| CPU | Use, with a graph of the last two minutes. |
| Memory | Use, and swap. |
| GPUs | Use and memory, where the driver reports them. |
| Network | Traffic in and out. |
| Disk activity | Reads and writes. |
| Batteries | The laptop's battery, and those of devices that report one (a mouse, a headset). |

Readings are taken every two seconds, only while the page is on screen, so the page costs nothing when you are
elsewhere. `avamux system status` prints the same from the command line.

## Volumes and disk maps

**Volumes** lists every mounted volume and how full it is. Scan one (or any folder, with **Scan a Folder…**) to see
what takes the space: a window opens with a treemap of the whole scan, every file drawn to scale and coloured by file
type, beside an explorer list (name, size, files, modified) of the folder being looked at.

- Click to select, double-click (or Enter) to go into a folder or open a file; Backspace goes up a folder, and the
  bar at the top goes back up to any level. Alt+Left / Alt+Right walk back and forward.
- Right-click an entry: Open, Go Into This Folder, Open in File Manager, Show in File Manager, Open a Terminal Here,
  Copy Path, Scan This Folder Again.
- While the scan runs, the map and the list fill in as it goes, under a progress strip with **Pause**, **Resume** and
  **Stop**. F5 (**Rescan**) starts over.

How scans work:

- They run on a few low-priority threads, and belong to the app rather than the window: closing the map leaves the
  scan running.
- Sizes are the space taken on disk (on Linux, the space allocated, not the file lengths). Files with several names
  (hard links) are counted once.
- A scan never follows links, and does not walk into other file systems mounted inside the one it scans.
- Scans are saved (when paused, stopped or finished, every few minutes while running, and when avamux closes) in
  `disk-scans/` in the data folder. A stopped scan carries on from where it stopped, even after a restart. A saved
  scan is only loaded when something looks at it.

From the command line:

```sh
avamux disk scan / --open              # a mount point or any folder; --again starts over
avamux disk scans                      # the scans and how far each got
avamux disk pause <id> && avamux disk resume <id>
avamux disk stop <id>                  # keeps what it found, to resume later
avamux disk forget <id>                # deletes the scan
```
