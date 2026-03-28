# Hilo Peak Event Scheduler

Automatically controls your **ecobee thermostat** during Hydro-Québec [Hilo](https://www.hiloenergie.com/) peak events — no Alexa, no Virtual Smart Home, no cloud middleman.

It connects directly to your ecobee over your local network via HomeKit (using [aiohomekit](https://github.com/Jc2k/aiohomekit)), and polls the Hilo API for upcoming peak events once per day.

---

## How it works

When a Hilo peak event is detected, the scheduler executes this temperature sequence automatically:

| Time | Action | Default temp |
|------|--------|-------------|
| T − 120 min | Preheat (low) — save current setpoint | 24 °C |
| T − 30 min | Preheat (high) — final boost | 25 °C |
| T + 0 | Reduction — event begins | 16 °C |
| T + 240 min | Recovery — restore saved setpoint | *(original)* |

All timings and temperatures are configurable via environment variables.

---

## Requirements

- Python 3.11+
- An **ecobee** thermostat with HomeKit enabled
- A **Hilo** account (Hydro-Québec)
- Both devices on the same local network

---

## Installation

```bash
pip install aiohomekit aiohttp python-dotenv zeroconf
```

Clone or download `hilo_scheduler.py` into a folder.

---

## Setup

### Step 1 — Pair with your ecobee (one time only)

Enable HomeKit on the ecobee first:

> Thermostat screen → Menu → Settings → HomeKit → Enable

Your ecobee will display a PIN. Then run:

```bash
python3 hilo_scheduler.py --pair --pin 12345678
```

The PIN can be passed with or without dashes (`12345678` or `123-45-678`).  
Credentials are saved to `ecobee_pairing.json`.

> **Optional:** pass `--ip 192.168.x.x` to target a specific ecobee if you have multiple HomeKit devices.

---

### Step 2 — Authenticate with Hilo (one time only)

Run the scheduler for the first time:

```bash
python3 hilo_scheduler.py --once
```

Your browser will open to the Hilo login page. After logging in, you will land on a `my.home-assistant.io` page. **Copy the full URL from your browser address bar** and paste it back into the terminal.

Tokens are saved to `hilo_tokens.json` and refreshed automatically on every subsequent run — you won't need to log in again unless the refresh token expires.

---

### Step 3 — Run the scheduler

**Continuous mode** (re-checks every 4 hours by default):
```bash
python3 hilo_scheduler.py
```

**Single check and exit:**
```bash
python3 hilo_scheduler.py --once
```

---

## Configuration

All settings can be overridden via environment variables or a `.env` file in the same directory.

| Variable | Default | Description |
|----------|---------|-------------|
| `TEMP_PREHEAT_LOW` | `24.0` | Temperature (°C) during early preheat |
| `TEMP_PREHEAT_HIGH` | `25.0` | Temperature (°C) during final preheat boost |
| `TEMP_REDUCTION` | `16.0` | Temperature (°C) during the event |
| `EVENT_DURATION_MIN` | `240` | Assumed event length in minutes |
| `PREHEAT_TOTAL_MIN` | `120` | Total preheat window in minutes before event |
| `PREHEAT_HIGH_MIN` | `30` | Minutes before event to apply high preheat |
| `CHECK_INTERVAL_HOURS` | `4` | How often to poll for new events in loop mode |
| `PAIRING_FILE`
| `HILO_TOKEN_FILE` | `hilo_tokens.json` | Path to the Hilo OAuth token cache |
| `HILO_CLIENT_ID` | *(built-in)* | Hilo OAuth client ID (public, from dvd-dev/python-hilo) |
| `HILO_SUBSCRIPTION_KEY` | *(built-in)* | Hilo API subscription key (public, from dvd-dev/python-hilo) |

**Example `.env` file:**
```env
TEMP_PREHEAT_LOW=23.5
TEMP_PREHEAT_HIGH=25.0
TEMP_REDUCTION=17.0
EVENT_DURATION_MIN=180
```

---

## CLI reference

| Command | Description |
|---------|-------------|
| `python3 hilo_scheduler.py` | Run continuously, re-check every 4 hours |
| `python3 hilo_scheduler.py --once` | Single check and exit |
| `python3 hilo_scheduler.py --pair` | Pair with ecobee (interactive PIN prompt) |
| `python3 hilo_scheduler.py --pair --pin 12345678` | Pair with PIN supplied directly |
| `python3 hilo_scheduler.py --pair --pin 12345678 --ip 192.168.x.x` | Pair targeting a specific IP |
| `python3 hilo_scheduler.py --test-event` | Simulate a full event cycle in ~30 seconds |

---

## Running as a Windows background service

The scheduler can run as a proper Windows Service using [NSSM](https://nssm.cc/) (Non-Sucking Service Manager), so it starts automatically with Windows and runs silently in the background.

> ⚠️ **Important:** The very first run requires an interactive browser login to authenticate with Hilo. Complete that step before installing the service — after that, tokens are refreshed silently and no browser is needed.

### Step 1 — Complete the interactive login first

```powershell
python3 hilo_scheduler.py --once
```

Log in when the browser opens, paste the redirect URL back into the terminal. This creates `hilo_tokens.json` which the service will reuse automatically.

### Step 2 — Install NSSM

```powershell
winget install nssm
```

> ℹ️ All `nssm` commands must be run in an **elevated PowerShell** (right-click Start → Windows PowerShell → Run as Administrator).

### Step 3 — Find your Python and NSSM paths

Services run with a minimal PATH, so full paths are required:

```powershell
# Find Python
where.exe python3

# Find nssm (winget doesn't always add it to PATH)
$nssm = (Get-ChildItem "$env:LOCALAPPDATA\Microsoft\WinGet\Packages\NSSM*" -Recurse -Filter "nssm.exe" | Where-Object { $_.FullName -like "*win64*" }).FullName
echo $nssm
```

### Step 4 — Register and start the service

Replace the paths and `MACHINE\Username` with your actual values :

```powershell
& $nssm set HiloScheduler Application   "C:\Users\JP\AppData\Local\Python\bin\python3.exe"
& $nssm set HiloScheduler AppParameters "C:\hilo_scheduler.py"
& $nssm set HiloScheduler AppDirectory  "C:\"
& $nssm set HiloScheduler ObjectName    "MACHINE\Username"
& $nssm set HiloScheduler AppStdout     "C:\hilo.log"
& $nssm set HiloScheduler AppStderr     "C:\hilo.log"
& $nssm set HiloScheduler AppStdoutCreationDisposition 4
& $nssm set HiloScheduler AppStderrCreationDisposition 4
& $nssm start HiloScheduler
```

The `ObjectName` line will prompt for your Windows password. This is required so the service runs as **your user account**, giving it access to your Python install and credential files.

### Service management

```powershell
nssm status HiloScheduler    # check if running
nssm stop HiloScheduler      # stop the service
nssm restart HiloScheduler   # restart the service
nssm remove HiloScheduler    # uninstall the service
```

Logs are written to `hilo.log` in the script directory.

---

## Files created

| File | Contents | Commit to git? |
|------|----------|---------------|
| `ecobee_pairing.json` | HomeKit pairing keys | ❌ No — contains private keys |
| `hilo_tokens.json` | Hilo OAuth access + refresh tokens | ❌ No — contains credentials |
| `.env` | Optional configuration overrides | ❌ No |

Both credential files are excluded by the included `.gitignore`.

---

## Architecture

```
hilo_scheduler.py
│
├── Hilo API  (HTTPS)
│   ├── OAuth2 PKCE login  → hilo_tokens.json
│   ├── GET /Locations     → location ID
│   └── GET /Seasons       → upcoming peak events
│
└── ecobee HomeKit  (local TCP, no cloud)
    ├── mDNS discovery     → finds ecobee IP + port automatically
    ├── HAP pairing        → ecobee_pairing.json
    └── HAP characteristics → read / write TargetTemperature
```

The ecobee is controlled entirely over your **local network** using the HomeKit Accessory Protocol. No Alexa, no internet connection required for thermostat control once paired.

---

## Credits

- Hilo API endpoints and constants sourced from [dvd-dev/python-hilo](https://github.com/dvd-dev/python-hilo)
- HomeKit local control via [Jc2k/aiohomekit](https://github.com/Jc2k/aiohomekit)

