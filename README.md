# Receptbanken 🍳

**Receptbanken** är en modern, komplett och självhostad webbapplikation för personlig recepthantering. 

Applikationen är byggd i **C# och .NET 10 LTS** med **Blazor Web App (Interactive Server)**, `IDbContextFactory<AppDbContext>`, **Microsoft Fluent UI Blazor** och en robust **SQLite-databas** hanterad via **Entity Framework Core**.

Den är utformad för att köras på en personlig Ubuntu Linux-hemserver (eller valfritt system med Docker), är helt fri från externa abonnemang och kräver **inga betalda API:er eller AI-tjänster**. All receptimport, svensk ingredienstolkning och portionsberäkning sker lokalt, deterministiskt och privat.

> [!IMPORTANT]
> **Nätverkskrav och Säkerhet:**
> Applikationen har medvetet ingen inbyggd användarautentisering och är avsedd att köras uteslutande på ett betrott lokalt hemnätverk (LAN/VLAN) eller skyddas bakom en autentiserande omvänd proxy (såsom Cloudflare Zero Trust, Tailscale eller Authelia). Exponera den inte direkt mot det publika internet!

---

## Egenskaper och Funktioner

- 🍲 **Komplett recepthantering**: Skapa, visa, redigera, duplicera och radera recept med bild, tider, beskrivning, strukturerade ingredienser och steg-för-steg-instruktioner.
- 🔗 **Smart receptimport från webbplatser**: Klistra in en länk från svenska och internationella matsajter (t.ex. ICA, Arla, Köket.se, Tasteline m.fl.). Systemet extraherar Schema.org JSON-LD-data eller tillämpar svenska HTML-heuristiker.
- 🛡️ **Stärkt SSRF-skydd**: Skyddar hemnätverket mot DNS Rebinding (via socket `ConnectCallback`), IPv4-mapped IPv6, RFC 1918-adresser, loopback och molnmetadata. Samma skydd gäller för externa bildnedladdningar.
- 👁️ **Obligatorisk förhandsgranskning**: Inget recept sparas automatiskt i databasen — användaren granskar, redigerar och godkänner alltid innan lagring sker.
- 🇸🇪 **Svensk ingredienstolk**: Förstår svenska mått och format som `1,5 dl`, `2 msk`, `½ tsk`, `200 g`, `1-2 klyftor vitlök` och `1 burk krossade tomater`.
- 🖼️ **Lokal bildlagring**: Bilder laddas upp eller importeras till serverns disk (konverteras till optimerat WebP-format) — inga beroenden av externa bildlänkar.
- 🏷️ **Kategorier & M:N-relationer**: Skapa egna färgkodade kategorier och tilldela flera kategorier per recept.
- 🔍 **Realtidssökning & Filtrering**: Sök blixtsnabbt över receptnamn, beskrivningar, ingredienser och kategorier.
- ⭐ **Favoriter**: Markera dina favoritrecept och filtrera fram dem med ett klick.
- ⚖️ **Dynamisk portionsskalning**: Ändra portionsantalet (+ / -) i detaljvyn för att direkt omräkna alla ingrediensmängder proportionellt.
- 🛒 **Inköpslista med aggregering**: Lägg till ingredienser från valda recept. Kompatibla mått och råvaror slås samman automatiskt. Avbockningsstöd i butiken.
- 👨‍🍳 **Matlagningsläge (Cooking Mode)**: Fullskärmsvy för mobil/surfplatta med **ett steg i taget**, stor läsbar text, utdragbar ingredienspanel och **Screen Wake Lock** (håller skärmen tänd under matlagningen).
- 💾 **Transaktionell Säkerhetskopiering**: Exportera hela din receptbank till ett portabelt `.zip`-arkiv. Återställning med skydd mot Zip Bomb (dekomprimeringsgräns 500 MB, max 5 000 filer), Zip Slip, och atomär återställning som rullar tillbaka både SQLite-transaktionen och filsystemet vid minsta fel.
- ⚡ **Högpresterande DbContext-hantering**: Byggt med `IDbContextFactory<AppDbContext>` för kortlivade databaskontexter i Blazor Server utan samtidighetsproblem eller minnesläckor.
- 🐳 **Docker & Persistent lagring**: Databasen (`receptbanken.db`), bildkatalogen och backuper lagras i externa volymer så att container-uppdateringar aldrig raderar användardata.

---

## Teknologistack

| Område | Teknologi |
| :--- | :--- |
| **Plattform** | .NET 10 LTS (`net10.0`), C# 13/14 |
| **Webbramverk** | ASP.NET Core Blazor Web App (Interactive Server) |
| **Databasarkitektur** | `IDbContextFactory<AppDbContext>` & pragmatiska applikationstjänster |
| **UI-komponenter** | Microsoft Fluent UI Blazor (`v4.x`) |
| **Visuellt tema** | Mörkt kulinariskt tema ("Dark Culinary Theme") med varma accenter |
| **Databas** | SQLite med Write-Ahead Logging (WAL) |
| **ORM & Migreringar** | Entity Framework Core SQLite |
| **HTML-parsning** | AngleSharp |
| **JSON & Data** | System.Text.Json |
| **Bildoptimering** | SixLabors.ImageSharp |
| **Testning** | xUnit, FluentAssertions, bUnit, Playwright |
| **Driftsättning** | Docker, Docker Compose, Linux multi-stage container |

---

## Lösningsstruktur

Lösningen är organiserad i en ren, pragmatisk och lättunderhållen projektstruktur:

```
Receptbanken/
├── src/
│   ├── Receptbanken.Core/           # Domänmodeller, gränssnitt, svensk ingredienstolk, portionsberäkning
│   ├── Receptbanken.Infrastructure/ # EF Core, SQLite, lokal bildlagring, SSRF-skyddad webbimportör, zip-backup
│   └── Receptbanken.Web/            # Blazor Interactive Server, sidor, komponenter och anpassat tema
└── tests/
    ├── Receptbanken.Tests/          # xUnit enhets- och integrationstester med riktig SQLite och HTML-fixturer
    └── Receptbanken.E2E/            # Playwright webbläsartester för hela användarresor
```

---

## Körning och Utveckling

### Förutsättningar på Windows (Utvecklingsdator)
- [.NET 10.0 SDK](https://dotnet.microsoft.com/download/dotnet/10.0)
- Windows PowerShell 5.1 eller PowerShell 7+
- OpenSSH-klient (`ssh`, `scp`) och inbyggt `tar.exe` (ingår i Windows 10/11)
- **Ingen Docker Desktop krävs på Windows.** Docker-bygget utförs direkt på Ubuntu-servern.

### Förutsättningar på Ubuntu (Hemserver)
- Ubuntu Server 24.04 LTS (`x86_64`)
- Docker Engine och Docker Compose (`docker compose version`)
- SSH-nyckelautentisering konfigurerad för din användare (som tillhör gruppen `docker`)
- Målkatalogen `/opt/receptbanken` skapad och ägd av din SSH-användare:
  ```bash
  sudo mkdir -p /opt/receptbanken && sudo chown -R $USER:$USER /opt/receptbanken
  ```

### Kör lokalt med .NET CLI
```bash
# Klona eller navigera till projektmappen
cd Receptbanken

# Återställ paket och bygg lösningen
dotnet build

# Kör webbapplikationen
dotnet run --project src/Receptbanken.Web
```
Öppna webbläsaren på `http://localhost:5000` (eller den port som terminalen visar).

### Köra tester
```bash
# Kör alla enhets- och integrationstester
dotnet test tests/Receptbanken.Tests
```

---

## Driftsättning med Docker

Driftsättningsflödet är utformat så att utvecklingsdatorn på Windows inte behöver ha Docker installerat:

```text
Windows (.NET build + test + källkodsarkiv) → SCP → Ubuntu (Docker build) → Docker Compose
```

### 1. Konfiguration (`.deploy.env`)
Kopiera `.deploy.env.example` till `.deploy.env` i projektets rotkatalog (filen ignoreras av Docker) och kontrollera värdena:
```ini
SERVER_HOST=home-server
SERVER_USER=mikaelollhage
SERVER_PORT=22
DEPLOY_PATH=/opt/receptbanken
APP_PORT=8080
VOLUME_NAME=receptbanken-data
```

### 2. Första installation (`-FirstInstall`)
Vid allra första installationen på Ubuntu-servern (när varken containern `receptbanken` eller volymen `receptbanken-data` finns ännu) måste växeln `-FirstInstall` anges:
```powershell
.\scripts\deploy.ps1 -FirstInstall
```
Detta förhindrar att en vanlig uppdatering av misstag skapar en ny, tom datavolym om man skulle råka peka mot fel värd eller volymnamn.

### 3. Vanlig uppdatering
För alla efterföljande uppdateringar kör du:
```powershell
.\scripts\deploy.ps1
```
Skriptet utför då i ordning:
1. Kör `dotnet build -c Release` och `dotnet test -c Release` på Windows.
2. Paketerar endast nödvändiga källkods- och projektfiler till ett rent `.tar.gz`-arkiv (exkluderar `bin`, `obj`, `.git`, `.env`, databaser, bilder och hemligheter) och verifierar arkivets innehåll.
3. Överför arkivet via SCP till `/opt/receptbanken/builds/<version>/` på Ubuntu.
4. Bygger `receptbanken:<version>` på Ubuntu (`docker build --platform linux/amd64`) **medan den befintliga containern fortsätter köras**.
5. Stoppar den gamla containern kontrollerat, skapar en rå volymbackup av `receptbanken-data`, verifierar SHA-256-kontrollsumman och säkerställer att `receptbanken.db` finns i arkivet.
6. Uppdaterar `IMAGE_TAG` och startar den nya versionen med `docker compose up -d --no-build`.
7. Verifierar containerns hälsostatus och rensar den temporära byggkatalogen.

Du kan när som helst förhandsgranska flödet och testa arkivskapandet utan att ansluta till servern med:
```powershell
.\scripts\deploy.ps1 -DryRun
```

### 4. Persistent datalagring och Backup
Den namngivna volymen `receptbanken-data` monteras till `/app/data` och bevarar:
- `receptbanken.db` (samt WAL/SHM-filer vid drift)
- `images/recipes/` (uppladdade och importerade receptbilder i WebP-format)
- `keys/` (ASP.NET Core DataProtection-nycklar)

Vid varje uppdatering sparas verifierade backuper i `/opt/receptbanken/backups/` på Ubuntu-servern:
- `volume-<version>.tar.gz` + `volume-<version>.tar.gz.sha256` (komplett råkopia av `receptbanken-data` efter kontrollerat containerstopp)
- `app-<version>.zip` (portabel applikationsbackup från `/api/backup/export`)
De senaste 10 verifierade backup-uppsättningarna behålls automatiskt.

### 5. Återställning / Rollback (`scripts/rollback.ps1`)
- **Standardrollback (endast Docker-image, lämnar databas och bilder orörda):**
  ```powershell
  .\scripts\rollback.ps1
  ```
- **Fullständig återställning av både Docker-image och datavolym från backup:**
  Kräver uttrycklig bekräftelse (`ATERSTALL`) och skapar först en extra säkerhetskopia (`pre-rollback-<timestamp>.tar.gz`) av aktuellt datatillstånd:
  ```powershell
  .\scripts\rollback.ps1 -RestoreData
  ```

### 6. Hälsokontroll (Healthcheck)
- **HTTP-endpoint (`GET /health`):** Verifierar att webbservern svarar och att SQLite-databasen går att ansluta till och läsa (`SELECT 1;`). Returnerar HTTP `200 OK` vid frisk tjänst och `503 Service Unavailable` vid fel.
- **Container-prob (`--healthcheck`):** `dotnet Receptbanken.Web.dll --healthcheck` anropar `/health` internt och används av Dockers `HEALTHCHECK` utan externa beroenden av `curl` eller `wget`.
- Om en databasmigrering vid uppstart skulle misslyckas avbryter applikationen omedelbart uppstarten så att en trasig instans aldrig rapporteras som frisk.

### 7. Felsökning
- **PowerShell ExecutionPolicy på Windows:** Om Windows blockerar körning av `.ps1`-skript, kör antingen `Set-ExecutionPolicy -Scope CurrentUser RemoteSigned` eller anropa skriptet med `powershell -ExecutionPolicy Bypass -File .\scripts\deploy.ps1`.
- **Kontrollera containerloggar på Ubuntu:**
  ```bash
  ssh mikaelollhage@home-server "docker logs --tail 100 receptbanken"
  ```
- **Kontrollera hälsostatus och volym på Ubuntu:**
  ```bash
  ssh mikaelollhage@home-server "docker ps --filter name=receptbanken && docker volume inspect receptbanken-data"
  ```

---

## Säkerhet och Integritet

- **SSRF-skydd**: Inbyggd validering förhindrar att receptimportören skickar förfrågningar till interna nätverksadresser (t.ex. din router, NAS eller lokala servrar).
- **Validerad bilduppladdning**: Filer verifieras via Magic Bytes och sparas med slumpmässiga GUID:er utan att bevara osäkra filnamn.
- **Skydd mot Zip Slip**: Vid återställning från backup kontrolleras alla sökvägar så att filer aldrig kan skrivas utanför målmappen.
- **Inget externt beroende**: Receptdata, bilder och historik lämnar aldrig din server.

---

## Arkitektur och Dokumentation

För djupgående tekniska detaljer, se:
- [`ARCHITECTURE.md`](file:///c:/Users/mikae/Documents/git/Receptbanken/ARCHITECTURE.md) — Fullständig arkitekturspecifikation, ER-diagram, SSRF-pipeline, UI-principer och teststrategi.
- [`PROJECT_PLAN.md`](file:///c:/Users/mikae/Documents/git/Receptbanken/PROJECT_PLAN.md) — Detaljerad fasindelad genomförandeplan med checklistor.

