"""MealTrack application factory."""
from datetime import date, datetime

from flask import Flask, flash, g, redirect, render_template, request, url_for
from flask_wtf.csrf import CSRFError, CSRFProtect

from config import Config

csrf = CSRFProtect()

MEAL_ICONS = {"Breakfast": "sunrise", "Lunch": "sun", "Dinner": "moon"}


def create_app(config_object=None):
    app = Flask(__name__)
    app.config.from_object(config_object or Config)

    csrf.init_app(app)

    from . import db
    db.init_app(app)

    from .auth_utils import home_url_for, init_auth
    init_auth(app)

    from .services.cutoff import maybe_run_sweep
    app.before_request(maybe_run_sweep)

    from .blueprints.admin import bp as admin_bp
    from .blueprints.auth import bp as auth_bp
    from .blueprints.customer import bp as customer_bp
    from .blueprints.seller import bp as seller_bp
    app.register_blueprint(auth_bp)
    app.register_blueprint(customer_bp, url_prefix="/customer")
    app.register_blueprint(admin_bp, url_prefix="/admin")
    app.register_blueprint(seller_bp, url_prefix="/seller")

    @app.route("/")
    def index():
        if g.get("user"):
            return redirect(home_url_for(g.user["role"]))
        return redirect(url_for("auth.login"))

    # ---------------- template helpers ----------------
    @app.context_processor
    def inject_globals():
        from .services import notifications
        ctx = {"app_name": "MealTrack", "tagline": "Smart Food Count & Meal Planning",
               "now": datetime.now(), "today": date.today(), "meal_icons": MEAL_ICONS}
        user = g.get("user")
        ctx["unread_count"], ctx["recent_notifications"] = 0, []
        if user:
            try:
                ctx["unread_count"] = notifications.unread_count(user["id"])
                ctx["recent_notifications"] = notifications.recent(user["id"], 5)
            except Exception:  # keep error pages renderable even if the DB is down
                app.logger.exception("Could not load notifications")
        return ctx

    @app.template_filter("fdate")
    def fdate(value, fmt="%a, %d %b %Y"):
        return value.strftime(fmt) if value else ""

    @app.template_filter("fdt")
    def fdt(value, fmt="%d %b %Y, %I:%M %p"):
        return value.strftime(fmt) if value else ""

    @app.template_filter("ftime")
    def ftime(value):
        return value.strftime("%I:%M %p") if value else ""

    @app.template_filter("iso")
    def iso(value):
        return value.isoformat() if value else ""

    @app.template_filter("ago")
    def ago(value):
        if not value:
            return ""
        secs = int((datetime.now() - value).total_seconds())
        if secs < 60:
            return "just now"
        if secs < 3600:
            return f"{secs // 60} min ago"
        if secs < 86400:
            return f"{secs // 3600} h ago"
        return value.strftime("%d %b")

    # ---------------- error handling ----------------
    @app.errorhandler(403)
    def forbidden(_e):
        return render_template("errors/error.html", code=403, title="Access denied",
                               message="You do not have permission to open this page."), 403

    @app.errorhandler(404)
    def not_found(_e):
        return render_template("errors/error.html", code=404, title="Page not found",
                               message="The page you are looking for does not exist."), 404

    @app.errorhandler(500)
    def server_error(_e):
        return render_template("errors/error.html", code=500, title="Something went wrong",
                               message="An unexpected error occurred. Please try again."), 500

    @app.errorhandler(CSRFError)
    def csrf_error(_e):
        flash("Your form expired. Please try again.", "warning")
        return redirect(request.referrer or url_for("index"))

    @app.after_request
    def security_headers(resp):
        resp.headers.setdefault("X-Content-Type-Options", "nosniff")
        resp.headers.setdefault("X-Frame-Options", "SAMEORIGIN")
        resp.headers.setdefault("Referrer-Policy", "same-origin")
        if g.get("user"):
            resp.headers["Cache-Control"] = "no-store"  # back button after logout shows nothing
        return resp

    return app
