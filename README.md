# Hilo Peak Event Scheduler

Automatically controls your **ecobee thermostat** during Hydro-Québec [Hilo](https://www.hiloenergie.com/) peak events — no Alexa, no Virtual Smart Home, no cloud middleman.

The scheduler connects directly to your ecobee over your **local network** using the HomeKit Accessory Protocol, and polls the Hilo API for upcoming peak events every 4 hours.

---

## How it works

When a Hilo peak event is detected, the scheduler executes this temperature sequence automatically:

| Time | Action | Default temp |
|------|--------|-------------|
| T - 120 min | Preheat (low) - save current setpoint | 24 C |
| T - 30 min | Preheat (high) - final boost | 25 C |
| T + 0 | Reduction - event begins | 16 C |
| T + 240 min | Recovery - restore saved setpoint | (original) |

All timings and temperatures are configurable via `appsettings.json`.

---

## Requirements

- Windows x64
- An **ecobee** thermostat with HomeKit enabled
- A **Hilo** account (Hydro-Quebec)
- Both devices on the same local network

---

## Setup

### Step 1 - Pair with your ecobee (one time only)

Enable HomeKit on the ecobee first:

> Thermostat screen -> Menu -> Settings -> HomeKit -> Enable

Your ecobee will display a PIN. Then run:

```powershell
cd HiloScheduler
HiloScheduler.exe --pair --pin 12345678
```

The PIN can be passed with or without dashes (`12345678` or `123-45-678`).
The scheduler will discover your ecobee automatically via mDNS.
Credentials are saved to `ecobee_pairing.json`.

> **Optional:** pass `--ip 192.168.x.x` to target a specific ecobee if you have multiple HomeKit devices.

---

### Step 2 - Authenticate with Hilo (one time only)

```powershell
HiloScheduler.exe --login
```

Your browser will open to the Hilo login page. After logging in, you will land on a `my.home-assistant.io` page. **Copy the full URL from your browser address bar** and paste it back into the terminal.

Tokens are saved to `hilo_tokens.json` and refreshed automatically on every subsequent run — you will not need to log in again unless the refresh token expires.

---

### Step 3 - Download files from the latest release and copy them to a folder on your PC

```
copy HiloScheduler.exe    C:\HiloScheduler\
copy ecobee_pairing.json  C:\HiloScheduler\
copy hilo_tokens.json     C:\HiloScheduler\
copy appsettings.json     C:\HiloScheduler\
```

---

### Step 4 - Install as a Windows Service

Open an **elevated PowerShell** (right-click Start -> Windows PowerShell -> Run as Administrator):

```powershell
sc.exe create HiloScheduler binpath= "C:\HiloScheduler\HiloScheduler.exe" start= auto
sc.exe start HiloScheduler
```

The service starts automatically with Windows and runs silently in the background. No third-party tools required.

---

## Service management

```powershell
sc.exe start   HiloScheduler   # start the service
sc.exe stop    HiloScheduler   # stop the service
sc.exe query   HiloScheduler   # check status
sc.exe delete  HiloScheduler   # uninstall the service
```

Logs are written to the Windows Event Log and are visible in **Event Viewer -> Windows Logs -> Application**, filtered by source `HiloScheduler`.

---

## Configuration

Edit `appsettings.json` next to the exe to change any setting. Restart the service for changes to take effect.

```json
{
  "Scheduler": {
    "PairingFile":        "ecobee_pairing.json",
    "TokenFile":          "hilo_tokens.json",
    "TempPreheatLow":     24.0,
    "TempPreheatHigh":    25.0,
    "TempReduction":      16.0,
    "EventDurationMin":   240,
    "PreheatTotalMin":    120,
    "PreheatHighMin":     30,
    "CheckIntervalHours": 4
  }
}
```

| Setting | Default | Description |
|---------|---------|-------------|
| `TempPreheatLow` | `24.0` | Temperature (C) during early preheat |
| `TempPreheatHigh` | `25.0` | Temperature (C) during final preheat boost |
| `TempReduction` | `16.0` | Temperature (C) during the event |
| `EventDurationMin` | `240` | Assumed event length in minutes |
| `PreheatTotalMin` | `120` | Total preheat window in minutes before event start |
| `PreheatHighMin` | `30` | Minutes before event to apply high preheat |
| `CheckIntervalHours` | `4` | How often to poll Hilo for new events |
| `PairingFile` | `ecobee_pairing.json` | Path to the ecobee HomeKit pairing credentials |
| `TokenFile` | `hilo_tokens.json` | Path to the Hilo OAuth token cache |

Paths can be absolute or relative to the exe location.

---

## CLI reference

| Command | Description |
|---------|-------------|
| `HiloScheduler.exe` | Run as foreground process or Windows Service (loops every 4h) |
| `HiloScheduler.exe --once` | Single check and exit |
| `HiloScheduler.exe --login` | Interactive one-time Hilo authentication |
| `HiloScheduler.exe --pair --pin 12345678` | Pair with ecobee |
| `HiloScheduler.exe --pair --pin 12345678 --ip 192.168.x.x` | Pair targeting a specific IP |

---

## Files

| File | Contents | Commit to git? |
|------|----------|---------------|
| `ecobee_pairing.json` | HomeKit pairing keys | No - contains private keys |
| `hilo_tokens.json` | Hilo OAuth access + refresh tokens | No - contains credentials |
| `appsettings.json` | Scheduler configuration | Yes - no secrets |

Credential files are excluded by `.gitignore`.

---

## Architecture

```
HiloScheduler.exe
|
+-- Hilo API  (HTTPS)
|   +-- OAuth2 PKCE login      -> hilo_tokens.json
|   +-- GET /Locations         -> location ID
|   +-- GET /Seasons           -> upcoming peak events
|
+-- ecobee HomeKit  (local TCP, no cloud)
    +-- mDNS discovery         -> finds ecobee IP + port automatically
    +-- HAP pair-setup         -> SRP-6a + Ed25519 key exchange -> ecobee_pairing.json
    +-- HAP pair-verify        -> X25519 + Ed25519 session handshake
    +-- ChaCha20-Poly1305      -> encrypted session
    +-- HAP characteristics    -> read / write TargetTemperature
```

The ecobee is controlled entirely over your **local network** using the HomeKit Accessory Protocol. No internet connection is required for thermostat control once paired.

---

## Credits

- Hilo API endpoints and constants sourced from [dvd-dev/python-hilo](https://github.com/dvd-dev/python-hilo)
- HomeKit pairing protocol based on the [Apple HAP specification](https://developer.apple.com/homekit/)
