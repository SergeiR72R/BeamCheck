# BeamCheck über eine Netzwerkfreigabe verteilen

**Einmalig (Administrator der Freigabe):**
1. `.\deploy\build.ps1` — baut `dist\`.
2. `.\deploy\publish.ps1 -Share "\\server\freigabe\BeamCheck"` — kopiert das Paket und schreibt `VERSION`.
3. Kollegen Leserechte auf die Freigabe geben.

**Kollegen:** `\\server\freigabe\BeamCheck\install.cmd` doppelklicken. Fertig — kein Admin nötig.
- Das Modul wird für den Benutzer installiert und beim Start von PERI CAD automatisch geladen.
- Im Ribbon erscheint die Registerkarte **Berechnungssoftware** (Panel „BeamCheck – Träger").

**Neue Version:** `build.ps1` + `publish.ps1` erneut ausführen; die Kollegen führen `install.cmd` auf der Freigabe noch einmal aus (PERI CAD vorher schließen).
**Protokoll:** `%APPDATA%\BeamCheck\install.log`.
