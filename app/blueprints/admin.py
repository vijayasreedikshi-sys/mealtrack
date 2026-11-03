"""Admin module: dashboard and food item (menu) management."""
from datetime import date
from decimal import Decimal, InvalidOperation

from flask import Blueprint, abort, flash, redirect, render_template, request, url_for

from ..auth_utils import guard_blueprint
from ..db import execute, query_all, query_one, query_value
from ..services import counts
from ..validators import clean

bp = Blueprint("admin", __name__)
guard_blueprint(bp, "admin")

FOOD_UNITS = ["plate", "piece", "bowl", "cup", "glass", "serving"]


@bp.route("/")
def dashboard():
    today = date.today()
    meal_totals, _ = counts.meal_totals_for_date(today)
    today_meals = counts.pending_summary_for_date(today)
    subs = counts.submission_counts(today, today)
    return render_template(
        "admin/dashboard.html", meal_totals=meal_totals, today_meals=today_meals,
        subs=subs, active_customers=counts.active_customer_count(),
    )


# ---------------------------------------------------------------------------
# Food items (menu master list)
# ---------------------------------------------------------------------------
def _get_food_or_404(food_id):
    food = query_one("SELECT * FROM food_items WHERE id=%s", (food_id,))
    if not food:
        abort(404)
    return food


def _read_food_form(food_id=None):
    """Validate the add/edit form. Returns (data, errors)."""
    data = {
        "food_name": clean(request.form.get("food_name"), 100),
        "description": clean(request.form.get("description"), 255),
        "unit": clean(request.form.get("unit"), 20).lower(),
        "price": clean(request.form.get("price"), 12),
        "status": "inactive" if request.form.get("status") == "inactive" else "active",
    }
    errors = []
    if len(data["food_name"]) < 2:
        errors.append("Food name must be at least 2 characters.")
    elif query_one(
        "SELECT id FROM food_items WHERE food_name=%s AND id <> %s",
        (data["food_name"], food_id or 0),
    ):
        errors.append(f"A food item named “{data['food_name']}” already exists.")
    if data["unit"] not in FOOD_UNITS:
        errors.append("Choose a valid unit.")
    price = None
    if data["price"]:
        try:
            price = Decimal(data["price"]).quantize(Decimal("0.01"))
            if price < 0 or price > Decimal("99999.99"):
                raise InvalidOperation
        except (InvalidOperation, ValueError):
            errors.append("Price must be a number between 0 and 99,999.99.")
            price = None
    data["price_value"] = price
    return data, errors


@bp.route("/food-items")
def food_items():
    q = clean(request.args.get("q"), 100)
    status = request.args.get("status") if request.args.get("status") in ("active", "inactive") else ""
    sql = (
        "SELECT f.*, "
        "  (SELECT COUNT(*) FROM food_schedule s WHERE s.food_item_id = f.id) AS times_scheduled, "
        "  (SELECT MIN(s.schedule_date) FROM food_schedule s "
        "     WHERE s.food_item_id = f.id AND s.schedule_date >= %s) AS next_date, "
        "  (SELECT GROUP_CONCAT(DISTINCT mt.meal_name ORDER BY mt.sort_order SEPARATOR ', ') "
        "     FROM food_schedule s JOIN meal_types mt ON mt.id = s.meal_type_id "
        "     WHERE s.food_item_id = f.id) AS meal_names "
        "FROM food_items f WHERE 1=1 "
    )
    params = [date.today()]
    if q:
        sql += "AND (f.food_name LIKE %s OR f.description LIKE %s) "
        like = f"%{q}%"
        params += [like, like]
    if status:
        sql += "AND f.status = %s "
        params.append(status)
    sql += "ORDER BY f.status = 'inactive', f.food_name"
    items = query_all(sql, params)
    stats = query_one(
        "SELECT COUNT(*) AS total, COALESCE(SUM(status='active'),0) AS active, "
        "COALESCE(SUM(status='inactive'),0) AS inactive FROM food_items"
    )
    return render_template("admin/food_items.html", items=items, q=q, status=status, stats=stats)


@bp.route("/food-items/new", methods=["GET", "POST"])
def food_item_new():
    form = {"food_name": "", "description": "", "unit": "plate", "price": "", "status": "active"}
    if request.method == "POST":
        form, errors = _read_food_form()
        if errors:
            for e in errors:
                flash(e, "danger")
        else:
            execute(
                "INSERT INTO food_items (food_name, description, unit, price, status) VALUES (%s,%s,%s,%s,%s)",
                (form["food_name"], form["description"] or None, form["unit"], form["price_value"], form["status"]),
            )
            flash(f"“{form['food_name']}” added to the menu items.", "success")
            if request.form.get("next_action") == "add_another":
                return redirect(url_for("admin.food_item_new"))
            return redirect(url_for("admin.food_items"))
    return render_template("admin/food_item_form.html", form=form, food=None, units=FOOD_UNITS)


@bp.route("/food-items/<int:food_id>/edit", methods=["GET", "POST"])
def food_item_edit(food_id):
    food = _get_food_or_404(food_id)
    form = {
        "food_name": food["food_name"], "description": food["description"] or "",
        "unit": food["unit"], "status": food["status"],
        "price": "" if food["price"] is None else f"{food['price']:.2f}",
    }
    if request.method == "POST":
        form, errors = _read_food_form(food_id)
        if errors:
            for e in errors:
                flash(e, "danger")
        else:
            execute(
                "UPDATE food_items SET food_name=%s, description=%s, unit=%s, price=%s, status=%s WHERE id=%s",
                (form["food_name"], form["description"] or None, form["unit"], form["price_value"],
                 form["status"], food_id),
            )
            flash(f"“{form['food_name']}” updated.", "success")
            return redirect(url_for("admin.food_items"))
    usage = query_all(
        "SELECT s.schedule_date, mt.meal_name FROM food_schedule s "
        "JOIN meal_types mt ON mt.id = s.meal_type_id "
        "WHERE s.food_item_id=%s AND s.schedule_date >= %s ORDER BY s.schedule_date, mt.sort_order LIMIT 8",
        (food_id, date.today()),
    )
    return render_template("admin/food_item_form.html", form=form, food=food, units=FOOD_UNITS, usage=usage)


@bp.route("/food-items/<int:food_id>/toggle", methods=["POST"])
def food_item_toggle(food_id):
    food = _get_food_or_404(food_id)
    new_status = "inactive" if food["status"] == "active" else "active"
    execute("UPDATE food_items SET status=%s WHERE id=%s", (new_status, food_id))
    if new_status == "inactive":
        flash(f"“{food['food_name']}” deactivated. It can't be added to new schedules, "
              "but meals already scheduled keep it.", "success")
    else:
        flash(f"“{food['food_name']}” activated.", "success")
    return redirect(request.referrer or url_for("admin.food_items"))


@bp.route("/food-items/<int:food_id>/delete", methods=["POST"])
def food_item_delete(food_id):
    food = _get_food_or_404(food_id)
    used = int(query_value("SELECT COUNT(*) FROM food_schedule WHERE food_item_id=%s", (food_id,), 0))
    if used:
        flash(f"“{food['food_name']}” is used in {used} scheduled meal(s), so it can't be deleted "
              "(that would break count history). Deactivate it instead.", "warning")
        return redirect(url_for("admin.food_items"))
    execute("DELETE FROM food_items WHERE id=%s", (food_id,))
    flash(f"“{food['food_name']}” deleted.", "success")
    return redirect(url_for("admin.food_items"))
