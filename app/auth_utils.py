"""Session authentication and role-based access control."""
from functools import wraps

from flask import abort, flash, g, redirect, request, session, url_for

from .db import query_one

ROLE_HOME = {
    "customer": "customer.dashboard",
    "admin": "admin.dashboard",
    "seller": "seller.dashboard",
}


def load_logged_in_user():
    """Runs before every request: loads g.user and kicks out inactive users."""
    g.user = None
    if request.endpoint == "static":
        return
    user_id = session.get("user_id")
    if user_id is None:
        return
    user = query_one(
        "SELECT id, name, email, role, phone, room_no, status, created_at FROM users WHERE id=%s",
        (user_id,),
    )
    if user is None or user["status"] != "active" or user["role"] != session.get("role"):
        session.clear()
        flash("Your session has ended. Please log in again.", "warning")
        return
    g.user = user


def init_auth(app):
    app.before_request(load_logged_in_user)


def login_user(user):
    session.clear()  # new session on login (prevents session fixation)
    session.permanent = True
    session["user_id"] = user["id"]
    session["role"] = user["role"]


def home_url_for(role):
    return url_for(ROLE_HOME.get(role, "auth.login"))


def login_required(view):
    @wraps(view)
    def wrapped(*args, **kwargs):
        if g.get("user") is None:
            flash("Please log in to continue.", "info")
            return redirect(url_for("auth.login", next=request.path))
        return view(*args, **kwargs)
    return wrapped


def role_required(*roles):
    def decorator(view):
        @wraps(view)
        def wrapped(*args, **kwargs):
            if g.get("user") is None:
                flash("Please log in to continue.", "info")
                return redirect(url_for("auth.login", next=request.path))
            if g.user["role"] not in roles:
                abort(403)
            return view(*args, **kwargs)
        return wrapped
    return decorator


def guard_blueprint(bp, role):
    """Protect every route of a blueprint for a single role."""
    @bp.before_request
    def _guard():
        if g.get("user") is None:
            flash("Please log in to continue.", "info")
            return redirect(url_for("auth.login", next=request.path))
        if g.user["role"] != role:
            abort(403)
