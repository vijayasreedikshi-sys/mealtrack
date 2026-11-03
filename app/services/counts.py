"""Automatic food count aggregation – the single source of truth for totals.

Rules applied in every query:
  * only submission_status IN ('submitted','locked') is counted
    (pending / cancelled / never-submitted are excluded)
  * quantity 0 is a valid submission (it adds 0 but counts as submitted)
  * only ACTIVE customers are counted
  * UNIQUE(customer_id, food_schedule_id) means an edited submission is a
    single row, so it can never be double-counted
"""
from datetime import date

from ..db import query_all, query_one, query_value

VALID_STATUSES = "('submitted','locked')"

# LEFT JOIN fragment: valid counts of active customers for schedule alias "s"
_VALID_COUNTS_JOIN = (
    "LEFT JOIN (customer_meal_counts c "
    "           JOIN users cu ON cu.id = c.customer_id AND cu.status='active' AND cu.role='customer') "
    f"      ON c.food_schedule_id = s.id AND c.submission_status IN {VALID_STATUSES} "
)


def schedule_totals(start: date, end: date, meal_type_id=None, seller_id=None, published_only=True):
    """One row per scheduled meal with total required quantity.

    Columns: schedule_id, schedule_date, cutoff_time, meal_type_id, meal_name,
    sort_order, food_name, unit, total_qty, submitters, zero_count
    """
    sql = (
        "SELECT s.id AS schedule_id, s.schedule_date, s.cutoff_time, s.is_published, "
        "       mt.id AS meal_type_id, mt.meal_name, mt.sort_order, f.food_name, f.unit, "
        "       COALESCE(SUM(c.quantity), 0) AS total_qty, "
        "       COUNT(c.id) AS submitters, "
        "       COALESCE(SUM(c.quantity = 0), 0) AS zero_count "
        "FROM food_schedule s "
        "JOIN meal_types mt ON mt.id = s.meal_type_id "
        "JOIN food_items f ON f.id = s.food_item_id "
    )
    params = []
    if seller_id is not None:
        sql += "JOIN seller_assignments sa ON sa.food_schedule_id = s.id AND sa.seller_id = %s "
        params.append(seller_id)
    sql += _VALID_COUNTS_JOIN + "WHERE s.schedule_date BETWEEN %s AND %s "
    params += [start, end]
    if published_only:
        sql += "AND s.is_published = 1 "
    if meal_type_id:
        sql += "AND s.meal_type_id = %s "
        params.append(meal_type_id)
    sql += (
        "GROUP BY s.id, s.schedule_date, s.cutoff_time, s.is_published, mt.id, mt.meal_name, "
        "         mt.sort_order, f.food_name, f.unit "
        "ORDER BY s.schedule_date, mt.sort_order"
    )
    rows = query_all(sql, params)
    for r in rows:
        r["total_qty"] = int(r["total_qty"])
        r["zero_count"] = int(r["zero_count"])
    return rows


def meal_totals_for_date(d: date, seller_id=None):
    """{meal_name: total_qty} for one date plus 'Total'."""
    rows = schedule_totals(d, d, seller_id=seller_id)
    totals = {r["meal_name"]: r["total_qty"] for r in rows}
    totals["Total"] = sum(totals.values())
    return totals, rows


def schedule_total(schedule_id):
    return int(query_value(
        "SELECT COALESCE(SUM(c.quantity),0) FROM customer_meal_counts c "
        "JOIN users u ON u.id = c.customer_id AND u.status='active' AND u.role='customer' "
        f"WHERE c.food_schedule_id = %s AND c.submission_status IN {VALID_STATUSES}",
        (schedule_id,), 0))


def customer_breakdown(schedule_id):
    """Customer-wise quantities for one scheduled meal (valid submissions only)."""
    return query_all(
        "SELECT u.id, u.name, u.room_no, c.quantity, c.submission_status, c.updated_at "
        "FROM customer_meal_counts c "
        "JOIN users u ON u.id = c.customer_id AND u.status='active' AND u.role='customer' "
        f"WHERE c.food_schedule_id = %s AND c.submission_status IN {VALID_STATUSES} "
        "ORDER BY u.room_no, u.name",
        (schedule_id,),
    )


def active_customer_count():
    return int(query_value(
        "SELECT COUNT(*) FROM users WHERE role='customer' AND status='active'", None, 0))


def pending_customers(schedule_id):
    """Active customers who have NOT submitted a valid count for this meal."""
    return query_all(
        "SELECT u.id, u.name, u.room_no, u.phone FROM users u "
        "LEFT JOIN customer_meal_counts c ON c.customer_id = u.id AND c.food_schedule_id = %s "
        f"     AND c.submission_status IN {VALID_STATUSES} "
        "WHERE u.role='customer' AND u.status='active' AND c.id IS NULL "
        "ORDER BY u.room_no, u.name",
        (schedule_id,),
    )


def pending_summary_for_date(d: date):
    """Pending (not submitted) customer count per meal for a date."""
    active = active_customer_count()
    rows = schedule_totals(d, d)
    return [
        {**r, "pending": max(active - int(r["submitters"]), 0), "active_customers": active}
        for r in rows
    ]


def submission_counts(start: date, end: date):
    """Number of valid submissions vs. expected (active customers × published meals)."""
    row = query_one(
        "SELECT COUNT(DISTINCT s.id) AS meals, COUNT(c.id) AS submitted "
        "FROM food_schedule s " + _VALID_COUNTS_JOIN +
        "WHERE s.is_published=1 AND s.schedule_date BETWEEN %s AND %s",
        (start, end),
    )
    active = active_customer_count()
    meals = int(row["meals"] or 0)
    submitted = int(row["submitted"] or 0)
    expected = meals * active
    return {"meals": meals, "submitted": submitted, "expected": expected,
            "pending": max(expected - submitted, 0), "active_customers": active}
