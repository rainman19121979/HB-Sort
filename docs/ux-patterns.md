# HB-Sort UX-Pattern-Katalog

**Status:** verbindliche Referenz fuer neue UI-Arbeit (Dialoge, Views, Overlays).
**Erstellt:** 2026-05-29 (ux-analyst).
**Quelle:** Code-Realitaet (Primaerquelle), `CLAUDE.md` "Konventionen (aktiv)"
(Sekundaerquelle — Kurzfassung dessen, was hier ausfuehrlich steht).

## Zweck

`CLAUDE.md` listet die UI-Konventionen in Stichpunkten. Dieses Dokument ist der
ausfuehrliche Katalog: pro Pattern eine **Regel**, ein knappes **Beispiel**, eine
**Vorbild-Datei** zum Abschauen und — wo der Code von der Regel abweicht — eine
**Drift-Inventur** mit Verweis auf das Backlog-Item, das den Drift fixt.

Verbindlich heisst: neue Dialoge orientieren sich am hier benannten Vorbild,
nicht an einem zufaellig gewaehlten Nachbar-Dialog. Wo unten ein Drift steht, ist
die als **richtig** markierte Variante das Ziel — nicht der Ist-Zustand.

Dieses Dokument war die Arbeitsgrundlage fuer den **v0.1.25-Kosmetik-Sweep**
(Wellen 1-3, Backlog-Items B1/B2/B6/B10 + UX-1..UX-4/UX-7). Die Drift-Inventur
am Ende (Abschnitt 12) listet alle damaligen Drifts — inzwischen alle behoben;
sie dient kuenftig als Vorlage fuer neue UX-Checks.

---

## 1. Dialog-Footer (kritischstes Pattern — hier entstehen die Drifts)

### Regel

- **Layout:** links Abbrechen/Schliessen (`IsCancel="True"`), rechts der primaere
  Button (`IsDefault="True"` + `AccentButtonStyle`). Bei `HorizontalAlignment="Right"`
  + `StackPanel` heisst "links/rechts" die Lese-Reihenfolge: Sekundaer-Button
  zuerst im Markup (steht damit weiter links), Primaer-Button zuletzt. Bei einem
  Zwei-Spalten-`Grid` gehoert der Sekundaer-Button in Spalte 0 (links), der
  Primaer-Button in die rechte Spalte.
- **`AccentButtonStyle` NUR fuer primaere Aktionen:** Speichern, Anlegen,
  Exportieren, Uebernehmen, Bestaetigen, Verifizieren. Niemals fuer
  Schliessen/Abbrechen.
- **Schliessen/Abbrechen ohne Style** (= grauer Standard-Button).
- **Destruktive Aktionen** (Loeschen, Freigeben): `Background="#D32F2F"
  Foreground="White"`, kein `AccentButtonStyle`. Noch kein zentraler
  `DangerButtonStyle` vorhanden — Background-Hex inline ist der aktuelle Stand
  (offenes TODO `DangerButtonStyle`, in `CLAUDE.md` erwaehnt).
- **Wortwahl:** "Abbrechen" fuer Editier-/Eingabe-Dialoge, "Schliessen" fuer
  Read-Only-Dialoge.
- **Read-Only-Dialoge:** nur `IsCancel="True"` am Schliessen-Button, **kein**
  `IsDefault="True"` (ENTER und ESC duerfen nicht denselben Button ausloesen).
- **Editier-Dialoge:** Primaer-Button bekommt `IsDefault="True"`, Abbrechen
  `IsCancel="True"`.

### Beispiel (korrekter Editier-Dialog-Footer)

```xml
<StackPanel Grid.Row="5" Orientation="Horizontal"
            HorizontalAlignment="Right" Margin="0,12,0,0">
    <Button Content="Abbrechen" IsCancel="True"
            Width="120" Margin="0,0,8,0" Click="Cancel_Click"/>
    <Button Content="Exportieren" Width="120"
            Style="{StaticResource AccentButtonStyle}"
            IsDefault="True" Click="Export_Click"/>
</StackPanel>
```

### Vorbild-Dateien

- **Editier-/Aktions-Dialog:** `HBSort/Views/BsxExportDialog.xaml`
  (Abbrechen links/grau, "Exportieren" rechts mit `AccentButtonStyle` +
  `IsDefault`). Ebenfalls korrekt: `BinCreateDialog.xaml`,
  `BinBulkCreateDialog.xaml`, `WantedListExportDialog.xaml`,
  `BuildSuggestionDetailDialog.xaml`, `DismantleWizardDialog.xaml`.
- **Destruktiver Button:** `HBSort/Views/MinifigSummaryDialog.xaml:182-185`
  (Loeschen mit `#D32F2F`/White) — der Footer drumherum driftet allerdings,
  siehe unten.

### Aktuelle Drifts

| Dialog | Problem | Soll | Backlog |
|---|---|---|---|
| `MinifigSummaryDialog.xaml:199-201` | "Schliessen" hat `AccentButtonStyle`; alle Buttons rechts (kein links/rechts-Split) | Schliessen grau, ohne Style | **UX-1** |
| `BinDetailDialog.xaml:157-159` | "Schliessen" (Read-Only) hat `AccentButtonStyle` | Schliessen grau | UX-1 (gleicher Sweep) |
| `FloatingPartDetailDialog.xaml:109-110` | "Schliessen" (Read-Only) hat `AccentButtonStyle` | Schliessen grau | UX-1 (gleicher Sweep) |
| `ManageIgnoredDialog.xaml:86-90` | "Schliessen" hat `AccentButtonStyle` **und** `IsDefault`+`IsCancel` (Read-Only-Verletzung) | Schliessen grau, nur `IsCancel` | **B2** |
| `MassUpdateExportDialog.xaml:87-92` | "Schliessen" steht ganz rechts (Spalte 4) hinter dem Primaer-Button "Verifizieren" — Reihenfolge invertiert | Schliessen links, primaer rechts | **B2** |
| `BlReserveDialog.xaml:120-124` | "Abbrechen" steht rechts (einziger Button, in Grid-Spalte 1) statt links | Abbrechen links | **UX-2** |

**Korrekte Footer (Referenz, nicht anfassen):** `BsxExportDialog`,
`WantedListExportDialog`, `BinCreateDialog`, `BinBulkCreateDialog`,
`BuildSuggestionDetailDialog`, `DismantleWizardDialog`.

> Hinweis zur `CLAUDE.md`-Sekundaerquelle: dort ist genau dieses Layout
> beschrieben. Die Drifts sind also reine Umsetzungs-Abweichungen, kein
> Konventions-Streit. Welle 3 zieht sie gerade.

---

## 2. Dialog-VM-Pattern (DI + DialogResult)

### Regel

- **Dialog-VMs als `AddTransient`** in `App.xaml.cs::ConfigureServices`
  registrieren (pro Aufruf frische Instanz, kein geteilter Zustand).
- **Aufloesung im Code-Behind** via `App.Services.GetRequiredService<T>()`.
  Das ist der **haeufigere** Stil (etabliert in Welle 2 / Backlog B1 —
  `ManageIgnoredViewModel`, `MassUpdateExportViewModel`).
- Daneben existiert die **Constructor-Injection-Variante**: der Dialog selbst
  ist in DI (`AddTransient<Views.BinCreateDialog>()`), der Aufrufer holt das
  ganze Dialog-Objekt (`SettingsWindow.xaml.cs:149`). Beide Varianten sind
  legitim; `GetRequiredService`-im-Code-Behind ueberwiegt.
- **`DialogResult`:** `true` nur bei echtem Erfolg (Speichern/Anlegen/Export
  durchgefuehrt). Schliessen ohne Aktion → `false` oder `null`.

### Beispiel

```csharp
// App.xaml.cs::ConfigureServices
services.AddTransient<ViewModels.ManageIgnoredViewModel>();
services.AddTransient<ViewModels.MassUpdateExportViewModel>();

// View-Code-Behind
var vm = App.Services.GetRequiredService<MassUpdateExportViewModel>();
```

### Vorbild-Dateien

- **DialogResult sauber:** `HBSort/Views/BinDetailDialog.xaml.cs` (true nur bei
  echter Aktion). Seit Welle 2 auch `MassUpdateExportDialog` /
  `ManageIgnoredDialog` als Ziel.
- **DI-Registrierung:** `HBSort/App.xaml.cs:427-428`.

### Aktuelle Drifts

| Stelle | Problem | Backlog |
|---|---|---|
| `BuildSuggestionsView.xaml.cs:32`, `BlInventoryView.xaml.cs:35` | `ManageIgnoredViewModel` / `MassUpdateExportViewModel` wurden urspruenglich per `new` erzeugt; Migration auf `AddTransient` + `GetRequiredService` | **B1** (in Welle 2 weitgehend gezogen) |
| `MassUpdateExportDialog.xaml.cs` | `Close_Click` setzte `DialogResult=true` statt `false`/`null` | **B6/B10** |

---

## 3. SortInstruction-Modal (Take/Put/Plus)

### Regel

- **Einziger Post-Save-Sortier-Pfad** seit v0.1.24-beta.1. Wird von **allen 6
  aktiven Sortier-Triggern** plus der **V5-Reservierungs-Aufloesung** genutzt:
  Bulk-Move, DismantleWizard, Single-Move, StoreFloating, Reverse-Match-Konsum,
  Wizard-Stufe-2-Save (+ V5 Shop-Return / Shop-Exchange).
- **Aufbau:** `SortInstruction` mit `Take` (Liste von `SortSection`, pro Quell-Bin
  eine), `Put` (Liste, pro Ziel-Bin eine, mind. ein Eintrag), optionalem
  `PlusHint` und `HeaderText` (Default "Operation erfolgreich").
  `Take` darf leer sein (z.B. StoreFloating — Teil ist in der Hand).
- **Modal wird aktiv weggeklickt**, kein Auto-Dismiss. Leeres
  Take+Put+PlusHint → Modal gar nicht zeigen (defensive No-Op).
- **Aufruf entkoppelt** ueber `ISortInstructionPresenter.Show(...)` — Aufrufer
  muessen die Window-Owner-Hierarchie nicht kennen (Wurzel-Fix gegen den
  "kein Popup aus Lagertab-Pfad"-Bug).
- **Performance-Pattern (Pflicht):** das Setzen der Modal-Properties laeuft via
  `Dispatcher.InvokeAsync(..., DispatcherPriority.Render)`, damit der UI-Thread
  freie Render-Slots hat, bevor das Modal zeichnet. Sonst wirkt der Modal-Open
  traege, weil vorgelagerte `DataChanged`-Subscriber den Thread belasten.
  Test-Pfad (ohne `Application.Current`) faellt auf synchrone Aktualisierung
  zurueck.
- **DTO niemals manuell zusammenstueckeln**, wenn ein Helper passt: die
  wiederkehrenden Bausteine (Take-Sektionen aus konsumierten FloatingParts,
  BL-Shop-Take, Figur-Put, V5-Return/Exchange) liegen in
  `SortInstructionBuilder`.

### Vorbild-Dateien

- DTO: `HBSort/ViewModels/SortInstruction.cs`
- Builder-Helper: `HBSort/ViewModels/SortInstructionBuilder.cs`
- Presenter: `HBSort/Services/ISortInstructionPresenter.cs` +
  `HBSort/Services/SortInstructionPresenter.cs`
- Render-Pattern: `HBSort/ViewModels/BinInstructionViewModel.cs:159-185`
  (`ShowSortInstruction`)
- Aufrufer-Beispiele: `MinifigSummaryDialog.xaml.cs:151`,
  `DismantleWizardDialog.xaml.cs:162`, `CollectMinifigWizardDialog.xaml.cs:104`,
  `BuildSuggestionDetailDialog.xaml.cs:196`

> Legacy-Hinweis: `BinInstructionMode.Single` + `.Group` sind Altmodi, die
> abgeraeumt werden (v0.1.25 Cleanup OPEN-18). Neue Trigger immer ueber
> `SortInstruction` / `ShowSortInstruction`, nie ueber Single/Group.

---

## 4. BL-Shop-Badge

### Regel

- **Blauer Badge** `Background="#0277BD"`, weisse `SemiBold`-Schrift, `FontSize="10"`,
  `CornerRadius="3"`, `Padding="6,2"`, `Cursor="Hand"`. Sichtbar nur wenn das Teil
  intern noch fehlt **und** das BL-Inventar es anbietet
  (`Visibility` via `ShowBlBadge`).
- **Klick** (`MouseLeftButtonUp="BlBadge_Click"`, `Tag="{Binding}"`) oeffnet den
  Reserve-/Lot-Picker.
- **Beschriftung — Progressive Disclosure (Zwei-Stufen-Pattern):**
  Der Badge selbst ist bewusst knapp und zeigt nur die *Anwesenheit* im
  Shop (`BlAvailability.BadgeText`, liefert by-design `"BL-Shop"`). Die
  *Mengen-/Zustands-Details* tragen den **Tooltip** (`BadgeTooltip`,
  z.B. "Im BL-Shop verfuegbar: 3x Neu, 2x Gebraucht. Klicken um zu
  reservieren."). Stufe 1 = Badge (gibt es das im Shop?), Stufe 2 =
  Hover/Klick (wie viel, welcher Zustand). Das hält den Badge in den
  engen Teile-Zeilen kompakt, ohne Info zu verlieren.
- **Konsistenz-Regel:** alle Dialoge binden den Badge-Text auf
  `BlAvailability.BadgeText` (nicht hartkodiert) und den Tooltip auf
  `BlAvailability.BadgeTooltip`. So wirkt eine künftige Änderung an
  `BadgeText`/`BadgeTooltip` (`BlAvailabilityInfo` in
  `MinifigSummaryViewModel.cs`) automatisch in allen Dialogen gleich.
- Vorkommen: `MinifigSummaryDialog`, `BuildSuggestionDetailDialog`,
  `PartLookupView` (Reservierungs-Badge).

### Beispiel (richtige Variante)

```xml
<Border Margin="8,0,0,0" Background="#0277BD" CornerRadius="3" Padding="6,2"
        Cursor="Hand" Tag="{Binding}" MouseLeftButtonUp="BlBadge_Click"
        ToolTip="{Binding BlAvailability.BadgeTooltip}"
        Visibility="{Binding ShowBlBadge, Converter={StaticResource BoolToVisibility}}">
    <TextBlock Text="{Binding BlAvailability.BadgeText}"
               Foreground="White" FontWeight="SemiBold" FontSize="10"/>
</Border>
```

### Vorbild-Datei

`HBSort/Views/MinifigSummaryDialog.xaml:139-150` (bindet `BadgeText` +
`BadgeTooltip`).

### Drift behoben (UX-4, v0.1.25 Welle 3)

`BuildSuggestionDetailDialog.xaml:166` hatte den Badge-Text hartkodiert
(`Text="BL-Shop"`) statt auf `BlAvailability.BadgeText` zu binden —
obwohl der Dialog dasselbe `BlAvailability`-Objekt bereits an anderer
Stelle band. Behoben durch reinen Binding-Tausch: jetzt überall
`Text="{Binding BlAvailability.BadgeText}"`. Damit ist die Binding-
Konsistenz hergestellt (alle Dialoge greifen auf dieselbe Property zu).

**Hinweis:** `BadgeText` liefert aktuell by-design `"BL-Shop"` (die
Mengen-Info steckt im Tooltip, siehe Progressive-Disclosure-Regel oben).
Falls der Badge künftig informativer werden soll, ist das eine reine
VM-Änderung an `BlAvailabilityInfo.BadgeText` — die XAML-Bindings müssen
dann nicht mehr angefasst werden.

---

## 5. Reserve-Flow + V5-Aufloesung

### Regel

- **Reserve-Dialog** (`BlReserveDialog`): Lot-Liste; Klick auf ein Lot
  reserviert die Menge atomar (Reserve + Part-Update + ScanEvent ueber
  `ReserveForPartAsync`). In `BuildSuggestionDetailDialog` zusaetzlich eine
  **Quellen-Auswahl pro Teil** (HBSort-Lager vs. BL-Shop).
- **V5-Aufloesung** (`PartLookupService.AssignPartToMinifigAsync`): ein
  physisch gescanntes Teil ersetzt eine bestehende BL-Reservierung, wenn
  `(Collected+1) + Reserved > Needed && Reserved > 0`. `EffectiveCollected`
  bleibt invariant — nur die Quelle wechselt (BL-reserviert → physisch).
  Die juengste offene Reservierung (LIFO) wird via
  `ReleaseSingleReservationAsync` aufgeloest.
- **Anweisung je nach Lot-Condition** (immer als SortInstruction-Modal,
  nie still):
  - **U-Lot (Gebraucht) → Return** (`SortInstructionBuilder.BuildV5ShopReturn`):
    eine Put-Sektion "BL-Shop: {Remarks}" — das gescannte Teil fuellt den Shop
    wieder auf, das Reservierte bleibt bei der Figur. Keine Take-Sektion.
  - **N-Lot (Neu) → Exchange** (`SortInstructionBuilder.BuildV5ShopExchange`):
    Tausch — Take das neue Teil aus dem Figur-Fach, Put zurueck in den Shop,
    Put das gescannte Teil in das Figur-Fach.

### Vorbild-Dateien

- Reserve-Dialog: `HBSort/Views/BlReserveDialog.xaml` (Footer driftet, s. UX-2)
- V5-Builder: `HBSort/ViewModels/SortInstructionBuilder.cs:131-243`
  (`BuildV5ShopReturn` / `BuildV5ShopExchange`)
- Aufloesungs-Logik: `PartLookupService.AssignPartToMinifigAsync`
- BL-Shop-Take in normalem Anlege-Flow:
  `SortInstructionBuilder.AddBlShopTakeSections` (Z.101)

---

## 6. Strict-Mode-Feedback (erklaeren statt nur ablehnen)

### Regel

Wenn der Service eine Aktion wegen Bin-Typ-Verletzung ablehnt
(`InvalidBinKindException` aus dem `BinKindGuard`), bekommt der User eine
**klare Erklaerung**, nicht nur eine kommentarlose Ablehnung. Die Exception
traegt die noetigen Felder fuer eine sprechende Meldung:
`TargetBinKind` (lesbar via `StorageBinKindExtensions.ToDisplayName`),
`TargetBinLabel`, `AttemptedAction`. Das Prinzip gilt analog fuer
Volle-Faecher-Faelle (→ Pattern 7).

**Reverse-Match-Bypass:** FloatingPart → Waiting-Bin ist erlaubt, wenn das Teil
zu einer der wartenden Figuren im Bin passt. Aufrufer ohne diesen Bypass muessen
den Fehler an die UI weiterreichen (nicht verschlucken).

### Vorbild-Dateien

- Exception + lesbare Bin-Typ-Namen:
  `HBSort.Core/Services/InvalidBinKindException.cs` (`ToDisplayName` ab Z.48)
- Guard: `BinKindGuard` (gleiche Datei, ab Z.58)

---

## 7. Volle-Faecher-Banner (aus v0.1.22)

### Regel

Wenn der Bin-Vorschlags-Service kein passendes Fach findet ("Kein Fach frei"),
zeigt die UI ein **Banner mit Direkt-Link in die Lagerfach-Verwaltung** — der
User soll nicht raten muessen, wo er Platz schafft. Pro Teil bzw. pro
Vorschlag eine sichtbare Zeile, kein stilles Default-Verhalten.

### Vorbild-Dateien

- `HBSort/Views/MinifigDetailView.xaml:255-266` (Warnung + Button
  "Lagerfach-Verwaltung oeffnen")
- `HBSort/Views/BuildSuggestionDetailDialog.xaml:69-78` (Volle-Faecher-Warnung
  + "Lagerfach-Verwaltung oeffnen")
- `HBSort/Views/PartLookupView.xaml:326` (Button "Lagerfach-Verwaltung")
- `HBSort/Views/CollectMinifigWizardDialog.xaml:317` (Volle-Faecher-Banner,
  eigene Banner-Zeile)
- Pro-Teil-Variante: `HBSort/Views/DismantleWizardDialog.xaml:104-108`
  ("Kein Fach frei - manuell waehlen")

---

## 8. Pending-vs-Detail-Abgrenzung (bewusst unterschiedliche Layouts)

### Regel

Die **Pending-Sicht** (`MinifigDetailView` — die gerade erkannte, noch **nicht
persistierte** Figur im Scan-Tab) hat **bewusst** ein anderes Layout als die
**Detail-Dialoge** (z.B. `MinifigSummaryDialog`, nach Persist). Pending zeigt
Header + Teileliste + Footer mit Lagerfach-Auswahl + Verwerfen; die
Detail-Dialoge zeigen die persistierte Figur mit Loeschen/Zerlegen/Verschieben.

**Dieser Unterschied ist Absicht und darf nicht versehentlich nivelliert
werden.** Bei kuenftigen UX-Iterationen, die "Dialog-Optik vereinheitlichen",
ist die Pending-Sicht ausdruecklich ausgenommen — sie gehoert in einen anderen
Lebenszyklus-Zustand (vor Persist) als die Detail-Dialoge (nach Persist).

### Vorbild-Dateien

- Pending-Sicht: `HBSort/Views/MinifigDetailView.xaml` (Kommentar Z.5-9
  beschreibt den Pending-Zweck)
- Detail-Dialog (Referenz fuer persistierte Figuren):
  `HBSort/Views/MinifigSummaryDialog.xaml`

---

## 9. Klickbare Bilder

### Regel

Jedes `<Image>` mit einem BL-/Cache-Bild bekommt
`b:ImageZoom.IsEnabled="True"`. Namespace im Wurzel-Element:
`xmlns:b="clr-namespace:HBSort.Behaviors"`.

### Beispiel

```xml
<UserControl ... xmlns:b="clr-namespace:HBSort.Behaviors">
    <Image Source="{Binding ImageUrl}" b:ImageZoom.IsEnabled="True"/>
</UserControl>
```

### Vorbild-Dateien

Durchgaengig gelebt — 28 Vorkommen in 18 Dateien, u.a.
`CollectMinifigWizardDialog.xaml` (3x), `PartLookupView.xaml` (3x),
`MinifigSummaryDialog.xaml`, `MinifigDetailView.xaml`,
`BuildSuggestionDetailDialog.xaml`. Kein Drift bekannt.

---

## 10. Tooltips

### Regel

- **Du-Form, Verb-Anfang, max. ~80 Zeichen, Punkt am Ende.**
  Beispiel: "Kopiert den XML-Code in die Zwischenablage."
- **Globaler Schalter** `AppSettings.ShowTooltips` via `ITooltipsService`
  (`ApplyAsync()` beim Start, `HBSort/App.xaml.cs:118`). Tooltips nicht hart
  erzwingen, sondern ueber den Service-Schalter steuerbar lassen.

### Vorbild-Dateien

`HBSort/Views/WantedListExportDialog.xaml:101,107`,
`HBSort/Views/MassUpdateExportDialog.xaml:85` (sprechende Du-Form-Tooltips).
Globaler Schalter: `HBSort/ViewModels/SettingsViewModel.cs` + `App.xaml.cs:118`.

### Drift behoben (UX-3, v0.1.25 Welle 3)

`BlInventoryView.xaml:163` — Tooltip der "Reserviert"-Spalte sagte noch
"aktuell durchgehend leer" (Pre-beta.8-Stand). Neuer Text: *"Zeigt, wie
viele Stueck dieses Lots fuer wartende Figuren reserviert sind."*

---

## 11. Panel-Header-Toolbar (Ueberschrift + Aktionen im engen Panel)

### Regel

- Wenn ein Panel-Header aus **Ueberschrift + einem oder mehreren Aktions-
  Bedienelementen** besteht und das Panel schmal ist (z.B. das rechte
  Bottom-Right-Panel im Sortier-Tab): **nicht** alles in eine Zeile mit
  `*`/`Auto`/`Auto`-Spalten quetschen — die `Auto`-Controls verdraengen
  sonst die `*`-Ueberschrift bis zum Abschneiden.
- Stattdessen: **Ueberschrift in eine eigene volle Zeile**, darunter eine
  **Toolbar-Zeile** (eigenes `Grid`) mit den Aktions-Bedienelementen.
  Aufbau aussen ein `StackPanel`: Zeile 1 = `TextBlock` (Ueberschrift),
  Zeile 2 = `Grid` mit den Buttons/Toggles.
- Gilt nur fuer **enge** Panels. Breite Tab-Header (z.B. BL-Inventar-Tab,
  ganze Fensterbreite) duerfen Ueberschrift + Buttons weiter in einer
  Zeile haben — dort ist genug Platz.

### Beispiel

```xml
<StackPanel Grid.Row="0" Margin="0,0,0,8">
    <TextBlock Text="WAS KANN ICH BAUEN?" FontSize="14" FontWeight="SemiBold"/>
    <!-- Toolbar-Zeile unter der Ueberschrift -->
    <Grid Margin="0,6,0,0">
        <Grid.ColumnDefinitions>
            <ColumnDefinition Width="Auto"/>
            <ColumnDefinition Width="*"/>
        </Grid.ColumnDefinitions>
        <Button Grid.Column="0" Content="Alle Preise holen" .../>
        <CheckBox Grid.Column="1" Content="BL-Inventar beruecksichtigen" .../>
    </Grid>
</StackPanel>
```

### Vorbild-Datei

`HBSort/Views/BuildSuggestionsView.xaml` (Header-Block, Z.28-61) — entstanden
aus dem UX-7-Fix (v0.1.25 Welle 3). Vorher quetschten Ueberschrift + Button +
Toggle in einer `*`/`Auto`/`Auto`-Zeile, der Button verdraengte die
Ueberschrift; jetzt zweizeilig.

### Abgrenzung

Breiter Tab-Header als Gegenbeispiel (einzeilig erlaubt):
`HBSort/Views/BlInventoryView.xaml` (Z.22-49) — volle Fensterbreite,
"BRICKLINK INVENTAR" + zwei Buttons passen problemlos nebeneinander.

---

## 12. Drift-Inventur

Vollstaendige Liste der UI-Drifts aus dem v0.1.24-Stable-Stand, sortiert nach
Backlog-Item. War die Abarbeitungs-Checkliste fuer den v0.1.25-Kosmetik-Sweep.

**Status: alle Drifts behoben in Wellen 1-3 (v0.1.25, 2026-05-29).**

| Datei : Zeile | Drift | Soll-Zustand | Backlog | Status |
|---|---|---|---|---|
| `MinifigSummaryDialog.xaml:199-201` | "Schliessen" mit `AccentButtonStyle`, kein links/rechts-Split | grau, ohne Style | UX-1 | ✅ Welle 3 |
| `BinDetailDialog.xaml:157-159` | "Schliessen" (Read-Only) mit `AccentButtonStyle` | grau | UX-1 | ✅ Welle 3 |
| `FloatingPartDetailDialog.xaml:109-110` | "Schliessen" (Read-Only) mit `AccentButtonStyle` | grau | UX-1 | ✅ Welle 3 |
| `BlReserveDialog.xaml:120-124` | "Abbrechen" rechts (Grid-Spalte 1) statt links | Abbrechen links | UX-2 | ✅ Welle 3 |
| `ManageIgnoredDialog.xaml:86-90` | "Schliessen" mit `AccentButtonStyle` + `IsDefault`+`IsCancel` (Read-Only-Verletzung) | grau, nur `IsCancel` | B2 | ✅ Welle 3 |
| `MassUpdateExportDialog.xaml:87-92` | "Schliessen" rechts hinter Primaer-Button "Verifizieren" | Schliessen links, primaer rechts | B2 | ✅ Welle 3 |
| `MassUpdateExportDialog.xaml.cs` | `Close_Click` setzt `DialogResult=true` statt `false`/`null` | `false`/`null` | B6/B10 | ✅ Welle 2 |
| `BuildSuggestionsView.xaml.cs:32`, `BlInventoryView.xaml.cs:35` | Dialog-VMs urspruenglich per `new` statt DI | `AddTransient` + `GetRequiredService` | B1 | ✅ Welle 2 |
| `BuildSuggestionDetailDialog.xaml:166` | Badge-Text hartkodiert "BL-Shop" | `{Binding BlAvailability.BadgeText}` (Binding-Konsistenz) | UX-4 | ✅ Welle 3 |
| `BlInventoryView.xaml:163` | Tooltip-Text veraltet ("aktuell durchgehend leer") | aktuellen Text setzen | UX-3 | ✅ Welle 3 |

**Korrekte Footer (Referenz, nicht anfassen):** `BsxExportDialog.xaml`,
`WantedListExportDialog.xaml`, `BinCreateDialog.xaml`,
`BinBulkCreateDialog.xaml`, `BuildSuggestionDetailDialog.xaml` (Footer),
`DismantleWizardDialog.xaml`.

---

*Zuletzt aktualisiert: 2026-05-29 (ux-analyst + Welle-3-Fix). Alle in der
Drift-Inventur gelisteten Befunde sind behoben (Wellen 1-3, v0.1.25). Neue
Drifts beim nächsten UX-Check hier ergänzen.*
