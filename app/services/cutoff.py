"""Cutoff rules, status calculation and the periodic lock/notify sweep."""
import threading
import time as _time
from datetime import datetime, timedelta

from flask import current_app, request

from ..db import execute, get_db


def to_time(value):
    """PyMySQL returns MySQL TIME columns as timedelta."""
    if isinstance(value, timedelta):
        return (datetime.min + value).time()
    return value


def compute_cutoff(schedule_date, meal_type):
    """Default cutoff for a meal on a date, from meal_types settings."""
    day = schedule_date + timedelta(days=int(meal_type["default_cutoff_day_offset"]))
    return datetime.combine(day, to_time(meal_type["default_cutoff_time"]))


def is_open(cutoff_time, now=None):
    return (now or datetime.now()) < cutoff_time


def count_status(submission_status, cutoff_time, now=None):
    """Status shown to users:
    submitted -> customer submitted and can still edit
    locked    -> submitted and the cutoff has passed
    pending   -> not yet submitted, still open
    missed    -> not submitted and the cutoff has passed
    """
    now = now or datetime.now()
    closed = cutoff_time <= now
    if submission_status == "cancelled":
        return "cancelled"
    if submission_status == "locked" or (submission_status == "submitted" and closed):
        return "locked"
    if submission_status == "submitted":
        return "submitted"
    return "missed" if closed else "pending"


# ---------------------------------------------------------------------------
# Sweep: lock counts after cutoff, send reminders and final-count notices.
# Runs at most once per SWEEP_INTERVAL_SECONDS, triggered by normal requests,
# so no separate scheduler/service is required on Windows.
# ---------------------------------------------------------------------------
_sweep_lock = threading.Lock()
_last_sweep = {"at": 0.0}


def run_sweep(now=None):
    now = now or datetime.now()
    reminder_window = now + timedelta(hours=current_app.config["REMINDER_HOURS_BEFORE_CUTOFF"])

    # 1. Lock submitted counts whose cutoff has passed.
    execute(
        "UPDATE customer_meal_counts c JOIN food_schedule s ON s.id = c.food_schedule_id "
        "SET c.submission_status='locked' "
        "WHERE c.submission_status='submitted' AND s.cutoff_time <= %s",
        (now,), commit=False,
    )

    # 2. Deadline reminders for active customers who have not submitted yet.
    execute(
        "INSERT IGNORE INTO notifications (user_id, message, link, dedupe_key, created_at) "
        "SELECT u.id, "
        "  CONCAT('Reminder: submit your ', mt.meal_name, ' count for ', "
        "         DATE_FORMAT(s.schedule_date, '%%a %%d %%b'), ' before ', "
        "         DATE_FORMAT(s.cutoff_time, '%%h:%%i %%p'), '.'), "
        "  CONCAT('/customer/submit?date=', DATE_FORMAT(s.schedule_date, '%%Y-%%m-%%d')), "
        "  CONCAT('remind:', s.id, ':', u.id), %s "
        "FROM food_schedule s "
        "JOIN meal_types mt ON mt.id = s.meal_type_id "
        "JOIN users u ON u.role='customer' AND u.status='active' "
        "LEFT JOIN customer_meal_counts c ON c.food_schedule_id = s.id AND c.customer_id = u.id "
        "     AND c.submission_status IN ('submitted','locked') "
        "WHERE s.is_published=1 AND s.cutoff_time > %s AND s.cutoff_time <= %s AND c.id IS NULL",
        (now, now, reminder_window), commit=False,
    )

    # 3. Final cooking count to assigned sellers once a meal closes (last 24h only).
    execute(
        "INSERT IGNORE INTO notifications (user_id, message, link, dedupe_key, created_at) "
        "SELECT a.seller_id, "
        "  CONCAT('Final count for ', mt.meal_name, ' (', f.food_name, ') on ', "
        "         DATE_FORMAT(s.schedule_date, '%%a %%d %%b'), ': ', COALESCE(t.total, 0), ' portions.'), "
        "  CONCAT('/seller/?date=', DATE_FORMAT(s.schedule_date, '%%Y-%%m-%%d')), "
        "  CONCAT('final:', s.id, ':', a.seller_id), %s "
        "FROM seller_assignments a "
        "JOIN food_schedule s ON s.id = a.food_schedule_id "
        "JOIN meal_types mt ON mt.id = s.meal_type_id "
        "JOIN food_items f ON f.id = s.food_item_id "
        "LEFT JOIN ( "
        "   SELECT c.food_schedule_id, SUM(c.quantity) AS total "
        "   FROM customer_meal_counts c JOIN users u ON u.id = c.customer_id AND u.status='active' "
        "   WHERE c.submission_status IN ('submitted','locked') GROUP BY c.food_schedule_id "
        ") t ON t.food_schedule_id = s.id "
        "WHERE s.is_published=1 AND s.cutoff_time <= %s AND s.cutoff_time > %s",
        (now, now, now - timedelta(hours=24)), commit=False,
    )
    get_db().commit()


def maybe_run_sweep():
    """before_request hook (throttled)."""
    if request.endpoint in (None, "static"):
        return
    interval = current_app.config["SWEEP_INTERVAL_SECONDS"]
    if _time.monotonic() - _last_sweep["at"] < interval:
        return
    if not _sweep_lock.acquire(blocking=False):
        return
    try:
        _last_sweep["at"] = _time.monotonic()
        run_sweep()
    except Exception:  # never break a page because of the background sweep
        current_app.logger.exception("Lock/notification sweep failed")
        get_db().rollback()
    finally:
        _sweep_lock.release()
