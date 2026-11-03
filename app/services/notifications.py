"""In-app notifications."""
from datetime import datetime

from ..db import execute, execute_many, query_all, query_value


def notify(user_id, message, link=None, dedupe_key=None, commit=True):
    """Create a notification. With a dedupe_key the same notice is never sent twice."""
    execute(
        "INSERT IGNORE INTO notifications (user_id, message, link, dedupe_key, created_at) "
        "VALUES (%s,%s,%s,%s,%s)",
        (user_id, message[:500], link, dedupe_key, datetime.now()),
        commit=commit,
    )


def notify_many(user_ids, message, link=None, key_prefix=None, commit=True):
    now = datetime.now()
    rows = [
        (uid, message[:500], link, f"{key_prefix}:{uid}" if key_prefix else None, now)
        for uid in user_ids
    ]
    if rows:
        execute_many(
            "INSERT IGNORE INTO notifications (user_id, message, link, dedupe_key, created_at) "
            "VALUES (%s,%s,%s,%s,%s)",
            rows,
            commit=commit,
        )
    return len(rows)


def notify_role(role, message, link=None, key_prefix=None, commit=True):
    ids = [r["id"] for r in query_all(
        "SELECT id FROM users WHERE role=%s AND status='active'", (role,))]
    return notify_many(ids, message, link, key_prefix, commit)


def unread_count(user_id):
    return int(query_value(
        "SELECT COUNT(*) FROM notifications WHERE user_id=%s AND is_read=0", (user_id,), 0))


def recent(user_id, limit=5):
    return query_all(
        "SELECT id, message, link, is_read, created_at FROM notifications "
        "WHERE user_id=%s ORDER BY created_at DESC, id DESC LIMIT %s",
        (user_id, limit),
    )


def mark_read(user_id, notification_id):
    return execute(
        "UPDATE notifications SET is_read=1 WHERE id=%s AND user_id=%s",
        (notification_id, user_id),
    )


def mark_all_read(user_id):
    return execute("UPDATE notifications SET is_read=1 WHERE user_id=%s AND is_read=0", (user_id,))
