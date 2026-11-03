"""MySQL access helpers. Every query goes through these functions and uses
parameter placeholders (%s), so values are never interpolated into SQL."""
from contextlib import contextmanager

import pymysql
from flask import current_app, g


def get_db():
    if "db" not in g:
        cfg = current_app.config
        g.db = pymysql.connect(
            host=cfg["DB_HOST"], port=cfg["DB_PORT"], user=cfg["DB_USER"],
            password=cfg["DB_PASSWORD"], database=cfg["DB_NAME"], charset="utf8mb4",
            cursorclass=pymysql.cursors.DictCursor, autocommit=False,
        )
    return g.db


def close_db(_exc=None):
    conn = g.pop("db", None)
    if conn is not None:
        try:
            conn.rollback()  # discard anything not explicitly committed
            conn.close()
        except pymysql.Error:
            pass


def init_app(app):
    app.teardown_appcontext(close_db)


def query_all(sql, params=None):
    with get_db().cursor() as cur:
        cur.execute(sql, params or ())
        return cur.fetchall()


def query_one(sql, params=None):
    with get_db().cursor() as cur:
        cur.execute(sql, params or ())
        return cur.fetchone()


def query_value(sql, params=None, default=None):
    row = query_one(sql, params)
    if not row:
        return default
    value = next(iter(row.values()))
    return default if value is None else value


def execute(sql, params=None, commit=True):
    """Run INSERT/UPDATE/DELETE. Returns lastrowid (or rowcount when no id)."""
    conn = get_db()
    with conn.cursor() as cur:
        cur.execute(sql, params or ())
        result = cur.lastrowid or cur.rowcount
    if commit:
        conn.commit()
    return result


def execute_many(sql, seq_params, commit=True):
    conn = get_db()
    with conn.cursor() as cur:
        cur.executemany(sql, seq_params)
        count = cur.rowcount
    if commit:
        conn.commit()
    return count


@contextmanager
def transaction():
    """Yield a cursor; commit on success, roll back on any exception."""
    conn = get_db()
    cur = conn.cursor()
    try:
        yield cur
        conn.commit()
    except Exception:
        conn.rollback()
        raise
    finally:
        cur.close()
