# Quellen und Lizenzen der Walküren-Transport-Sounds (GC_Server)

Stand: 2026-10-19. Alle 9 Dateien sind OGG Vorbis mit 44,1 kHz: Effekte mono (ca. 55–80 kbit/s), Musik stereo (ca. 125 kbit/s). Die dekodierte Spitze liegt bei etwa −1 dBFS, es gibt kein Clipping, und am Anfang steht keine Stille. Zusammen sind es 5 638 255 Bytes (5,6 MB).

- **Originale:** Jede Originaldatei liegt mit einer `QUELLE.txt` (Seiten-URL, Datei-URL, Urheber, Lizenz, Datum, Verwendung) in einem eigenen Ordner unter `Valheim\audio-quellen\<nn>_<name>\`.
- **Kandidaten:** Dort liegen auch Kandidaten, die nicht verwendet wurden. Sie sind in ihrer `QUELLE.txt` als „NICHT VERWENDET“ markiert.
- **Neu bauen:** Die Skripte liegen in `Valheim\audio-werkstatt\`, nämlich `build_sfx.py` und `build_music.py` (Python mit numpy, scipy und soundfile).

**Lizenzprüfung:** Jede Lizenz wurde am 2026-10-19 auf der jeweiligen Quellseite geprüft:

- Wikimedia Commons über die API (extmetadata) und den Wikitext der Dateiseite
- OpenGameArt über das Feld „License(s)“ der Seite
- incompetech über die Lizenzseite mit dem erzeugten Credit-Text
- der Walkürenritt über die offizielle YouTube-Beschreibung des Herausgebers
- bei den zwei SoundBible-Dateien zusätzlich über die SoundBible-Seite

Verwendet wurden nur CC0, gemeinfreie Werke (Public Domain, auch US-Bundesbehörden), CC BY 3.0 und CC BY 4.0. Es gibt **kein** CC BY-SA, kein NC oder ND und keine Mitschnitte aus Filmen oder Spielen. Das Horn im Stil von „Krieg der Welten“ ist ein eigener Nachbau aus einem gemeinfreien Nebelhorn und Synthese.

Abkürzungen: HT = Halbton, OGA = OpenGameArt.org, RT60 = Nachhallzeit.

| Datei | Zweck | Quelle(n) | Urheber | Lizenz | Bearbeitung |
|---|---|---|---|---|---|
| `valk_screech.ogg` (3,95 s) | Schrei der Walküre beim Kommen und bei der Abgabe | [Commons: Screaming Hawk.wav](https://commons.wikimedia.org/wiki/File:Screaming_Hawk.wav), Original: [soundbible.com/1517-Screaming-Hawk.html](https://soundbible.com/1517-Screaming-Hawk.html) | PsychoBird | [CC BY 3.0](https://creativecommons.org/licenses/by/3.0/) | Hauptschrei (0–2,75 s) 3 HT tiefer per Resampling, leicht verstimmte Dopplung (−3,25 HT, 12 ms versetzt) für mehr Breite, EQ (Hochpass 350 Hz, +3 dB bei 1,4 kHz), leichte Sättigung, großer Hall (RT60 3 s), Limiter, Normalisierung, Ausblendung |
| `scream_wail.ogg` (3,90 s) | Einzelner Horror-Schrei (Easteregg) | [Commons: Nick121087 - Demonic Woman Scream (cc0) (freesound).mp3](https://commons.wikimedia.org/wiki/File:Nick121087_-_Demonic_Woman_Scream_(cc0)_(freesound).mp3) | nick121087 | [CC0 1.0](https://creativecommons.org/publicdomain/zero/1.0/) | 0–2,75 s, 2 HT tiefer, Hochpass 200 Hz, Hall (RT60 2,2 s), Limiter, Ausblendung. Bezogen nur von Wikimedia Commons, nicht von Freesound |
| `quake_rumble.ogg` (8,00 s, Schleife) | Tiefes Erdbebengrollen, nahtlos schleifbar (Easteregg) | Eigene Synthese, keine Fremdquelle | für GC_Server erzeugt | keine Fremdrechte | Periodisches Bandrauschen (16–80, 55–260 und 180–900 Hz) mit langsamen periodischen Schwankungen, Geröll-Knistern, Sättigung und zirkulärem Hall. Die Schleifennaht liegt an der leisesten Stelle, ohne Ein- oder Ausblendung |
| `boom_big.ogg` (4,80 s) | Große Explosion am Himmel, wenn Geschosse platzen (Easteregg) | [OGA: Cannon fire](https://opengameart.org/content/cannon-fire); [OGA: Chunky Explosion](https://opengameart.org/content/chunky-explosion); eigene Synthese | Thimras; Joth | [CC0 1.0](https://creativecommons.org/publicdomain/zero/1.0/) (beide) | Kanonenschuss als Anfangsknall und eine Explosion als Körper. Synthetisch dazu: Knall-Transient, Sub-Druckwelle (58→24 Hz), rollendes Nachgrollen und Krachen in den Mitten. Sättigung, weiter Hall (RT60 3,2 s), Limiter, 1,6 s Ausblendung |
| `heartbeat.ogg` (3,60 s, Schleife) | Langsamer, tiefer, bedrohlicher Herzschlag mit 3 Schlägen | [OGA: Heartbeat sounds](https://opengameart.org/content/heartbeat-sounds) (`heartbeat_slow`) | bart | [CC0 1.0](https://creativecommons.org/publicdomain/zero/1.0/) | Ein Schlag 3 HT tiefer, dreimal im Abstand von 1,2 s gesetzt (50 Schläge pro Minute). Dazu synthetische Sub-Schläge und ein Klopf-Anteil (140–450 Hz), damit der Schlag auch auf kleinen Lautsprechern hörbar ist. Sättigung, EQ und zirkulärer Hall machen die Schleife nahtlos |
| `choir_doom.ogg` (7,61 s) | Bedrohlich anschwellender Chor (Easteregg) | [incompetech: Arcadia](https://incompetech.com/music/royalty-free/index.html?isrc=USUAN1100326) | Kevin MacLeod (incompetech.com) | [CC BY 4.0](https://creativecommons.org/licenses/by/4.0/) | Ausschnitt 66,0–69,8 s, ein verminderter Akkord H–D–F. Eine Oktave tiefer und damit halb so schnell. Schwillt über 3,2 s an. Dazu ein synthetischer Bordun auf H und F (Tritonus), Hall (RT60 3,5 s) und Ausblendung |
| `music_valkyrie.ogg` (58,25 s, stereo) | Heroische Musik, während die Walküre kommt und fortfliegt | [incompetech: Strength of the Titans](https://incompetech.com/music/royalty-free/index.html?isrc=USUAN1900052) (ISRC USUAN1900052) | Kevin MacLeod (incompetech.com) | [CC BY 4.0](https://creativecommons.org/licenses/by/4.0/) | Ganzes Stück bis zum natürlichen Ende. Stille am Anfang entfernt, Normalisierung auf −1 dBFS, 2,5 s Ausblendung, ca. 123 kbit/s |
| `music_battle.ogg` (145,91 s, stereo) | Kampfmusik für den folgenden Raid | [incompetech: Five Armies](https://incompetech.com/music/royalty-free/index.html?isrc=USUAN1100875) (ISRC USUAN1100875) | Kevin MacLeod (incompetech.com) | [CC BY 4.0](https://creativecommons.org/licenses/by/4.0/) | Beginnt bei 7,92 s mit dem Trommel-Einsatz vor dem großen Tutti und läuft bis zum natürlichen Ende. Normalisierung, 3 s Ausblendung, ca. 125 kbit/s |
| `music_easter.ogg` (137,43 s, stereo) | Völlig übertriebene Easteregg-Musik: der Walkürenritt, und am Ende kommt nur eine Möwe | Lizenzangabe: [YouTube: „Ride of the Valkyries - Wagner [COPYRIGHT FREE]“](https://www.youtube.com/watch?v=uNkRW_9pHRQ); Download: [pmmusic.pro/downloads](https://pmmusic.pro/downloads/) | Komposition: Richard Wagner (gemeinfrei). Aufnahme: dirigiert von Philip Milman (pmmusic.pro), finanziert von Ludwig und Schlatt, veröffentlicht von „Lud and Schlatts Musical Emporium“ | [CC BY 3.0](https://creativecommons.org/licenses/by/3.0/) | Ganze Aufnahme bis zum natürlichen Ende, von 48 auf 44,1 kHz umgerechnet. Sanfte Kompression (2,2:1) und Limiter machen sie etwa 3,5 dB lauter. Normalisierung, 2,5 s Ausblendung, ca. 126 kbit/s |

## Namensnennung (CC-BY)

Diese Zeilen gehören in die README der Mod oder in einen Credits-Abschnitt. CC BY verlangt Titel, Urheber, Quelle, Lizenz mit Link und den Hinweis, dass die Datei bearbeitet wurde.

**Musik (Kevin MacLeod), Wortlaut nach incompetech:**

```
"Strength of the Titans", "Five Armies", "Arcadia"
Kevin MacLeod (incompetech.com)
Licensed under Creative Commons: By Attribution 4.0
http://creativecommons.org/licenses/by/4.0/
Bearbeitet: "Five Armies" gekürzt, "Arcadia" als Ausschnitt eine Oktave tiefer mit Hall (choir_doom.ogg), alle normalisiert.
```

**Walkürenritt:**

```
"Ride of the Valkyries" (Richard Wagner), dirigiert von Philip Milman (https://pmmusic.pro/),
veröffentlicht von Lud and Schlatts Musical Emporium – https://www.youtube.com/watch?v=uNkRW_9pHRQ
Lizenz: CC BY 3.0 – https://creativecommons.org/licenses/by/3.0/
Bearbeitet: komprimiert, lauter gemacht, normalisiert, ausgeblendet.
```

**Geräusche:**

```
"Screaming Hawk" von PsychoBird – https://soundbible.com/1517-Screaming-Hawk.html
Lizenz: CC BY 3.0 – https://creativecommons.org/licenses/by/3.0/
Bearbeitet: tiefer gestimmt, gedoppelt, mit Hall (valk_screech.ogg).
```

**Freiwilliger Dank (nicht vorgeschrieben, weil CC0 oder gemeinfrei):** nick121087 (Schrei, über Wikimedia Commons), Thimras, Joth und bart (OpenGameArt).

## Aussortiert (Gorduan, 19.10.2026)

Gorduan: Auch das Easteregg muss zu Valheim passen und darf nicht nach Meme klingen; Valheim hat eigenen Donner, eigenes Heulen und eine eigene Möwe. Diese Dateien stecken nicht mehr in der Mod; sie liegen in `Valheim\audio-werkstatt\aussortiert-2026-10-19\`, ihre Originale weiter in `audio-quellen`.

| Datei | Grund |
|---|---|
| `thunder_strike.ogg` | Valheim hat eigenen Donner |
| `thunder_short.ogg` | Valheim hat eigenen Donner |
| `braam_horn.ogg` | Nebelhorn nicht gewünscht; tiefer Ton jetzt aus Valheim (Opferstein-Ruf, Faders Brüllen) |
| `evil_laugh.ogg` | kein Lachen, auch keine Alternative |
| `howl_scream.ogg` | Valheim hat eigenes Heulen (Fenring) |
| `seagull_call.ogg` | Valheim hat eine eigene Möwe |
| `sad_trombone.ogg` | zu sehr Meme |
| `siren.ogg` | passt nicht zu Valheim |

Statt dessen spielt die Mod Valheims eigene Geräusche: `sfx_thunder`, `sfx_mistlands_thunder`, `sfx_distant thunder` (Donner), `sfx_fenring_howl` (Heulen), `sfx_seagull_idle` (Möwe), `fx_bossstone_activate_Eikthyr` und `fx_bossstone_activate_Yagluth` (Ruf der Opfersteine) und `fx_Fader_Roar` (Faders Brüllen).
