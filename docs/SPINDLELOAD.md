# SPINDELLOAD — loads in every spindle (part of BeamCheck since v46)

`SPINDELLOAD` calculates the **maximum load in every spindle** (Spindel / Fußspindel / Kopfspindel / UJB) of the whole drawing
or of a selected part of it, with the same engine as `BEAMLOAD` / `BEAMBATCH`. It was developed as a separate plugin
(V01–V05_yashu, on BeamCheck v43) and is part of `BeamCheck.Acad.dll` since **v46**: one DLL, one `NETLOAD`.

| Command | What it does |
|---|---|
| `SPINDELLOAD` | calculates all spindles, asks about open head spindles, spindle list with review and editing tools, Excel + labels |
| `SPINDELDUMP` | pick a part: PERI properties, article, name, BeamCheck type, weight, whether it counts as **SPINDLE** |
| `SPINDELSETTINGS` | opens the spindle settings |

Ribbon: tab *Berechnungssoftware*, panel *BeamCheck – Spindeln*. AutoCAD-based PERI CAD only (not in the BricsCAD build).

## How the loads are calculated

Every spindle is calculated with **BeamCheck's own engine** (the same code as BEAMLOAD / BEAMBATCH): an invisible short
support is placed under the spindle foot, the spindle is the "standard standing on it", and BeamCheck finds everything the
column carries – upper RCS / SRU beams and the verticals and beams on them (lever rule, uplift setting), consoles, pin ledgers,
decks bearing on ledgers, level factors, γG / γQ. All tolerances, weights, load classes come from `BeamCheck.settings.json`
(`BEAMLOADSETTINGS`) and `BeamCheck.weights.csv`.

**Stack on a head spindle, modelled offset** (BeamCheck v47, also in BEAMLOAD / BEAMBATCH): when the foot (spindle / base plate)
of the next stack stands up to `StackTransferPlanDistance` 600 mm **beside** the top of a column, or up to `StackTransferHeight` 600 mm
**above** it, and not on a beam, deck or pin, its **whole load goes into that column** (the column top must carry nothing else –
a column a beam rests on gives the stack to the beam). Each foot goes to the nearest column top. Every such transfer is listed as a note
(ℹ … *auf dem Kopf … steht …*) and in the Rechenweg (*Versetzt auf dem Kopf*). Switch: `StackTransferEnabled` in `BeamCheck.settings.json`.

**Supports from the building model** (BeamCheck v48, also in BEAMLOAD / BEAMBATCH): everything on the layer `BuildingSupportLayerPattern`
(default `BUILDING STRUCTURE…`) – steel beams, foundations, pedestals imported from Navisworks as blocks – is a **building support**, never a
scaffold part (no weight). Its real plan outline is taken from its faces (smallest rectangle, also for rotated steel). A scaffold beam bears on it
when the beam crosses the outline (±50 mm `BuildingSupportPlanTolerance` + half the beam width) and its top is 0–300 mm (`BuildingSupportHeight`)
below the underside of the beam – the mesh grating in between carries nothing. Support point: the **outer supports** of the beam at the **inner edge**
(simply supported), supports **between** them at the **middle** of the flange / foundation (continuous beam). The share taken there goes into the
building; spindles under the same beam get only their own lever share. Note: *Träger … liegt auch auf dem Bauwerk*; Rechenweg: *= Bauwerk …*.

On top of BeamCheck:
* **Head spindles** (nothing found standing on them, also not by the transfer above) are flagged; you tell the program what stands on them. The linked spindle /
  vertical is calculated with BeamCheck at its own foot and its **whole load** goes into the head spindle **and every spindle below it**.
* **Added parts** (window) are given to BeamCheck like "Objekte hinzufügen" in BEAMLOAD – so they are distributed by BeamCheck, not just added as weight.
* **Removed parts** (window) are excluded from every spindle's calculation.
* **Manual loads** (kN) go into the spindle and every spindle below it.
* Check value: *model weight* vs. *dead load on the bottom spindles* (< 100 % = some load reaches no spindle, > 100 % = conservative uplift / double paths).

## SPINDELLOAD – the workflow

1. Load class → `Calculate [All/Select/Pick]` (*Select*: window only the part of a big model you need) → reads the drawing → calculates all spindles → summary.
   **Pick**: pick one or more parts – a spindle, UVA, UVR, UVH, UH-Zapfen, base plate … any vertical part, at any height. Each is calculated like a
   spindle: **everything standing on it** is collected (its column up to the top, beams and consoles on it, stacks on its head). A part that is not
   a standard (UH-Zapfen, base plate, nut) stands for the stack standing on it. Only the objects within `PickReadRadius` (15 m) of the picked parts
   are read, so it is quick in big models. The command line shows *PICKED S… Gk Qk Fk Fd*, and the list shows only the picked parts; open head
   spindles **above** them are asked for, and listed with them (*Open head spindle(s) above it*).
   A **progress window** shows the step (reading / calculating / linked columns), a progress bar, *x of y*, time of this step, **remaining time** and total time, with a **Cancel** button
   (cancel while calculating: the spindles done so far are kept, the rest are marked *Not calculated*). The times of each step are written to the command line.
   Calculation runs on **all CPU cores**; each spindle gets only the parts around its column (`CandidateRadius`), grown along beams.
   Besides the spindles, every part matching `PartRowPattern` (default **UVA** and **UH-Zapfen / UHA-Zapfen**, articles 136779, 130681, 130684)
   gets a **row of its own** (type *part*) with everything standing on it.
   Temporary markers in the view: **red** = open head spindle / maximum, **yellow** = BeamCheck warning (not saved in the drawing).
2. *Answer the open head spindles one by one?* For each one the program **isolates a work zone** around it (only that area is visible) and asks
   `[Click/Window/Nothing/Manual/Skip/Stop]`:
   * **Click** – just click *near* the spindle / vertical standing on it (no exact pick needed: the nearest one on screen is offered, `Next` cycles through candidates, `Accept`).
   * **Window** – drag a window over it; the lowest spindle / vertical in the window is taken.
   * **Nothing** – nothing more stands on it.  **Manual** – type a load.  **Skip** / **Stop**.
3. **The spindle list** (window): all spindles, sortable by any column (default: highest Fd first), *Only spindles to check* filter,
   colours red / yellow / green, and buttons:

| Button | What it does |
|---|---|
| Show in model (work zone) | isolates the area around the spindle, zooms, highlights everything that loads it; then `Orbit/Wider/Smaller/All/Back` |
| Calculation (Rechenweg) | load list + BeamCheck's step-by-step calculation in a text window (copyable) |
| Next spindle to check ▶ | jumps to the next red / yellow row |
| What stands on it? (click) | link as in step 2 |
| Nothing stands on it | answers an open head spindle |
| Add missing parts (window) | work zone isolated, already counted parts highlighted → drag a window over what is missing → list + confirm |
| Remove wrong parts (window) | same, the parts are removed from the whole calculation |
| Manual load… | dead / service kN |
| Undo my changes on it / Undo last removal | take changes back |
| Mark checked / unchecked | your review status (green) |
| Excel + labels, close | `<drawing>_SPINDLELOAD.xlsx` (Spindles, Summary, Removed parts) and labels on layer `SPINDLELOAD` |

   Double-click a row = *Show in model*. After every change only the spindles around it are recalculated (< 1 s).
4. All your answers, links, added / removed parts and manual loads are saved in `<drawing>.spindleload.json` next to the DWG
   (save the DWG first) and are used again the next time.

`SPINDELDUMP` – pick any part: all PERI properties, article, name, BeamCheck type, weight, whether it counts as **SPINDLE**.
`SPINDELSETTINGS` – opens `%APPDATA%\BeamCheck\BeamCheck.Spindle.settings.json` (created from the copy next to the DLL):
`SpindlePattern`, `SupportEndPartPattern`, `VirtualSupportSize` 200, `HeadTopTolerance` 300, work zone `ZoneRadius` 3000 / `ZoneBelow` 1000 / `ZoneAbove` 4000,
`PickTolerance` 600 (click-near distance), `MarkerRadius` 350, label settings,
`CandidateRadius` 3500 / `CandidateBelow` 600 (parts given to BeamCheck per spindle: plan radius around the spindle, grown along every beam / console above it; `0` = BeamCheck's `SearchRadius` like BEAMBATCH – much slower),
`MaxThreads` 0 (= all CPU cores),
`MaxObjectSize` 150000 mm (objects / parts bigger than 150 m are ignored), `MaxBlockEntities` 20000 (blocks with more objects – imported buildings, site models – are ignored; xrefs and XLINE / RAY are always ignored),
`SkipLayersPattern` "" (regex, e.g. `(?i)gebäude|building|arch|bestand` – objects on these layers are not read at all: faster).
Everything ignored is listed on the command line after reading; objects that take more than 2 s to read are listed too.
The progress window shows the object being read right now (*Now: …*).
If you use bays longer than ~3 m or very wide consoles and think a load is missing, raise `CandidateRadius`.

**Limits:** BeamCheck's geometric load model (no stiffness / wind / horizontal loads). A checking aid – results must be verified by a qualified engineer.


## Code

| File | Role |
|---|---|
| `src/BeamCheck.Core/Spindles/SpindleEngine.cs` | every spindle calculated with `BeamLoadSession` on an invisible support under its foot; candidate box grown along beams; column chain; links; added / removed parts; manual loads; parallel, cancellable |
| `src/BeamCheck.Core/Spindles/SpindleModel.cs` | spindle / part / result model, `Sidecar` (`<drawing>.spindleload.json`) |
| `src/BeamCheck.Core/Spindles/SpindleSettings.cs` | `BeamCheck.Spindle.settings.json` |
| `src/BeamCheck.Cad.Shared/SpindleScanner.cs` | reads the drawing with `CadElementReader` (spindle hooks `SpindleRx` / `ComponentRx`, unset for BEAMLOAD) and the safety filters |
| `src/BeamCheck.Cad.Shared/SpindleCommands.cs` | the commands and the workflow (work zone, click near, window, markers, Rechenweg, Excel, labels) |
| `src/BeamCheck.Cad.Shared/UI/SpindleForms.cs` | spindle list, text window, progress window |
| `tests/BeamCheck.Core.Tests/SpindleEngineTests.cs` | engine tests without AutoCAD |

## History

### v49 (2026-10-21) — a support standard through the beam is no load on it
* A standard that comes up **through** the beam from below (its foot more than 200 mm under the beam's underside – the support under the
  beam) is no longer counted as a stack standing on the beam. Before, when its body reached a little above the beam top, the deck level
  hanging on it **under** the beam appeared as a load on the beam at that support (B-005 in "Beam check Feed_V1": C = 7,23 kN at S2).
  BEAMLOAD, BEAMBATCH and SPINDELLOAD (a head spindle inserted into the standard below no longer gets that standard's load).

### v48 (2026-10-21) — building supports, UVA / UH-Zapfen rows
* **Building model as supports** (BEAMLOAD, BEAMBATCH, SPINDELLOAD): objects on the layer `BUILDING STRUCTURE…` (`BuildingSupportLayerPattern`) are
  supports – steel beams, foundations, pedestals; 0–300 mm under the scaffold beam (mesh between), ±50 mm in plan; outer supports at the inner edge,
  intermediate ones at the middle. They are no longer read as decks / beams (a pedestal was read as a 40 t deck before).
* BEAMBATCH: a beam lying only on the building gets those supports (no more "festem Untergrund" for it).
* **SPINDELLOAD All / Select**: UVA and UH-Zapfen get rows of their own (`PartRowPattern` in the spindle settings).
* Labels, list and Excel show the **article and name** (`S12 UJB 38-50/30: 23,4 kN`).
* PERIDUMP says when an object is read as a building support.

### v47 (2026-10-21) — stacks on head spindles, pick any vertical
* **Stack on a head spindle modelled offset** (up to 600 mm beside the head, or its foot up to 600 mm above it): its whole load goes into the head
  spindle and down – in BEAMLOAD, BEAMBATCH and SPINDELLOAD. Settings `StackTransferEnabled`, `StackTransferPlanDistance`, `StackTransferHeight`
  (BeamCheck.settings.json, the settings version stays 24: your settings are kept). The foot spindle of such a stack is no longer counted as a bottom spindle.
* **BEAMLOAD asks** for every head spindle in the load path with nothing found on it: *Was steht darauf? [Wählen/Nichts/Keine weiteren]* – pick the
  spindle / standard standing on it, its whole load goes into that column. Dialog button **Was steht auf dem Kopf?…** asks again (or lets you pick
  a head and the stack on it). Head spindles: `HeadPartPattern`.
* **SPINDELLOAD Pick**: pick any vertical part (spindle, UVA, UVR, UVH, UH-Zapfen …) and get the load of everything standing on it. Setting `PickReadRadius`.
* Changes on a spindle of the offset stack (manual loads, links, added parts) also count in the head spindle below.

### v46 (2026-10-20) — SPINDELLOAD in BeamCheck
* SPINDELLOAD, SPINDELDUMP and SPINDELSETTINGS are commands of `BeamCheck.Acad.dll` (no separate `Vxx_yashu.Spindle.dll`, no loader LISP).
* Calculated with the BeamCheck v46 engine (load model unchanged since v43; v44/v45 added the ribbon and the beam proofs).
* The spindle recognition is part of BeamCheck's own `CadElementReader` (no decompiled copy any more). BEAMLOAD / BEAMBATCH do not use it: their results are unchanged.
* Spindle settings: `%APPDATA%\BeamCheck\BeamCheck.Spindle.settings.json` (next to `BeamCheck.settings.json`), so it also works when BeamCheck is installed from a read-only network share.
  A spindle settings file of V05 or older is not taken over (new file name); your answers in `<drawing>.spindleload.json` are.
* Ribbon panel *BeamCheck – Spindeln* with the three commands.

### V05_yashu (2026-10-19)
* **Fix: SPINDELLOAD could freeze at the end of "Reading the drawing"** (seen at 34 980 / 35 070). Causes guarded:
  construction lines (XLINE / RAY, infinite size) and huge objects are no longer read; parts with impossible coordinates are dropped;
  the spatial index can no longer loop over an "infinite" area.
* Ignored before reading: XLINE / RAY, xrefs, blocks with more than `MaxBlockEntities` objects, objects larger than `MaxObjectSize`, layers matching `SkipLayersPattern`.
  All ignored objects and all slow objects (> 2 s) are listed on the command line.
* Progress window shows the object being read (*Now: …*) and "Preparing the calculation" between reading and calculating.

### V04_yashu (2026-10-19)
* **Progress window** for reading and calculating: step, bar, x of y, elapsed / remaining / total time, **Cancel** (partial results kept). Also for recalculations after a change.
* **Much faster**: each spindle calculated once (V03 often calculated base spindles twice), calculation on all CPU cores, and BeamCheck gets only the parts
  around the spindle's column (`CandidateRadius` 3500 mm, grown along beams; `CandidateBelow` 600 mm) instead of everything within 5 m at every height.
  Test model (961 spindles, 15 600 parts): V03 6.4 s -> V04 1.0 s on 2 cores; tall model (15 lifts): about 6x less work per spindle. Results identical in all tests.
* `Calculate [All/Select]`: calculate only a selected area of a big model.
* Step times written to the command line (to see where time goes).

### V03_yashu (2026-10-19)
* Loads now calculated with **BeamCheck v43's own engine** for every spindle (invisible support under the spindle foot):
  upper beams, consoles, pin ledgers, decks on ledgers, level factors, gammas exactly as BEAMLOAD / BEAMBATCH.
* Links: the linked spindle / vertical is calculated at its own foot with BeamCheck; its whole load goes into the head spindle and down.
* Added parts are given to BeamCheck ("Objekte hinzufügen"); removed parts excluded everywhere; manual loads go down the column.
* **Easy editing in big models**: spindle list window (sortable, problems first, colours, double-click), automatic **work zone isolation**,
  **click near** a part (no exact pick), **window** add / remove with automatic filtering and confirmation, temporary **markers**,
  undo of the last removal, BeamCheck **Rechenweg** per spindle, Excel output (BeamCheck's Excel writer).
* Only nearby spindles are recalculated after a change.

### V02_yashu (2026-10-19)
* New **load-path model**: vertical stacks, beams/consoles on verticals carrying further verticals and beams (lever rule), beam-on-beam,
  spindle on platform directly above another spindle (vertical load connected), platform spread (assumed), any depth.
* **Head spindles with nothing on top** are listed and shown one by one: *Pick* the spindle / vertical / beam standing on it, *Manual* load, or *None*.
* New review option **Link** (pick / clear what stands on a spindle). Manual loads now travel down to the base spindle.
* **Exclude** now removes a part from the whole calculation; every change recalculates.
* Summary / CSV list loads that reach no spindle, floating stacks, and all assumptions; review shows the path of every load.
* Files renamed to `V02_yashu.*`; new spindle settings (stack / transfer / platform / beam tolerances).

### V01_yashu
* BeamCheck v43 + first SPINDELLOAD (nearest-column distribution), PERI objects read with BeamCheck's reader, NETLOAD loader.
