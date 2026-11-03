"""Customer module: dashboard, weekly schedule, count submission, history."""
from collections import OrderedDict
from datetime import date, datetime, timedelta

from flask import Blueprint, current_app, flash, g, redirect, render_template, request, url_for

from ..auth_utils import guard_blueprint
from ..db import query_all, query_one, transaction
from ..services import notifications
from ..services.cutoff import count_status
from ..validators import parse_date, parse_int, week_start

bp = Blueprint("customer", __name__)
guard_blueprint(bp, "customer")


def _meal_types():
    return query_all("SELECT id, meal_name, sort_order FROM meal_types ORDER BY sort_order")


def _my_schedule(start, end, customer_id):
    """Published schedule rows in a date range joined with this customer's counts
    and last week's quantity for the same meal (for 'copy last week')."""
    rows = query_all(
        "SELECT s.id, s.schedule_date, s.cutoff_time, s.notes, mt.id AS meal_type_id, mt.meal_name, "
        "       mt.sort_order, f.food_name, f.description, f.unit, f.price, "
        "       c.quantity, c.submission_status, c.submitted_at, c.updated_at, "
        "       lw.quantity AS last_week_qty "
        "FROM food_schedule s "
        "JOIN meal_types mt ON mt.id = s.meal_type_id "
        "JOIN food_items f  ON f.id = s.food_item_id "
        "LEFT JOIN customer_meal_counts c ON c.food_schedule_id = s.id AND c.customer_id = %s "
        "LEFT JOIN food_schedule s2 ON s2.schedule_date = DATE_SUB(s.schedule_date, INTERVAL 7 DAY) "
        "     AND s2.meal_type_id = s.meal_type_id "
        "LEFT JOIN customer_meal_counts lw ON lw.food_schedule_id = s2.id AND lw.customer_id = %s "
        "     AND lw.submission_status IN ('submitted','locked') "
        "WHERE s.is_published = 1 AND s.schedule_date BETWEEN %s AND %s "
        "ORDER BY s.schedule_date, mt.sort_order",
        (customer_id, customer_id, start, end),
    )
    now = datetime.now()
    for r in rows:
        r["status"] = count_status(r["submission_status"], r["cutoff_time"], now)
        r["is_open"] = r["cutoff_time"] > now
        r["has_value"] = r["submission_status"] in ("submitted", "locked")
    return rows


def _group_by_date(rows):
    grouped = OrderedDict()
    for r in rows:
        grouped.setdefault(r["schedule_date"], []).append(r)
    return grouped


@bp.route("/")
def dashboard():
    uid = g.user["id"]
    today = date.today()
    ws = week_start(today)
    today_rows = _my_schedule(today, today, uid)
    week_rows = _my_schedule(ws, ws + timedelta(days=6), uid)

    week_total_meals = len(week_rows)
    week_submitted = sum(1 for r in week_rows if r["has_value"])
    week_portions = sum(int(r["quantity"] or 0) for r in week_rows if r["has_value"])
    open_unsubmitted = sum(1 for r in week_rows if r["is_open"] and not r["has_value"])

    next_deadline = query_one(
        "SELECT s.id, s.schedule_date, s.cutoff_time, mt.meal_name, f.food_name, c.submission_status "
        "FROM food_schedule s JOIN meal_types mt ON mt.id = s.meal_type_id "
        "JOIN food_items f ON f.id = s.food_item_id "
        "LEFT JOIN customer_meal_counts c ON c.food_schedule_id = s.id AND c.customer_id = %s "
        "WHERE s.is_published = 1 AND s.cutoff_time > %s "
        "ORDER BY s.cutoff_time LIMIT 1",
        (uid, datetime.now()),
    )
    upcoming_pending = query_all(
        "SELECT s.schedule_date, s.cutoff_time, mt.meal_name, f.food_name "
        "FROM food_schedule s JOIN meal_types mt ON mt.id = s.meal_type_id "
        "JOIN food_items f ON f.id = s.food_item_id "
        "LEFT JOIN customer_meal_counts c ON c.food_schedule_id = s.id AND c.customer_id = %s "
        "     AND c.submission_status IN ('submitted','locked') "
        "WHERE s.is_published = 1 AND s.cutoff_time > %s AND c.id IS NULL "
        "ORDER BY s.cutoff_time LIMIT 6",
        (uid, datetime.now()),
    )
    return render_template(
        "customer/dashboard.html", today_rows=today_rows, week_total_meals=week_total_meals,
        week_submitted=week_submitted, week_portions=week_portions,
        open_unsubmitted=open_unsubmitted, next_deadline=next_deadline,
        upcoming_pending=upcoming_pending, week_start=ws,
    )


@bp.route("/schedule")
def schedule():
    ws = week_start(parse_date(request.args.get("week"), date.today()))
    we = ws + timedelta(days=6)
    rows = _my_schedule(ws, we, g.user["id"])
    grid = OrderedDict(((ws + timedelta(days=i)), {}) for i in range(7))
    for r in rows:
        grid[r["schedule_date"]][r["meal_name"]] = r
    return render_template(
        "customer/schedule.html", grid=grid, meal_types=_meal_types(), week_start=ws, week_end=we,
        prev_week=ws - timedelta(days=7), next_week=ws + timedelta(days=7),
    )


def _default_submit_date(uid):
    row = query_one(
        "SELECT MIN(s.schedule_date) AS d FROM food_schedule s "
        "LEFT JOIN customer_meal_counts c ON c.food_schedule_id = s.id AND c.customer_id = %s "
        "     AND c.submission_status IN ('submitted','locked') "
        "WHERE s.is_published = 1 AND s.cutoff_time > %s AND c.id IS NULL",
        (uid, datetime.now()),
    )
    return (row and row["d"]) or date.today()


@bp.route("/submit", methods=["GET", "POST"])
def submit():
    uid = g.user["id"]
    max_qty = current_app.config["MAX_QTY_PER_MEAL"]

    if request.method == "POST":
        view = "week" if request.form.get("view") == "week" else "day"
        back_date = parse_date(request.form.get("date"), date.today())
        now = datetime.now()
        submitted, unchanged, rejected, invalid = [], 0, 0, 0

        entries = []
        for key, raw in request.form.items():
            if not key.startswith("qty_"):
                continue
            schedule_id = parse_int(key[4:], minimum=1)
            raw = (raw or "").strip()
            if schedule_id is None or raw == "":
                continue  # blank = not submitted (stays pending)
            qty = parse_int(raw, minimum=0, maximum=max_qty)
            if qty is None:
                invalid += 1
                continue
            entries.append((schedule_id, qty))

        with transaction() as cur:
            for schedule_id, qty in entries:
                # Lock the schedule row and re-check the cutoff on the server.
                cur.execute(
                    "SELECT s.id, s.schedule_date, s.cutoff_time, mt.meal_name FROM food_schedule s "
                    "JOIN meal_types mt ON mt.id = s.meal_type_id "
                    "WHERE s.id=%s AND s.is_published=1 FOR UPDATE",
                    (schedule_id,),
                )
                sched = cur.fetchone()
                if not sched or sched["cutoff_time"] <= now:
                    rejected += 1
                    continue
                cur.execute(
                    "SELECT id, quantity, submission_status FROM customer_meal_counts "
                    "WHERE customer_id=%s AND food_schedule_id=%s FOR UPDATE",
                    (uid, schedule_id),
                )
                existing = cur.fetchone()
                if existing is None:
                    cur.execute(
                        "INSERT INTO customer_meal_counts (customer_id, food_schedule_id, quantity, "
                        "submission_status, submitted_at, updated_at) VALUES (%s,%s,%s,'submitted',%s,%s)",
                        (uid, schedule_id, qty, now, now),
                    )
                    cur.execute(
                        "INSERT INTO count_change_log (count_id, old_qty, new_qty, changed_at) "
                        "VALUES (%s,NULL,%s,%s)", (cur.lastrowid, qty, now),
                    )
                elif existing["submission_status"] in ("locked", "cancelled"):
                    rejected += 1
                    continue
                elif existing["submission_status"] == "submitted" and existing["quantity"] == qty:
                    unchanged += 1
                    continue
                else:
                    cur.execute(
                        "UPDATE customer_meal_counts SET quantity=%s, submission_status='submitted', "
                        "submitted_at=COALESCE(submitted_at, %s), updated_at=%s WHERE id=%s",
                        (qty, now, now, existing["id"]),
                    )
                    cur.execute(
                        "INSERT INTO count_change_log (count_id, old_qty, new_qty, changed_at) "
                        "VALUES (%s,%s,%s,%s)", (existing["id"], existing["quantity"], qty, now),
                    )
                submitted.append((sched, qty))

        if submitted:
            summary = ", ".join(
                f"{s['schedule_date'].strftime('%a %d %b')} {s['meal_name']}: {q}" for s, q in submitted[:6])
            if len(submitted) > 6:
                summary += f" and {len(submitted) - 6} more"
            notifications.notify(uid, f"Food count saved – {summary}.",
                                 url_for("customer.submissions"))
            flash(f"Saved {len(submitted)} meal count(s). You can edit them until each cutoff.", "success")
        if unchanged and not submitted:
            flash("No changes – your counts were already submitted.", "info")
        if rejected:
            flash(f"{rejected} meal(s) were not saved because the cutoff time has passed.", "warning")
        if invalid:
            flash(f"{invalid} value(s) were invalid. Enter a whole number from 0 to {max_qty}.", "danger")
        if not (submitted or unchanged or rejected or invalid):
            flash("Enter a quantity (0 if you don't need the meal) for at least one meal.", "info")
        return redirect(url_for("customer.submit", date=back_date.isoformat(), view=view))

    view = "week" if request.args.get("view") == "week" else "day"
    selected = parse_date(request.args.get("date")) or _default_submit_date(uid)
    if view == "week":
        start = week_start(selected)
        end = start + timedelta(days=6)
    else:
        start = end = selected
    rows = _my_schedule(start, end, uid)
    grouped = _group_by_date(rows)
    any_open = any(r["is_open"] for r in rows)
    step = 7 if view == "week" else 1
    return render_template(
        "customer/submit.html", grouped=grouped, view=view, selected=selected, start=start, end=end,
        any_open=any_open, max_qty=max_qty,
        prev_date=selected - timedelta(days=step), next_date=selected + timedelta(days=step),
    )


@bp.route("/submissions")
def submissions():
    today = date.today()
    end = parse_date(request.args.get("end"), today + timedelta(days=13))
    rows = _my_schedule(today, end, g.user["id"])
    counts = {k: 0 for k in ("pending", "submitted", "locked", "missed")}
    for r in rows:
        counts[r["status"]] = counts.get(r["status"], 0) + 1
    return render_template("customer/submissions.html", grouped=_group_by_date(rows),
                           counts=counts, end=end, today=today)


@bp.route("/history")
def history():
    today = date.today()
    end = parse_date(request.args.get("end"), today)
    start = parse_date(request.args.get("start"), end - timedelta(days=29))
    if start > end:
        start, end = end, start
    meal_filter = parse_int(request.args.get("meal"), minimum=1)

    sql = (
        "SELECT s.schedule_date, s.cutoff_time, mt.meal_name, mt.sort_order, f.food_name, "
        "       c.quantity, c.submission_status, c.submitted_at, c.updated_at, "
        "       (SELECT COUNT(*) FROM count_change_log l WHERE l.count_id = c.id) AS changes "
        "FROM food_schedule s JOIN meal_types mt ON mt.id = s.meal_type_id "
        "JOIN food_items f ON f.id = s.food_item_id "
        "LEFT JOIN customer_meal_counts c ON c.food_schedule_id = s.id AND c.customer_id = %s "
        "WHERE s.is_published = 1 AND s.schedule_date BETWEEN %s AND %s AND s.cutoff_time <= %s "
    )
    params = [g.user["id"], start, end, datetime.now()]
    if meal_filter:
        sql += "AND s.meal_type_id = %s "
        params.append(meal_filter)
    sql += "ORDER BY s.schedule_date DESC, mt.sort_order"
    rows = query_all(sql, params)
    for r in rows:
        r["status"] = count_status(r["submission_status"], r["cutoff_time"])

    meal_totals = OrderedDict((m["meal_name"], 0) for m in _meal_types())
    for r in rows:
        if r["status"] == "locked":
            meal_totals[r["meal_name"]] += int(r["quantity"] or 0)
    missed = sum(1 for r in rows if r["status"] == "missed")

    monthly = query_all(
        "SELECT DATE_FORMAT(s.schedule_date, '%%Y-%%m') AS ym, "
        "       DATE_FORMAT(MIN(s.schedule_date), '%%b %%Y') AS label, "
        "       SUM(CASE WHEN mt.meal_name='Breakfast' THEN c.quantity ELSE 0 END) AS breakfast, "
        "       SUM(CASE WHEN mt.meal_name='Lunch' THEN c.quantity ELSE 0 END) AS lunch, "
        "       SUM(CASE WHEN mt.meal_name='Dinner' THEN c.quantity ELSE 0 END) AS dinner, "
        "       SUM(c.quantity) AS total "
        "FROM customer_meal_counts c JOIN food_schedule s ON s.id = c.food_schedule_id "
        "JOIN meal_types mt ON mt.id = s.meal_type_id "
        "WHERE c.customer_id = %s AND c.submission_status IN ('submitted','locked') "
        "  AND s.schedule_date <= %s "
        "GROUP BY ym ORDER BY ym DESC LIMIT 6",
        (g.user["id"], today),
    )
    return render_template(
        "customer/history.html", rows=rows, start=start, end=end, meal_filter=meal_filter,
        meal_types=_meal_types(), meal_totals=meal_totals, missed=missed, monthly=monthly,
    )
