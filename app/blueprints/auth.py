"""Common pages: login, registration, password reset, profile, notifications, logout."""
import hashlib
import secrets
from datetime import datetime, timedelta
from urllib.parse import urlparse

from flask import (Blueprint, abort, current_app, flash, g, redirect, render_template,
                   request, session, url_for)
from werkzeug.security import check_password_hash, generate_password_hash

from ..auth_utils import home_url_for, login_required, login_user
from ..db import execute, query_all, query_one, query_value, transaction
from ..services import notifications
from ..validators import (clean, parse_int, validate_email, validate_name, validate_password,
                          validate_phone)

bp = Blueprint("auth", __name__)

# Used to keep login timing similar whether or not the email exists.
_DUMMY_HASH = generate_password_hash("not-a-real-password-123")


def _safe_next(target):
    """Only allow relative redirects inside this site."""
    if not target:
        return None
    parts = urlparse(target)
    if parts.scheme or parts.netloc or not target.startswith("/"):
        return None
    return target


@bp.route("/login", methods=["GET", "POST"])
def login():
    if g.get("user"):
        return redirect(home_url_for(g.user["role"]))
    email = ""
    if request.method == "POST":
        email = clean(request.form.get("email"), 150).lower()
        password = request.form.get("password") or ""
        user = query_one("SELECT * FROM users WHERE email=%s", (email,))
        valid = check_password_hash(user["password_hash"] if user else _DUMMY_HASH, password)
        if not user or not valid:
            flash("Invalid email or password.", "danger")
        elif user["status"] != "active":
            flash("Your account is inactive. Please contact the admin.", "danger")
        else:
            login_user(user)
            flash(f"Welcome back, {user['name'].split()[0]}!", "success")
            nxt = _safe_next(request.args.get("next"))
            role_prefix = f"/{user['role']}"
            if nxt and (nxt.startswith(role_prefix) or nxt.startswith("/profile")
                        or nxt.startswith("/notifications")):
                return redirect(nxt)
            return redirect(home_url_for(user["role"]))
    return render_template("auth/login.html", email=email)


@bp.route("/register", methods=["GET", "POST"])
def register():
    if g.get("user"):
        return redirect(home_url_for(g.user["role"]))
    form = {"name": "", "email": "", "phone": "", "room_no": ""}
    if request.method == "POST":
        form = {
            "name": clean(request.form.get("name"), 100),
            "email": clean(request.form.get("email"), 150).lower(),
            "phone": clean(request.form.get("phone"), 15),
            "room_no": clean(request.form.get("room_no"), 20),
        }
        password = request.form.get("password") or ""
        confirm = request.form.get("confirm_password") or ""
        errors = [e for e in (
            validate_name(form["name"]), validate_email(form["email"]),
            validate_phone(form["phone"], required=True), validate_password(password),
        ) if e]
        if password != confirm:
            errors.append("Passwords do not match.")
        if not errors and query_one("SELECT id FROM users WHERE email=%s", (form["email"],)):
            errors.append("An account with this email already exists.")
        if errors:
            for e in errors:
                flash(e, "danger")
        else:
            user_id = execute(
                "INSERT INTO users (name, email, password_hash, role, phone, room_no, status) "
                "VALUES (%s,%s,%s,'customer',%s,%s,'active')",
                (form["name"], form["email"], generate_password_hash(password),
                 form["phone"], form["room_no"] or None),
            )
            notifications.notify(user_id, "Welcome to MealTrack! Check the weekly schedule and "
                                          "submit your food counts before each cutoff.",
                                 "/customer/schedule")
            flash("Registration successful. Please log in.", "success")
            return redirect(url_for("auth.login"))
    return render_template("auth/register.html", form=form)


@bp.route("/forgot-password", methods=["GET", "POST"])
def forgot_password():
    if request.method == "POST":
        email = clean(request.form.get("email"), 150).lower()
        user = query_one("SELECT id, status FROM users WHERE email=%s", (email,))
        if user and user["status"] == "active":
            token = secrets.token_urlsafe(32)
            execute(
                "INSERT INTO password_resets (user_id, token_hash, expires_at) VALUES (%s,%s,%s)",
                (user["id"], hashlib.sha256(token.encode()).hexdigest(),
                 datetime.now() + timedelta(minutes=current_app.config["PASSWORD_RESET_MINUTES"])),
            )
            link = url_for("auth.reset_password", token=token, _external=True)
            # No mail server configured: the link is written to the server console.
            current_app.logger.warning("PASSWORD RESET LINK for %s: %s", email, link)
            print(f"\n*** MealTrack password reset link for {email}:\n{link}\n", flush=True)
        flash("If an account exists for that email, a reset link has been generated. "
              "In this local setup the link is printed in the server console window, "
              "or the admin can reset your password.", "info")
        return redirect(url_for("auth.login"))
    return render_template("auth/forgot_password.html")


@bp.route("/reset-password/<token>", methods=["GET", "POST"])
def reset_password(token):
    token_hash = hashlib.sha256(token.encode()).hexdigest()
    reset = query_one(
        "SELECT * FROM password_resets WHERE token_hash=%s AND used=0 AND expires_at > %s",
        (token_hash, datetime.now()),
    )
    if not reset:
        flash("This reset link is invalid or has expired.", "danger")
        return redirect(url_for("auth.forgot_password"))
    if request.method == "POST":
        password = request.form.get("password") or ""
        error = validate_password(password)
        if not error and password != request.form.get("confirm_password"):
            error = "Passwords do not match."
        if error:
            flash(error, "danger")
        else:
            with transaction() as cur:
                cur.execute("UPDATE users SET password_hash=%s WHERE id=%s",
                            (generate_password_hash(password), reset["user_id"]))
                cur.execute("UPDATE password_resets SET used=1 WHERE user_id=%s", (reset["user_id"],))
            flash("Password updated. Please log in.", "success")
            return redirect(url_for("auth.login"))
    return render_template("auth/reset_password.html", token=token)


@bp.route("/logout", methods=["POST"])
def logout():
    session.clear()
    flash("You have been logged out.", "info")
    return redirect(url_for("auth.login"))


@bp.route("/profile", methods=["GET", "POST"])
@login_required
def profile():
    if request.method == "POST":
        action = request.form.get("action")
        if action == "details":
            name = clean(request.form.get("name"), 100)
            phone = clean(request.form.get("phone"), 15)
            room_no = clean(request.form.get("room_no"), 20) or None
            errors = [e for e in (validate_name(name), validate_phone(phone)) if e]
            if errors:
                for e in errors:
                    flash(e, "danger")
            else:
                if g.user["role"] != "customer":
                    room_no = g.user["room_no"]
                execute("UPDATE users SET name=%s, phone=%s, room_no=%s WHERE id=%s",
                        (name, phone or None, room_no, g.user["id"]))
                flash("Profile updated.", "success")
        elif action == "password":
            current = request.form.get("current_password") or ""
            new = request.form.get("new_password") or ""
            stored = query_value("SELECT password_hash FROM users WHERE id=%s", (g.user["id"],))
            error = None
            if not check_password_hash(stored, current):
                error = "Current password is incorrect."
            else:
                error = validate_password(new)
                if not error and new != request.form.get("confirm_password"):
                    error = "New passwords do not match."
            if error:
                flash(error, "danger")
            else:
                execute("UPDATE users SET password_hash=%s WHERE id=%s",
                        (generate_password_hash(new), g.user["id"]))
                flash("Password changed successfully.", "success")
        return redirect(url_for("auth.profile"))
    return render_template("auth/profile.html")


# ---------------- notifications (all roles) ----------------
@bp.route("/notifications")
@login_required
def notifications_page():
    page = parse_int(request.args.get("page"), 1, minimum=1)
    per_page = 20
    total = int(query_value("SELECT COUNT(*) FROM notifications WHERE user_id=%s", (g.user["id"],), 0))
    items = query_all(
        "SELECT id, message, link, is_read, created_at FROM notifications WHERE user_id=%s "
        "ORDER BY created_at DESC, id DESC LIMIT %s OFFSET %s",
        (g.user["id"], per_page, (page - 1) * per_page),
    )
    pages = max(1, -(-total // per_page))
    return render_template("common/notifications.html", items=items, page=page, pages=pages, total=total)


@bp.route("/notifications/<int:nid>/open", methods=["POST"])
@login_required
def notification_open(nid):
    item = query_one("SELECT id, link FROM notifications WHERE id=%s AND user_id=%s", (nid, g.user["id"]))
    if not item:
        abort(404)
    notifications.mark_read(g.user["id"], nid)
    target = _safe_next(item["link"]) or url_for("auth.notifications_page")
    return redirect(target)


@bp.route("/notifications/read-all", methods=["POST"])
@login_required
def notifications_read_all():
    notifications.mark_all_read(g.user["id"])
    flash("All notifications marked as read.", "success")
    return redirect(_safe_next(request.form.get("next")) or url_for("auth.notifications_page"))
