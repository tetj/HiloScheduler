#!/usr/bin/env python3
"""
Hilo Peak Event Scheduler — Direct ecobee control via HomeKit
--------------------------------------------------------------
Polls the Hilo API for upcoming peak events and controls your ecobee
thermostat directly over your local network via HomeKit (aiohomekit).
No Alexa, no Virtual Smart Home, no cloud middleman.

Temperature profile for each event:
  T-120 min  ->>  read & save current setpoint, set to TEMP_PREHEAT_LOW (24°C)
  T-30  min  ->>  boost to TEMP_PREHEAT_HIGH (25°C)
  T+0        ->>  reduce to TEMP_REDUCTION (16°C)
  T+240 min  ->>  restore saved setpoint

FIRST TIME SETUP — pair with your ecobee:
  python3 hilo_scheduler.py --pair

NORMAL OPERATION (loops, re-checks every 4 hours by default):
  python3 hilo_scheduler.py

SINGLE CHECK (run once and exit):
  python3 hilo_scheduler.py --once

TEST event cycle without waiting for a real event:
  python3 hilo_scheduler.py --test-event

INSTALL DEPENDENCIES:
  pip install aiohomekit aiohttp python-dotenv zeroconf

See README.md for full setup instructions.
"""

import asyncio
import json
import logging
import os
import sys
from datetime import datetime, timezone, timedelta
from pathlib import Path

import aiohttp
from dotenv import load_dotenv

load_dotenv()

# ---------------------------------------------------------------------------
# CONFIGURATION — edit here or set in a .env file
# ---------------------------------------------------------------------------

# Path to save HomeKit pairing credentials after --pair step
PAIRING_FILE = Path(os.getenv("PAIRING_FILE", "ecobee_pairing.json"))

# Temperature setpoints (°C)
TEMP_PREHEAT_LOW  = float(os.getenv("TEMP_PREHEAT_LOW",  "24.0"))  # first 90 min
TEMP_PREHEAT_HIGH = float(os.getenv("TEMP_PREHEAT_HIGH", "25.0"))  # last 30 min before event
TEMP_REDUCTION    = float(os.getenv("TEMP_REDUCTION",    "16.0"))  # during event
EVENT_DURATION_MIN = int(os.getenv("EVENT_DURATION_MIN", "240"))   # event length in minutes

# Preheat window: total minutes before event start to begin preheating
PREHEAT_TOTAL_MIN = int(os.getenv("PREHEAT_TOTAL_MIN", "120"))     # 2 hours total
PREHEAT_HIGH_MIN  = int(os.getenv("PREHEAT_HIGH_MIN",  "30"))      # last 30 min at high temp

# How often to poll the Hilo API for new events when running in loop mode
CHECK_INTERVAL_HOURS = int(os.getenv("CHECK_INTERVAL_HOURS", "4"))

# ---------------------------------------------------------------------------
# Hilo API constants (public values from dvd-dev/python-hilo — same for all users)
# Override via environment variables if needed.
# ---------------------------------------------------------------------------

HILO_AUTH_BASE  = "https://connexion.hiloenergie.com/HiloDirectoryB2C.onmicrosoft.com/B2C_1A_Sign_In"
HILO_AUTH_URL   = f"{HILO_AUTH_BASE}/oauth2/v2.0/authorize"
HILO_TOKEN_URL  = f"{HILO_AUTH_BASE}/oauth2/v2.0/token"
HILO_API_BASE        = "https://api.hiloenergie.com/Automation/v1/api"
HILO_CHALLENGE_BASE  = "https://api.hiloenergie.com/challenge/v1/api"
HILO_GDSERVICE_BASE  = "https://api.hiloenergie.com/GDService/v1/api"
HILO_CLIENT_ID       = os.getenv("HILO_CLIENT_ID",       "1ca9f585-4a55-4085-8e30-9746a65fa561")
HILO_SUBSCRIPTION_KEY = os.getenv("HILO_SUBSCRIPTION_KEY", "20eeaedcb86945afa3fe792cea89b8bf")
HILO_SCOPE      = "openid https://HiloDirectoryB2C.onmicrosoft.com/hiloapis/user_impersonation offline_access"
HILO_REDIRECT   = "https://my.home-assistant.io/redirect/oauth/"
HILO_TOKEN_FILE = Path(os.getenv("HILO_TOKEN_FILE", "hilo_tokens.json"))

# HomeKit characteristic IIDs for a thermostat (standard HAP spec)
# These are stable across all certified HomeKit thermostats including ecobee.
HAP_TARGET_TEMP   = 35   # TargetTemperature (iid varies — discovered at runtime)
HAP_CURRENT_TEMP  = 11   # CurrentTemperature

# ---------------------------------------------------------------------------
# Logging
# ---------------------------------------------------------------------------

logging.basicConfig(
    level=logging.INFO,
    format="%(asctime)s  %(levelname)-8s  %(message)s",
    datefmt="%Y-%m-%d %H:%M:%S",
)
log = logging.getLogger("hilo_scheduler")

# ---------------------------------------------------------------------------
# Hilo API helpers
# ---------------------------------------------------------------------------

def _pkce_pair() -> tuple[str, str]:
    import hashlib, base64, secrets
    verifier  = secrets.token_urlsafe(64)
    challenge = base64.urlsafe_b64encode(
        hashlib.sha256(verifier.encode()).digest()
    ).rstrip(b"=").decode()
    return verifier, challenge


def _load_tokens() -> dict:
    if HILO_TOKEN_FILE.exists():
        return json.loads(HILO_TOKEN_FILE.read_text())
    return {}


def _save_tokens(tokens: dict) -> None:
    HILO_TOKEN_FILE.write_text(json.dumps(tokens, indent=2))


async def _exchange_code(session: aiohttp.ClientSession, code: str, verifier: str) -> dict:
    async with session.post(HILO_TOKEN_URL, data={
        "grant_type":    "authorization_code",
        "client_id":     HILO_CLIENT_ID,
        "code":          code,
        "redirect_uri":  HILO_REDIRECT,
        "code_verifier": verifier,
    }) as resp:
        resp.raise_for_status()
        return await resp.json()


async def _refresh_tokens(session: aiohttp.ClientSession, refresh_token: str) -> dict:
    async with session.post(HILO_TOKEN_URL, data={
        "grant_type":    "refresh_token",
        "client_id":     HILO_CLIENT_ID,
        "refresh_token": refresh_token,
    }) as resp:
        resp.raise_for_status()
        return await resp.json()


async def get_hilo_token(session: aiohttp.ClientSession) -> str:
    import urllib.parse, webbrowser

    tokens = _load_tokens()

    # Try refresh token first
    if tokens.get("refresh_token"):
        try:
            tokens = await _refresh_tokens(session, tokens["refresh_token"])
            _save_tokens(tokens)
            log.info("Hilo authentication successful (refresh token).")
            return tokens["access_token"]
        except Exception as exc:
            log.warning(f"Refresh token failed ({exc}), re-authenticating via browser.")

    # Full PKCE browser flow
    import secrets as _secrets
    verifier, challenge = _pkce_pair()
    state = _secrets.token_urlsafe(16)
    params = {
        "client_id":             HILO_CLIENT_ID,
        "response_type":         "code",
        "scope":                 HILO_SCOPE,
        "redirect_uri":          HILO_REDIRECT,
        "state":                 state,
        "code_challenge":        challenge,
        "code_challenge_method": "S256",
    }
    auth_url = HILO_AUTH_URL + "?" + urllib.parse.urlencode(params)

    print("\n" + "="*60)
    print("Hilo login required. Your browser will open.")
    print("Log in, then you will land on a my.home-assistant.io page.")
    print("Copy the FULL URL from your browser address bar and paste it here.")
    print("(It will contain 'redirect/_change' or 'redirect/oauth' with a 'code=' in it)")
    print("="*60)
    webbrowser.open(auth_url)
    callback_url = input("Paste the full browser URL here: ").strip()

    # code may be directly in the URL or embedded in a 'redirect' query param
    # e.g. https://my.home-assistant.io/redirect/_change/?redirect=oauth%2F%3Fcode%3D...
    parsed = urllib.parse.urlparse(callback_url)
    qs = urllib.parse.parse_qs(parsed.query)
    code = qs.get("code", [None])[0]
    if not code:
        # try decoding the 'redirect' param (my.home-assistant.io _change URL)
        redirect_param = qs.get("redirect", [None])[0]
        if redirect_param:
            inner = urllib.parse.parse_qs(urllib.parse.urlparse(urllib.parse.unquote(redirect_param)).query)
            code = inner.get("code", [None])[0]
    if not code:
        raise RuntimeError(
            "No 'code' found in URL. Make sure you copy the full URL from the address bar after login."
        )

    tokens = await _exchange_code(session, code, verifier)
    _save_tokens(tokens)
    log.info("Hilo authentication successful (new login).")
    return tokens["access_token"]


async def get_location_id(session: aiohttp.ClientSession, token: str) -> int:
    headers = {"Authorization": f"Bearer {token}", "Ocp-Apim-Subscription-Key": HILO_SUBSCRIPTION_KEY}
    async with session.get(f"{HILO_API_BASE}/Locations", headers=headers) as resp:
        resp.raise_for_status()
        locations = await resp.json()
        if not locations:
            raise RuntimeError("No Hilo locations found on this account.")
        location_id = locations[0]["id"]
        log.info(f"Using Hilo location ID: {location_id}")
        return location_id


async def get_next_event(
    session: aiohttp.ClientSession, token: str, location_id: int
) -> "dict | None":
    """Return the next upcoming Hilo peak event, or None."""
    headers = {"Authorization": f"Bearer {token}", "Ocp-Apim-Subscription-Key": HILO_SUBSCRIPTION_KEY}
    url = f"{HILO_CHALLENGE_BASE}/Locations/{location_id}/Seasons"
    async with session.get(url, headers=headers) as resp:
        resp.raise_for_status()
        data = await resp.json()

    now = datetime.now(timezone.utc)
    upcoming = []
    seasons = data if isinstance(data, list) else [data]
    for season in seasons:
        for event in season.get("events", []):
            if event.get("status") != "Upcoming":
                continue
            start_str = event.get("startDateUtc") or event.get("startDateUTC") or event.get("startDate")
            if not start_str:
                continue
            start = parse_iso(start_str)
            end   = start + timedelta(minutes=EVENT_DURATION_MIN)
            if end > now:
                upcoming.append({**event, "startDateUTC": start_str, "endDateUTC": end.isoformat()})

    if not upcoming:
        return None
    upcoming.sort(key=lambda e: parse_iso(e["startDateUTC"]))
    return upcoming[0]

# ---------------------------------------------------------------------------
# HomeKit / aiohomekit helpers
# ---------------------------------------------------------------------------

def load_pairing_data() -> dict:
    if not PAIRING_FILE.exists():
        raise FileNotFoundError(
            f"Pairing file '{PAIRING_FILE}' not found. "
            "Run with --pair first: python3 hilo_scheduler.py --pair"
        )
    return json.loads(PAIRING_FILE.read_text())


async def get_ecobee_connection():
    """Return an authenticated aiohomekit pairing connection to the ecobee."""
    from aiohomekit.controller.ip import IpController
    from aiohomekit.characteristic_cache import CharacteristicCacheMemory
    from zeroconf.asyncio import AsyncZeroconf

    pairing_data = load_pairing_data()
    zeroconf = AsyncZeroconf()
    controller = IpController(char_cache=CharacteristicCacheMemory(), zeroconf_instance=zeroconf)
    pairing = controller.load_pairing(
        pairing_data["alias"], pairing_data["pairing"]
    )
    return pairing, controller, zeroconf


async def get_target_temp(pairing) -> float:
    """Read the current target temperature from the ecobee."""
    from aiohomekit.model.characteristics import CharacteristicsTypes

    # Discover accessories to find the correct aid/iid for TargetTemperature
    accessories = await pairing.list_accessories_and_characteristics()
    aid, iid = find_characteristic(accessories, "00000035-0000-1000-8000-0026BB765291")
    result = await pairing.get_characteristics([(aid, iid)])
    temp = result[(aid, iid)]["value"]
    log.info(f"Current ecobee target temperature: {temp}°C")
    return float(temp)


async def set_target_temp(pairing, temp: float) -> None:
    """Set the target temperature on the ecobee."""
    accessories = await pairing.list_accessories_and_characteristics()
    aid, iid = find_characteristic(accessories, "00000035-0000-1000-8000-0026BB765291")
    await pairing.put_characteristics([(aid, iid, temp)])
    log.info(f"ecobee target temperature set to {temp}°C")


def find_characteristic(accessories: list, uuid: str) -> tuple[int, int]:
    """Walk the accessory/service/characteristic tree to find a characteristic by UUID."""
    uuid = uuid.upper()
    for accessory in accessories:
        for service in accessory.get("services", []):
            for char in service.get("characteristics", []):
                char_type = char.get("type", "").upper()
                # HAP UUIDs may be short (e.g. "35") or full
                if char_type == uuid or char_type.lstrip("0") == uuid.lstrip("0"):
                    return accessory["aid"], char["iid"]
    raise RuntimeError(
        f"Characteristic {uuid} not found on ecobee. "
        "Try running --pair again to refresh the accessory profile."
    )

# ---------------------------------------------------------------------------
# Pairing flow (run once with --pair)
# ---------------------------------------------------------------------------

async def do_pair(pin: str | None = None, ip: str | None = None) -> None:
    """Pair with the ecobee directly by IP, saving credentials to PAIRING_FILE."""

    print("\n=== ecobee HomeKit Pairing ===")
    print("Make sure HomeKit is enabled on the ecobee:")
    print("  Thermostat screen ->> Menu ->> Settings ->> HomeKit ->> Enable")
    print(">>> Once enabled, your ecobee screen will show a PIN. <<<\n")

    from aiohomekit.controller.ip import IpDiscovery, IpController
    from aiohomekit.zeroconf import HomeKitService
    from aiohomekit.characteristic_cache import CharacteristicCacheMemory
    from zeroconf.asyncio import AsyncZeroconf, AsyncServiceBrowser, AsyncServiceInfo
    from zeroconf import ServiceStateChange

    zeroconf = AsyncZeroconf()

    try:
        # Discover the ecobee via mDNS so we get the real port, device ID and flags
        print(f"Discovering HomeKit device{f' at {ip}' if ip else ''} via mDNS...")
        found_event = asyncio.Event()
        description: HomeKitService | None = None

        def on_service_state_change(zeroconf, service_type, name, state_change):
            if state_change != ServiceStateChange.Added:
                return
            async def _fetch():
                nonlocal description
                info = AsyncServiceInfo(service_type, name)
                if not await info.async_request(zeroconf, 3000):
                    return
                addresses = info.parsed_addresses()
                if ip is not None and ip not in addresses:
                    return
                try:
                    description = HomeKitService.from_service_info(info)
                    print(f"Found: {description.name} at {description.address}:{description.port}")
                    found_event.set()
                except ValueError:
                    pass
            asyncio.ensure_future(_fetch())

        browser = AsyncServiceBrowser(
            zeroconf.zeroconf, "_hap._tcp.local.", handlers=[on_service_state_change]
        )

        try:
            await asyncio.wait_for(found_event.wait(), timeout=15)
        except asyncio.TimeoutError:
            print(f"No HomeKit device found{f' at {ip}' if ip else ''} within 15 seconds.")
            return
        finally:
            await browser.async_cancel()

        ip_controller = IpController(char_cache=CharacteristicCacheMemory(), zeroconf_instance=zeroconf)
        device = IpDiscovery(ip_controller, description)

        print(">>> Your ecobee screen should be showing a PIN. <<<")
        if pin is None:
            pin = input("Enter the PIN shown on the ecobee screen (format: XXX-XX-XXX): ").strip()
        pin = pin.replace("-", "")
        if len(pin) == 8 and pin.isdigit():
            pin = f"{pin[:3]}-{pin[3:5]}-{pin[5:]}"
        print(f"Using PIN: {pin}")

        alias = "ecobee"
        finish_pairing = await device.async_start_pairing(alias)
        pairing = await finish_pairing(pin)

        pairing_data = {
            "alias": alias,
            "pairing": pairing.pairing_data,
        }
        PAIRING_FILE.write_text(json.dumps(pairing_data, indent=2))
        print(f"\nOK Paired successfully! Credentials saved to '{PAIRING_FILE}'.")
        print("You can now run the scheduler:")
        print("  python3 hilo_scheduler.py\n")

    except Exception as exc:
        print(f"Pairing failed: {exc}")
        raise

    finally:
        await zeroconf.async_close()

# ---------------------------------------------------------------------------
# Utilities
# ---------------------------------------------------------------------------

def parse_iso(s: str) -> datetime:
    s = s.replace("Z", "+00:00")
    dt = datetime.fromisoformat(s)
    if dt.tzinfo is None:
        dt = dt.replace(tzinfo=timezone.utc)
    return dt


async def sleep_until(target: datetime, label: str) -> None:
    now = datetime.now(timezone.utc)
    delta = (target - now).total_seconds()
    if delta <= 0:
        log.info(f"[{label}] Time already passed — acting immediately.")
        return
    log.info(
        f"[{label}] Waiting {delta/60:.1f} min "
        f"until {target.strftime('%Y-%m-%d %H:%M UTC')}..."
    )
    await asyncio.sleep(delta)

# ---------------------------------------------------------------------------
# Main event scheduling logic
# ---------------------------------------------------------------------------

async def handle_event(event: dict) -> None:
    """Execute the full temperature sequence for one Hilo peak event."""
    start_str = event.get("startDateUTC") or event.get("startDate")
    event_start = parse_iso(start_str)
    event_end   = event_start + timedelta(minutes=EVENT_DURATION_MIN)

    preheat_low_start  = event_start - timedelta(minutes=PREHEAT_TOTAL_MIN)
    preheat_high_start = event_start - timedelta(minutes=PREHEAT_HIGH_MIN)

    now = datetime.now(timezone.utc)

    log.info(
        f"Event schedule:\n"
        f"  {preheat_low_start.strftime('%H:%M UTC')}  ->>  Preheat low  ({TEMP_PREHEAT_LOW}°C)\n"
        f"  {preheat_high_start.strftime('%H:%M UTC')}  ->>  Preheat high ({TEMP_PREHEAT_HIGH}°C)\n"
        f"  {event_start.strftime('%H:%M UTC')}  ->>  Reduction    ({TEMP_REDUCTION}°C)\n"
        f"  {event_end.strftime('%H:%M UTC')}  ->>  Recovery     (restore saved)"
    )

    # Connect to ecobee once and reuse the connection for all phases
    pairing, controller, zeroconf = await get_ecobee_connection()

    try:
        # --- Phase 1: Preheat low (24°C) ---
        if preheat_low_start > now:
            await sleep_until(preheat_low_start, "PREHEAT LOW")
        else:
            log.info("[PREHEAT LOW] Already in preheat window.")

        # Save current setpoint before we touch anything
        saved_temp = await get_target_temp(pairing)
        log.info(f"Saved current setpoint: {saved_temp}°C (will restore after event)")
        await set_target_temp(pairing, TEMP_PREHEAT_LOW)

        # --- Phase 2: Preheat high (25°C, last 30 min) ---
        if preheat_high_start > datetime.now(timezone.utc):
            await sleep_until(preheat_high_start, "PREHEAT HIGH")
            await set_target_temp(pairing, TEMP_PREHEAT_HIGH)
        else:
            log.info("[PREHEAT HIGH] Skipped (already past this window).")

        # --- Phase 3: Reduction (16°C, event start) ---
        if event_start > datetime.now(timezone.utc):
            await sleep_until(event_start, "REDUCTION")
        await set_target_temp(pairing, TEMP_REDUCTION)

        # --- Phase 4: Recovery (restore saved temp, event end) ---
        await sleep_until(event_end, "RECOVERY")
        await set_target_temp(pairing, saved_temp)
        log.info(f"Event complete. Temperature restored to {saved_temp}°C.")

    finally:
        await zeroconf.async_close()


async def run_once() -> None:
    """Single pass: authenticate with Hilo, find next event, schedule it."""
    async with aiohttp.ClientSession() as session:
        token      = await get_hilo_token(session)
        loc_id     = await get_location_id(session, token)
        event      = await get_next_event(session, token, loc_id)

    if not event:
        log.info("No upcoming Hilo events found.")
        return

    start_str   = event.get("startDateUTC") or event.get("startDate")
    event_start = parse_iso(start_str)
    log.info(f"Next Hilo event: {event_start.strftime('%Y-%m-%d %H:%M UTC')}")

    await handle_event(event)


async def run_loop() -> None:
    """Run continuously, re-checking for events every CHECK_INTERVAL_HOURS hours."""
    while True:
        log.info("=== Hilo event check ===")
        try:
            await run_once()
        except FileNotFoundError as e:
            log.error(str(e))
            sys.exit(1)
        except Exception as e:
            log.error(f"Unexpected error: {e}", exc_info=True)

        next_check = datetime.now() + timedelta(hours=CHECK_INTERVAL_HOURS)
        log.info(
            f"Next check at {next_check.strftime('%Y-%m-%d %H:%M')} "
            f"(sleeping {CHECK_INTERVAL_HOURS}h)"
        )
        await asyncio.sleep(CHECK_INTERVAL_HOURS * 3600)

# ---------------------------------------------------------------------------
# Entry point
# ---------------------------------------------------------------------------

def _get_arg(flag: str) -> str | None:
    """Return the value after a CLI flag, or None if not present."""
    if flag in sys.argv:
        idx = sys.argv.index(flag)
        if idx + 1 < len(sys.argv):
            return sys.argv[idx + 1]
    return None

if __name__ == "__main__":
    if "--pair" in sys.argv:
        _pin_arg = _get_arg("--pin")
        _ip_arg = _get_arg("--ip")
        asyncio.run(do_pair(pin=_pin_arg, ip=_ip_arg))
    elif "--test-event" in sys.argv:
        # Fake a peak event starting 70 seconds from now with 10-second phases
        # so the full preheat → reduction → recovery cycle finishes in ~40 seconds.
        _now = datetime.now(timezone.utc)
        _fake_start = _now + timedelta(seconds=20)   # reduction kicks in at T+20s
        _fake_event = {"startDateUTC": _fake_start.strftime("%Y-%m-%dT%H:%M:%SZ")}
        # Override timing constants for the test run
        PREHEAT_TOTAL_MIN = 1 / 6      # 10 seconds before event
        PREHEAT_HIGH_MIN  = 1 / 12     # 5 seconds before event
        EVENT_DURATION_MIN = 1 / 6     # event lasts 10 seconds
        log.info("--- TEST EVENT MODE ---")
        log.info(f"Fake event start: {_fake_start.strftime('%H:%M:%S UTC')}")
        asyncio.run(handle_event(_fake_event))
    elif "--once" in sys.argv:
        asyncio.run(run_once())
    else:
        asyncio.run(run_loop())