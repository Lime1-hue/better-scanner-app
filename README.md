# Better Scanner

<img src="app.ico" width="64" alt="Better Scanner icon">

Made by **Emil Paladin** ([Lime1-hue](https://github.com/Lime1-hue)) · paladinemil947@gmail.com

Hands-free scanning for the office's Epson printers. You press **Start** once, walk to the printer and put the pages in the feeder. Each stack is scanned and saved as a PDF in your folder automatically, so you don't have to go back to the PC between stacks.

It replaces the Epson Scan 2 routine (open the app → walk to the printer → back to click Scan → back again for the paper).

## How it works

It doesn't use Epson Scan 2 or any driver. It talks to the printer directly over the network using **eSCL (AirScan)**, which is built into the printers. The scanning engine was ported from an internal document system and turned into a Windows desktop app.

1. **Find printers.** The app sends an mDNS query (`_uscan._tcp`) to discover printers. That still works when DHCP gives a printer a new IP, and the app re-checks automatically if a printer goes quiet.
2. **Hands-free.** Every second the app checks the feeder's paper sensor (`/eSCL/ScannerStatus` → `AdfState`). When paper has sat there for 3 seconds and the printer is idle, it starts a feeder scan by itself, saves the PDF, and waits for the next stack.
   - If the printer is busy (someone is copying), it waits until it's free.
   - If a scan fails (jam), it asks you to take the paper out first, so it doesn't keep re-feeding a stuck sheet. Pages received before the jam are still saved.
   - While it's waiting, the PC is kept from going to sleep.
3. **Page sizing.** The printer scans its whole area and the app trims the empty background in software (auto-crop). It can also fit pages to A4 / Short / Long / Legal, or leave them uncropped.
4. **PDF.** Pages are written straight into a PDF with no extra libraries. Black & white mode produces tiny 1-bit PDFs, about 2 KB a page for text.

## Using it

- **Printer:** pick one from the list. "Find printers" searches again, and "Add by IP…" adds a printer by its IP.
- **Save scans to:** any folder, including a shared network folder. You can also make a new folder for each day.
- **Save as:** one PDF per stack, one PDF per page, or one PDF every N pages. For example, *both sides + every 2 pages* gives one PDF per sheet.
- **Skip blank pages:** handy with both-sided scanning, where the backs are often blank.
- **Scan glass now:** one-click scan from the glass, for bound or fragile documents.
- **Recent scans:** double-click to open. F2 renames, and Delete sends the file to the Recycle Bin.
- Closing the window keeps the app running in the tray. You get a notification and a sound each time a stack is saved.

Settings are stored in `%AppData%\BetterScanner\settings.json`.

## Build

You need the .NET 10 SDK.

```
dotnet run -f net10.0-windows   # run from source
build.bat                       # builds both versions into publish\
```

`build.bat` produces two versions from the same code:

| PC | What to copy | Needs |
|---|---|---|
| Windows 10 / 11 | `publish\BetterScanner.exe` (about 50 MB, one file) | nothing |
| Windows 7 SP1 | both files in `publish\Windows7\` (`BetterScanner.exe` + `BetterScanner.exe.config`, about 150 KB) | [.NET Framework 4.8](https://dotnet.microsoft.com/download/dotnet-framework/net48) |

The two versions work the same way and see each other's printer locks. Differences between the frameworks are handled in `Compat.cs` and a few `#if NETFRAMEWORK` blocks.

**Windows 7 notes**
- If .NET Framework 4.8 is missing, Windows says so when the app starts. Install it and restart the PC.
- The printers only accept HTTPS. Windows 7 needs its updates (TLS 1.2 support) for that. If the app shows the printer as "not reachable" while Windows 10 PCs can see it, run Windows Update on that PC.

## One person per printer

Only one person at a time can run hands-free on a printer. While yours is on, your app tells the other copies on the network, every 2 seconds, that you're using it (UDP broadcast on port 47653, no server needed). On other PCs:

- The printer shows **"in use by Maria"** in the list and under it, as soon as the app is opened. Staff see this at their desk, before walking to the printer.
- The Start button is grayed out, and the panel offers **Use [another printer] instead (free)**, **Tell me when it's free**, and **Ask Maria to release it** (pops up a notification on her PC).
- "Scan glass now" still works, since it can't take paper from the feeder.

The printer is freed when you press Stop, close the app, after the auto-stop time with no paper (10 minutes by default), or within about 7 seconds if your PC crashes or drops off the network. If two people press Start at the same moment, the one who pressed first keeps it and the other is told why.

Each PC must click **Allow** on the Windows Firewall prompt the first time the app runs, or it won't hear coworkers' locks.

## Notes

- Hands-free only works with the **feeder**. The glass has no paper sensor.
- The lock only works between copies of Better Scanner. Epson Scan 2, other scanning apps and people copying at the printer don't know about it. Paper someone puts in the feeder while you're on hands-free goes to your PC, so stop hands-free when you're done (auto-stop does this for you).
- The printers use self-signed HTTPS certificates, so certificate checks are skipped. The app only talks to printers on the local network.

## License

MIT, see [LICENSE](LICENSE).
