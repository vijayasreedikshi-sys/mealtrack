"""Seller module (Milestone 1: live cooking requirements; full module in Milestone 3)."""
from datetime import date, datetime

from flask import Blueprint, g, render_template, request

from ..auth_utils import guard_blueprint
from ..db import query_all
from ..services import counts
from ..validators import parse_date

bp = Blueprint("seller", __name__)
guard_blueprint(bp, "seller")


@bp.route("/")
def dashboard():
    selected = parse_date(request.args.get("date"), date.today())
    rows = counts.schedule_totals(selected, selected, seller_id=g.user["id"])
    prep = {p["food_schedule_id"]: p for p in query_all(
        "SELECT food_schedule_id, prepared_quantity, preparation_status FROM food_preparation "
        "WHERE seller_id=%s AND food_schedule_id IN (SELECT id FROM food_schedule WHERE schedule_date=%s)",
        (g.user["id"], selected),
    )}
    now = datetime.now()
    for r in rows:
        p = prep.get(r["schedule_id"])
        r["prepared"] = p["prepared_quantity"] if p else None
        r["prep_status"] = p["preparation_status"] if p else "not_started"
        r["final"] = r["cutoff_time"] <= now
    total = sum(r["total_qty"] for r in rows)
    return render_template("seller/dashboard.html", rows=rows, total=total, selected=selected)
