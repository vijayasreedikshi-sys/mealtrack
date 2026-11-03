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

**Stack:** ASP.NET Core 10 MVC (C#, Razor views) · MySQL 8 · Dapper + MySqlConnector (parameterized SQL) ·
BCrypt password hashing · cookie authentication + antiforgery (CSRF) · Bootstrap 5 · Lucide icons · Inter font

---

## What to install on a new laptop

| Tool | Version | How to install | Needed for |
|------|---------|----------------|------------|
| **.NET SDK** | 10.0 (LTS) | `winget install Microsoft.DotNet.SDK.10` or https://dotnet.microsoft.com/download | building and running the app |
| **MySQL Server** | 8.0 | MySQL Installer from https://dev.mysql.com/downloads/installer/ (keep the `MySQL80` Windows service) | the database |
| **Git** | any | `winget install Git.Git` | cloning the repo |
| Visual Studio 2022/2026 *or* VS Code + C# Dev Kit | optional | – | editing / debugging |

NuGet packages are restored **automatically** by `dotnet run` / `dotnet build` (they are listed in
`MealTrack/MealTrack.csproj`, so you don't install them by hand):

| NuGet package | Version | Why |
|---------------|---------|-----|
| `MySqlConnector` | 2.6.2 | MySQL driver for .NET (async, connection pooling) |
| `Dapper` | 2.1.89 | Runs our hand-written, parameterized SQL and maps rows to C# classes |
| `BCrypt.Net-Next` | 4.2.0 | Password hashing |
| `ClosedXML` | 0.105.1 | Excel (.xlsx) report export |
| `QRCoder` | 1.8.1 | Generates the meal check-in QR codes |

The test project (`MealTrack.Tests`) also uses `xunit` 2.9.3, `xunit.runner.visualstudio` 4.0.1,
`Microsoft.NET.Test.Sdk` 18.11.0 and `Microsoft.AspNetCore.Mvc.Testing` 10.0.14. Run them with `dotnet test` from the repo
root (they use a separate `mealtrack_test` database, never your real one).

Everything else (MVC, Razor, cookie auth, CSRF protection, background sweep job) is built into ASP.NET Core.
Bootstrap, Lucide icons and the Inter font load from a CDN, so the PC needs internet access for the styling.

---

## Setup on Windows

```powershell
git clone <repo-url> "Food Tracker"
cd "Food Tracker\MealTrack"

# 1. MySQL credentials (this file is git-ignored)
copy appsettings.Local.example.json appsettings.Local.json
notepad appsettings.Local.json      # set your MySQL User and Password

# 2. Create the database, tables and demo data
dotnet run -- init-db               # add --reset to drop & recreate, --no-demo for admin only

# 3. Run the site
dotnet run
```

Open **http://localhost:5000**. Stop the server with `Ctrl+C`.

In Visual Studio you can instead open `MealTrack.sln` and press **F5** (run `init-db` once from a terminal first).

### Demo accounts

| Role | Email | Password |
|------|-------|----------|
| Admin | admin@mealtrack.local | Admin@123 |
| Seller (Breakfast & Dinner, may view customer list) | seller1@mealtrack.local | Seller@123 |
| Seller (Lunch) | seller2@mealtrack.local | Seller@123 |
| Customer | customer1@mealtrack.local … customer11@mealtrack.local | Customer@123 |
| Customer (inactive, cannot log in) | customer12@mealtrack.local | Customer@123 |

Demo data is generated **relative to today**: last week (closed and locked counts with preparation records),
this week, and next week (mostly open for submission). Run `dotnet run -- init-db --reset` before a demo to
refresh the dates.

Forgot-password links are printed in the terminal window running `dotnet run` (no mail server is configured).

---

## Project structure

```
Food Tracker/
├── MealTrack.sln
└── MealTrack/
    ├── MealTrack.csproj        # target framework + NuGet packages
    ├── Program.cs              # startup: services, cookie auth, CSRF, middleware, init-db command
    ├── appsettings.json        # default settings (business rules, DB host/name)
    ├── appsettings.Local.json  # your MySQL user/password (git-ignored)
    ├── Database/
    │   ├── schema.sql          # tables, keys, indexes, CHECK constraints
    │   └── seed.sql            # meal types (with default cutoffs) + food items
    ├── Data/
    │   ├── Db.cs               # connection + query helpers (Dapper), settings classes
    │   └── DbInitializer.cs    # creates DB, runs SQL, generates demo users/schedules/counts
    ├── Infrastructure/         # current-user middleware, flash messages, validators, UI helpers
    ├── Services/
    │   ├── CountService.cs     # automatic food-count aggregation (single source of truth)
    │   ├── Cutoff.cs           # cutoff rules, status logic, background lock/notify job
    │   └── NotificationService.cs
    ├── Controllers/            # Auth (common), Customer, Admin, Seller, Error
    ├── Models/                 # row classes and view models
    ├── Views/                  # Razor views: Shared layout + one folder per role
    └── wwwroot/                # theme.css (4-colour design system), app.js, logo.svg
```

## Meal check-in with QR codes (reduces food wastage)

Each meal has **one QR code for the day** (for example today's Breakfast, today's Lunch, today's Dinner). The QR stays
the same all day, and tomorrow every meal gets a new one. A QR only works on its own day.

1. **Admin or seller** opens **Meal Check-in** and clicks **Show QR** on the counter screen, or
   prints it with **Print today's QR**. A 6-digit code is shown under the QR.
2. **Customers** scan it with the phone camera, log in if asked, and the meal is **marked as eaten
   straight away**. Scanning again does not count twice. Without a camera they open
   **Meal Check-in** and type the 6-digit code. Staff can also tick people off with **Mark came** on the attendance list.
3. MealTrack compares *applied* (count of 1 or more) with *came*:
   * **No-show:** applied but never checked in. Those portions were cooked for nobody.
   * **Walk-in:** came without applying.
4. **Reports → Attendance & no-shows** shows this per meal, and the customer report has a *No-shows* column. The day
   after a meal, every no-show customer gets a reminder to enter 0 next time.

If a QR is shared outside the dining room, **New QR** replaces it for that meal, and the old QR stops working.

**Phones need to reach the laptop.** `localhost` only works on the laptop itself, so for a demo with real phones:

```powershell
dotnet run --launch-profile lan      # listens on every network card, port 5000
ipconfig                             # find the Wi-Fi IPv4 address, e.g. 192.168.0.115
```

Open the QR screen at `http://<that-ip>:5000/attendance` (the page shows the address if you opened it via localhost).
Keep the phones on the same Wi-Fi, and click **Allow** when the Windows Firewall asks about `dotnet`.
To pin the address used in the QR, set `MealTrack:PublicBaseUrl` in `appsettings.Local.json`.

## Troubleshooting

* **`Could not connect to MySQL`** – check `appsettings.Local.json` and that the `MySQL80` service is running
  (`Get-Service MySQL80`).
* **Port 5000 already in use** – stop the other process, or run `dotnet run --urls http://localhost:5050`.
* **`Database 'mealtrack' already exists`** – use `dotnet run -- init-db --reset` (this deletes all data).
