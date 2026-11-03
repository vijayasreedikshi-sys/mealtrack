"""
Create the MealTrack database, tables and demo data.

Usage (from the project root, with the venv active):
    python database\\init_db.py            # create DB (fails if it already exists)
    python database\\init_db.py --reset    # DROP and recreate the DB (all data lost)
    python database\\init_db.py --no-demo  # schema + reference data + admin only

Demo dates are generated relative to today, so there is always a
previous week (with locked counts and preparation records), the current
week and next week.
"""
import argparse
import os
import random
import re
import sys
from datetime import date, datetime, time, timedelta

import pymysql
from werkzeug.security import generate_password_hash

ROOT = os.path.abspath(os.path.join(os.path.dirname(__file__), ".."))
sys.path.insert(0, ROOT)
from config import Config  # noqa: E402

DB_DIR = os.path.dirname(os.path.abspath(__file__))

# weekday (0=Mon) -> (breakfast, lunch, dinner)
WEEKLY_MENU = {
    0: ("Idly", "Meals", "Chapati"),
    1: ("Pongal", "Lemon Rice", "Dosa"),
    2: ("Poori", "Meals", "Parotta"),
    3: ("Upma", "Sambar Rice", "Idiyappam"),
    4: ("Dosa", "Meals", "Chapati"),
    5: ("Idiyappam", "Veg Biryani", "Uthappam"),
    6: ("Pongal", "Curd Rice", "Veg Fried Rice"),
}

CUSTOMERS = [
    ("Arun Kumar", "101"), ("Priya Sharma", "102"), ("Karthik Raja", "103"),
    ("Divya Menon", "104"), ("Suresh Babu", "105"), ("Lakshmi Narayan", "106"),
    ("Vignesh S", "107"), ("Anitha R", "108"), ("Rahul Verma", "109"),
    ("Sneha Iyer", "110"), ("Manoj P", "111"), ("Fathima N", "112"),
]


def split_sql(text):
    """Split a .sql file into statements (no semicolons inside our literals)."""
    text = re.sub(r"--[^\n]*", "", text)
    return [s.strip() for s in text.split(";") if s.strip()]


def to_time(value):
    if isinstance(value, timedelta):  # PyMySQL returns TIME as timedelta
        return (datetime.min + value).time()
    return value


def connect(database=None):
    return pymysql.connect(
        host=Config.DB_HOST, port=Config.DB_PORT, user=Config.DB_USER,
        password=Config.DB_PASSWORD, database=database, charset="utf8mb4",
        cursorclass=pymysql.cursors.DictCursor, autocommit=False,
    )


def create_database(reset):
    conn = connect()
    with conn.cursor() as cur:
        cur.execute("SHOW DATABASES LIKE %s", (Config.DB_NAME,))
        exists = cur.fetchone() is not None
        if exists and not reset:
            print(f"Database '{Config.DB_NAME}' already exists. Use --reset to drop and recreate it.")
            sys.exit(1)
        if exists:
            cur.execute(f"DROP DATABASE `{Config.DB_NAME}`")
            print(f"Dropped database '{Config.DB_NAME}'.")
        cur.execute(
            f"CREATE DATABASE `{Config.DB_NAME}` CHARACTER SET utf8mb4 COLLATE utf8mb4_unicode_ci"
        )
    conn.commit()
    conn.close()
    print(f"Created database '{Config.DB_NAME}'.")


def run_sql_file(conn, filename):
    with open(os.path.join(DB_DIR, filename), encoding="utf-8") as fh:
        statements = split_sql(fh.read())
    with conn.cursor() as cur:
        for stmt in statements:
            cur.execute(stmt)
    conn.commit()
    print(f"Executed {filename} ({len(statements)} statements).")


def insert_user(cur, name, email, password, role, phone=None, room=None, status="active"):
    cur.execute(
        "INSERT INTO users (name, email, password_hash, role, phone, room_no, status) "
        "VALUES (%s,%s,%s,%s,%s,%s,%s)",
        (name, email, generate_password_hash(password), role, phone, room, status),
    )
    return cur.lastrowid


def seed_demo(conn, with_demo=True):
    rnd = random.Random(42)
    now = datetime.now()
    today = date.today()
    with conn.cursor() as cur:
        admin_id = insert_user(cur, "Admin", "admin@mealtrack.local", "Admin@123", "admin", "9000000001")
        if not with_demo:
            conn.commit()
            return

        seller1 = insert_user(cur, "Ravi Kumar", "seller1@mealtrack.local", "Seller@123", "seller", "9000000011")
        seller2 = insert_user(cur, "Meena Devi", "seller2@mealtrack.local", "Seller@123", "seller", "9000000012")

        customer_ids = []
        for i, (name, room) in enumerate(CUSTOMERS, start=1):
            status = "inactive" if i == 12 else "active"
            cid = insert_user(cur, name, f"customer{i}@mealtrack.local", "Customer@123", "customer",
                              f"98{i:08d}", room, status)
            customer_ids.append((cid, status))

        cur.execute("SELECT * FROM meal_types ORDER BY sort_order")
        meals = {m["meal_name"]: m for m in cur.fetchall()}
        cur.execute("SELECT id, food_name FROM food_items")
        foods = {f["food_name"]: f["id"] for f in cur.fetchall()}

        # Seller responsibilities: Ravi -> Breakfast & Dinner (may see customer list), Meena -> Lunch
        defaults = [(seller1, meals["Breakfast"]["id"], 1), (seller1, meals["Dinner"]["id"], 1),
                    (seller2, meals["Lunch"]["id"], 0)]
        cur.executemany(
            "INSERT INTO seller_meal_defaults (seller_id, meal_type_id, can_view_customers) VALUES (%s,%s,%s)",
            defaults,
        )
        seller_for_meal = {mt: (sid, view) for sid, mt, view in defaults}

        # Schedules: previous week Monday .. next week Sunday
        start = today - timedelta(days=today.weekday() + 7)
        schedule_rows = []
        for offset in range(21):
            d = start + timedelta(days=offset)
            for meal_name, food_name in zip(("Breakfast", "Lunch", "Dinner"), WEEKLY_MENU[d.weekday()]):
                m = meals[meal_name]
                cutoff = datetime.combine(d + timedelta(days=m["default_cutoff_day_offset"]),
                                          to_time(m["default_cutoff_time"]))
                cur.execute(
                    "INSERT INTO food_schedule (schedule_date, meal_type_id, food_item_id, cutoff_time, "
                    "is_published, created_by) VALUES (%s,%s,%s,%s,1,%s)",
                    (d, m["id"], foods[food_name], cutoff, admin_id),
                )
                sid = cur.lastrowid
                seller_id, can_view = seller_for_meal[m["id"]]
                cur.execute(
                    "INSERT INTO seller_assignments (seller_id, food_schedule_id, can_view_customers) "
                    "VALUES (%s,%s,%s)", (seller_id, sid, can_view),
                )
                schedule_rows.append((sid, d, meal_name, cutoff, seller_id))

        # Customer counts
        for sid, d, meal_name, cutoff, _ in schedule_rows:
            closed = cutoff <= now
            participation = 0.92 if closed else (0.6 if d <= today + timedelta(days=2) else 0.25)
            for cid, _status in customer_ids:
                if rnd.random() > participation:
                    continue  # no submission (pending / missed)
                qty = rnd.choices([0, 1, 2, 3], weights=[18, 55, 22, 5])[0]
                status = "locked" if closed else "submitted"
                submitted_at = min(cutoff - timedelta(hours=rnd.randint(2, 30)), now)
                cur.execute(
                    "INSERT INTO customer_meal_counts (customer_id, food_schedule_id, quantity, "
                    "submission_status, submitted_at, updated_at) VALUES (%s,%s,%s,%s,%s,%s)",
                    (cid, sid, qty, status, submitted_at, submitted_at),
                )

        # Preparation records for meals whose date is in the past (and today's closed meals)
        for sid, d, meal_name, cutoff, seller_id in schedule_rows:
            if not (cutoff <= now and d <= today):
                continue
            cur.execute(
                "SELECT COALESCE(SUM(c.quantity),0) AS total FROM customer_meal_counts c "
                "JOIN users u ON u.id=c.customer_id AND u.status='active' "
                "WHERE c.food_schedule_id=%s AND c.submission_status IN ('submitted','locked')",
                (sid,),
            )
            required = int(cur.fetchone()["total"])
            if d == today:
                cur.execute(
                    "INSERT INTO food_preparation (seller_id, food_schedule_id, required_quantity, "
                    "prepared_quantity, preparation_status) VALUES (%s,%s,%s,%s,'preparing')",
                    (seller_id, sid, required, required),
                )
                continue
            prepared = required + rnd.randint(0, max(1, required // 8))
            consumed = max(0, required - rnd.randint(0, 2))
            remaining = prepared - consumed
            wasted = rnd.randint(0, remaining)
            cur.execute(
                "INSERT INTO food_preparation (seller_id, food_schedule_id, required_quantity, "
                "prepared_quantity, consumed_quantity, remaining_quantity, wasted_quantity, "
                "preparation_status, remarks, updated_at) VALUES (%s,%s,%s,%s,%s,%s,%s,'completed',%s,%s)",
                (seller_id, sid, required, prepared, consumed, remaining, wasted, None,
                 datetime.combine(d, time(21, 0))),
            )

        # Welcome notifications
        for cid, status in customer_ids:
            if status == "active":
                cur.execute(
                    "INSERT INTO notifications (user_id, message, link) VALUES (%s,%s,%s)",
                    (cid, "Welcome to MealTrack! This week's food schedule is published.", "/customer/schedule"),
                )
        for sid_ in (seller1, seller2):
            cur.execute(
                "INSERT INTO notifications (user_id, message, link) VALUES (%s,%s,%s)",
                (sid_, "You have been assigned preparation schedules for this week.", "/seller/"),
            )
    conn.commit()
    print("Inserted demo users, 3 weeks of schedules, counts and preparation records.")


def main():
    parser = argparse.ArgumentParser(description="Initialise the MealTrack database")
    parser.add_argument("--reset", action="store_true", help="drop and recreate the database")
    parser.add_argument("--no-demo", action="store_true", help="only create the admin account")
    args = parser.parse_args()

    try:
        create_database(args.reset)
    except pymysql.err.OperationalError as exc:
        print(f"Could not connect to MySQL: {exc}\nCheck DB_HOST/DB_USER/DB_PASSWORD in .env")
        sys.exit(1)

    conn = connect(Config.DB_NAME)
    try:
        run_sql_file(conn, "schema.sql")
        run_sql_file(conn, "seed.sql")
        seed_demo(conn, with_demo=not args.no_demo)
    finally:
        conn.close()

    print("\nDone. Demo logins (see README):")
    print("  admin@mealtrack.local      / Admin@123")
    if not args.no_demo:
        print("  seller1@mealtrack.local    / Seller@123")
        print("  customer1@mealtrack.local  / Customer@123")


if __name__ == "__main__":
    main()
