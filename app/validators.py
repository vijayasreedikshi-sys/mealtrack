"""Server-side input validation helpers."""
import re
from datetime import date, datetime, timedelta

EMAIL_RE = re.compile(r"^[A-Za-z0-9._%+-]+@[A-Za-z0-9.-]+\.[A-Za-z]{2,}$")
PHONE_RE = re.compile(r"^[0-9+\- ]{7,15}$")


def clean(value, max_len=255):
    return (value or "").strip()[:max_len]


def validate_name(name):
    if not name or len(name) < 2:
        return "Name must be at least 2 characters."
    if len(name) > 100:
        return "Name is too long."
    return None


def validate_email(email):
    if not email or not EMAIL_RE.match(email) or len(email) > 150:
        return "Enter a valid email address."
    return None


def validate_phone(phone, required=False):
    if not phone:
        return "Phone number is required." if required else None
    if not PHONE_RE.match(phone):
        return "Enter a valid phone number."
    return None


def validate_password(password):
    if not password or len(password) < 8:
        return "Password must be at least 8 characters."
    if not re.search(r"[A-Za-z]", password) or not re.search(r"\d", password):
        return "Password must contain both letters and numbers."
    return None


def parse_date(value, default=None):
    try:
        return datetime.strptime(value, "%Y-%m-%d").date() if value else default
    except (TypeError, ValueError):
        return default


def parse_int(value, default=None, minimum=None, maximum=None):
    try:
        number = int(str(value).strip())
    except (TypeError, ValueError):
        return default
    if minimum is not None and number < minimum:
        return default
    if maximum is not None and number > maximum:
        return default
    return number


def week_start(d: date) -> date:
    return d - timedelta(days=d.weekday())
