# MealTrack – Smart Food Count & Meal Planning

MealTrack is a food **count** tracking and meal planning system for PGs, hostels and mess facilities.
It is **not** a food-ordering app. The admin publishes the menu, residents enter how many portions
they need for each meal (0 = not needed), and the kitchen (sellers) gets totals that are
calculated automatically, so they cook the right quantity and waste less.

| Role | What they do |
|------|--------------|
| **Customer** | View the daily and weekly menu, submit or edit quantities before the cutoff, and see status and history |
| **Admin** | Manage schedules, food items, customers, sellers and cutoff times, plus analytics and reports |
| **Seller** | See cooking requirements for assigned meals and record prepared, consumed and wasted quantities |

**Stack:** Python Flask · MySQL 8 (PyMySQL, parameterized SQL) · Bootstrap 5 · Chart.js · Lucide icons · Inter font

---

## Setup on Windows

Prerequisites: **Python 3.10+** and **MySQL Server 8** (running as the `MySQL80` service).

```powershell
cd "C:\Users\TmC\Desktop\Food Tracker"

# 1. Virtual environment + dependencies
python -m venv venv
venv\Scripts\activate
pip install -r requirements.txt

# 2. Configuration
copy .env.example .env
notepad .env          # set DB_PASSWORD (and DB_USER if not root), change SECRET_KEY

# 3. Create the database, tables and demo data
python database\init_db.py            # add --reset to drop & recreate, --no-demo for admin only

# 4. Run
python run.py
```

Open **http://127.0.0.1:5000**

### Demo accounts

| Role | Email | Password |
|------|-------|----------|
| Admin | admin@mealtrack.local | Admin@123 |
| Seller (Breakfast & Dinner, may view customer list) | seller1@mealtrack.local | Seller@123 |
| Seller (Lunch) | seller2@mealtrack.local | Seller@123 |
| Customer | customer1@mealtrack.local … customer11@mealtrack.local | Customer@123 |
| Customer (inactive, cannot log in) | customer12@mealtrack.local | Customer@123 |

Demo data is generated **relative to today**: last week (closed and locked counts with preparation records),
this week, and next week (mostly open for submission).

---

## Project structure

```
Food Tracker/
├── run.py                  # entry point
├── config.py               # settings loaded from .env
├── requirements.txt
├── database/
│   ├── schema.sql          # tables, keys, indexes, CHECK constraints
│   ├── seed.sql            # meal types (with default cutoffs) + food items
│   └── init_db.py          # creates DB, runs SQL, generates demo users/schedules/counts
└── app/
    ├── __init__.py         # application factory, filters, error handlers
    ├── db.py               # connection per request + query helpers
    ├── auth_utils.py       # session login, role guards
    ├── validators.py       # server-side input validation
    ├── services/
    │   ├── counts.py       # automatic food-count aggregation (single source of truth)
    │   ├── cutoff.py       # cutoff rules, status logic, lock/notify sweep
    │   └── notifications.py
    ├── blueprints/         # auth (common), customer, admin, seller
    ├── templates/          # base layout, partials, one folder per role
    └── static/             # theme.css (4-colour design system), app.js, logo.svg
```

## Key business rules

* **Aggregation** – totals use `SUM(quantity)` over rows with status `submitted` or `locked` from
  **active** customers only. Pending, cancelled and never-submitted rows are excluded.
  **0 is a valid submission.** `UNIQUE(customer_id, food_schedule_id)` means an edited count
  replaces the old one, so it is never double-counted. All totals come from live SQL queries.
* **Cutoff** – each meal type has a default cutoff (Breakfast 8:00 PM the previous day, Lunch 9:00 AM,
  Dinner 2:00 PM). Each schedule stores its own `cutoff_time`, and the server re-checks it inside a
  transaction on every submission. After the cutoff the count becomes **Locked**.
* **Statuses** – *Pending* (open, not submitted), *Submitted* (editable), *Locked* (cutoff passed),
  *Not submitted* (closed without a count).
* **Sweep** – about once a minute, during normal requests, MealTrack locks counts past their cutoff,
  sends deadline reminders (2 h before cutoff) and sends final counts to the assigned sellers.
  Every notification has a de-duplication key. No extra scheduler service is required.

## Security

Werkzeug scrypt password hashing · session cookies (HttpOnly, SameSite=Lax, 8 h) · session reset on
login · CSRF tokens on every form · role guard on every blueprint (changing the URL to another
role's area returns **403**) · inactive users signed out immediately · parameterized queries only ·
safe `next` redirects · `no-store` caching for authenticated pages.

**Forgot password:** no mail server is configured, so the one-time reset link (valid 30 minutes) is
printed in the console window running `python run.py`. Admins can also reset passwords.

## Milestones

| # | Scope | Status |
|---|-------|--------|
| 1 | Foundation, database, authentication, design system, **Customer module** | ✅ |
| 2 | **Admin module**: food items, schedules (daily/weekly/copy week), cutoff settings, customers, sellers, food counts, analytics | ⏳ |
| 3 | **Seller module**: preparation tracking, wastage, notifications bell, reports (CSV/Excel), automated tests | ⏳ |
