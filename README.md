# KNX Alternative

Eine offene Alternative zur ETS für Windows 10/11, um KNX-Anlagen zu planen und in Betrieb zu nehmen.

> **Stand: Phase 1.** Projekte, Gruppenadressen, Busmonitor und das Programmieren der physikalischen Adresse funktionieren.
> Das Laden von Applikationen und Parametern in Geräte folgt in Phase 2.

## Funktionen

| Bereich | Phase 1 (jetzt) |
| --- | --- |
| Projekt | ETS-Projekte (.knxproj, ETS 5 und 6, auch mit Projektpasswort) importieren, eigenes Format `.knxalt` speichern, automatische Sicherung beim Schließen |
| Gebäude | Gebäude, Etagen, Räume, Verteiler und Funktionen aus dem ETS-Projekt anzeigen |
| Topologie | Bereiche, Linien und Geräte mit physikalischer Adresse |
| Gruppenadressen | 3-stufig, 2-stufig, frei; anlegen, löschen, umbenennen, Datenpunkttyp, Raum- und Funktionsspalte, CSV-Export/-Import im ETS-Format |
| Geräte | Hersteller, Produkt, Bestellnummer, Applikation, Kommunikationsobjekte mit Flags und Verknüpfungen |
| Inbetriebnahme | Physikalische Adresse per Programmiertaste oder Seriennummer, Geräteinfo lesen, Neustart |
| Diagnose | Gruppenmonitor mit Klartextwerten und Aufzeichnung, Werte senden/lesen, Programmiermodus suchen, Adresse prüfen, Linienscan |
| Schnittstellen | KNXnet/IP Tunneling (IP-Schnittstellen und -Router), KNXnet/IP Routing (Multicast), automatische Suche |

Den vollständigen Funktionskatalog im Vergleich zur ETS 6.4.1 und den Phasenplan gibt es im Projektdokument.

## Herunterladen

Jeder Push baut automatisch eine Windows-Version: unter **Actions → Build → Artifacts → KnxAlternative-win-x64**.
Die ZIP-Datei enthält `KnxAlternative.exe` (Oberfläche) und `knx.exe` (Kommandozeile). Keine Installation nötig.

## Kommandozeile

```
knx discover
knx monitor --ip 192.168.1.50 --project MeinHaus.knxproj
knx write 1/0/1 1.001 ein --ip 192.168.1.50
knx read 3/1/0 9.001 --ip 192.168.1.50
knx set-address 1.1.5 --ip 192.168.1.50      (danach Programmiertaste drücken)
knx info 1.1.5 --ip 192.168.1.50
knx scan 1.1 --ip 192.168.1.50
knx project MeinHaus.knxproj --csv gruppenadressen.csv
```

## Selbst bauen

Voraussetzung: [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0) unter Windows.

```
dotnet build KnxAlternative.sln
dotnet test
dotnet run --project src/KnxAlternative.App
```

## Aufbau

| Projekt | Inhalt |
| --- | --- |
| `src/KnxAlternative.Core` | Adressen, Datenpunkttypen, cEMI, KNXnet/IP, Geräteverwaltung, Projektmodell, .knxproj-Import |
| `src/KnxAlternative.App` | Windows-Oberfläche (WPF) |
| `src/KnxAlternative.Cli` | Kommandozeilenwerkzeug `knx` |
| `tests/KnxAlternative.Tests` | Unit-Tests und Tests gegen ein simuliertes KNX-IP-Gateway |

## Hinweise

- Die Busanbindung ist eine eigene Implementierung des offenen KNXnet/IP-Protokolls, ohne ETS-Komponenten.
- Herstellerdaten werden nur aus Projekten gelesen, die du selbst besitzt. Verschlüsselte Produktdaten werden nicht umgangen.
- Vor dem Programmieren echter Geräte immer zuerst eine Sicherung des ETS-Projekts behalten.
