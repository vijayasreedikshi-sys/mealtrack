using System.Text.RegularExpressions;
using Dapper;
using MealTrack.Models;
using MealTrack.Services;
using MySqlConnector;

namespace MealTrack.Data;

/// <summary>
/// Creates the MealTrack database, tables and demo data.
///
///   dotnet run -- init-db             create DB (fails if it already exists)
///   dotnet run -- init-db --reset     DROP and recreate the DB (all data lost)
///   dotnet run -- init-db --no-demo   schema + reference data + admin only
///
/// Demo dates are generated relative to today, so there is always a previous
/// week (locked counts and preparation records), the current week and next week.
/// </summary>
public static class DbInitializer
{
    // weekday (Monday first) -> (breakfast, lunch, dinner)
    private static readonly Dictionary<DayOfWeek, string[]> WeeklyMenu = new()
    {
        [DayOfWeek.Monday] = ["Idly", "Meals", "Chapati"],
        [DayOfWeek.Tuesday] = ["Pongal", "Lemon Rice", "Dosa"],
        [DayOfWeek.Wednesday] = ["Poori", "Meals", "Parotta"],
        [DayOfWeek.Thursday] = ["Upma", "Sambar Rice", "Idiyappam"],
        [DayOfWeek.Friday] = ["Dosa", "Meals", "Chapati"],
        [DayOfWeek.Saturday] = ["Idiyappam", "Veg Biryani", "Uthappam"],
        [DayOfWeek.Sunday] = ["Pongal", "Curd Rice", "Veg Fried Rice"],
    };

    private static readonly (string Name, string Room)[] Customers =
    [
        ("Arun Kumar", "101"), ("Priya Sharma", "102"), ("Karthik Raja", "103"),
        ("Divya Menon", "104"), ("Suresh Babu", "105"), ("Lakshmi Narayan", "106"),
        ("Vignesh S", "107"), ("Anitha R", "108"), ("Rahul Verma", "109"),
        ("Sneha Iyer", "110"), ("Manoj P", "111"), ("Fathima N", "112"),
    ];

    private static readonly string[] MealOrder = ["Breakfast", "Lunch", "Dinner"];

    public static async Task<int> RunAsync(IServiceProvider services, string[] args, string contentRoot)
    {
        var reset = args.Contains("--reset");
        var withDemo = !args.Contains("--no-demo");
        var db = services.GetRequiredService<Db>();

        try
        {
            if (!await CreateDatabaseAsync(db, reset)) return 1;
        }
        catch (MySqlException ex)
        {
            Console.WriteLine($"Could not connect to MySQL: {ex.Message}");
            Console.WriteLine("Check the Database section in appsettings.Local.json (User / Password).");
            return 1;
        }

        var sqlDir = FindSqlDir(contentRoot);
        await using var conn = await db.OpenAsync();
        await RunSqlFileAsync(conn, Path.Combine(sqlDir, "schema.sql"));
        await RunSqlFileAsync(conn, Path.Combine(sqlDir, "seed.sql"));
        await SeedAsync(conn, withDemo);

        Console.WriteLine();
        Console.WriteLine("Done. Demo logins (see README):");
        Console.WriteLine("  admin@mealtrack.local      / Admin@123");
        if (withDemo)
        {
            Console.WriteLine("  seller1@mealtrack.local    / Seller@123");
            Console.WriteLine("  customer1@mealtrack.local  / Customer@123");
        }
        return 0;
    }

    /// <summary>Drop and recreate the database (used by the automated tests).</summary>
    public static async Task ResetAsync(Db db, string contentRoot, bool withDemo)
    {
        await CreateDatabaseAsync(db, reset: true);
        var sqlDir = FindSqlDir(contentRoot);
        await using var conn = await db.OpenAsync();
        await RunSqlFileAsync(conn, Path.Combine(sqlDir, "schema.sql"));
        await RunSqlFileAsync(conn, Path.Combine(sqlDir, "seed.sql"));
        await SeedAsync(conn, withDemo);
    }

    /// <summary>
    /// Runs at every start-up: brings a database created by an older version up to date
    /// without losing data (creates new tables, adds new columns). Does nothing if the DB is missing.
    /// </summary>
    public static async Task UpgradeAsync(Db db, string contentRoot, ILogger log)
    {
        try
        {
            await using var conn = await db.OpenAsync();
            if (await conn.ExecuteScalarAsync<int>(
                    "SELECT COUNT(*) FROM information_schema.tables WHERE table_schema = DATABASE() AND table_name = 'users'") == 0)
                return; // not initialised yet – init-db will create everything

            var text = Regex.Replace(await File.ReadAllTextAsync(Path.Combine(FindSqlDir(contentRoot), "schema.sql")), "--[^\n]*", "");
            foreach (var stmt in text.Split(';').Select(s => s.Trim()).Where(s => s.StartsWith("CREATE TABLE ", StringComparison.Ordinal)))
                await conn.ExecuteAsync("CREATE TABLE IF NOT EXISTS " + stmt["CREATE TABLE ".Length..]);

            if (await conn.ExecuteScalarAsync<int>(
                    "SELECT COUNT(*) FROM information_schema.columns WHERE table_schema = DATABASE() " +
                    "AND table_name = 'food_schedule' AND column_name = 'qr_secret'") == 0)
            {
                await conn.ExecuteAsync("ALTER TABLE food_schedule ADD COLUMN qr_secret CHAR(32) NULL AFTER notes");
                log.LogInformation("Database upgraded: added food_schedule.qr_secret");
            }
        }
        catch (MySqlException ex)
        {
            log.LogWarning("Database upgrade check skipped: {Message}", ex.Message);
        }
    }

    private static string FindSqlDir(string contentRoot)
    {
        foreach (var dir in new[] { Path.Combine(contentRoot, "Database"), Path.Combine(AppContext.BaseDirectory, "Database") })
            if (File.Exists(Path.Combine(dir, "schema.sql"))) return dir;
        throw new FileNotFoundException("Could not find Database/schema.sql");
    }

    private static async Task<bool> CreateDatabaseAsync(Db db, bool reset)
    {
        await using var conn = await db.OpenAsync(withDatabase: false);
        var name = db.DatabaseName;
        if (!Regex.IsMatch(name, "^[A-Za-z0-9_]+$"))
            throw new InvalidOperationException("Database name may only contain letters, digits and _.");

        var exists = await conn.ExecuteScalarAsync<string?>("SHOW DATABASES LIKE @name", new { name }) is not null;
        if (exists && !reset)
        {
            Console.WriteLine($"Database '{name}' already exists. Use --reset to drop and recreate it.");
            return false;
        }
        if (exists)
        {
            await conn.ExecuteAsync($"DROP DATABASE `{name}`");
            Console.WriteLine($"Dropped database '{name}'.");
        }
        await conn.ExecuteAsync($"CREATE DATABASE `{name}` CHARACTER SET utf8mb4 COLLATE utf8mb4_unicode_ci");
        Console.WriteLine($"Created database '{name}'.");
        return true;
    }

    /// <summary>Split a .sql file into statements (no semicolons inside our literals).</summary>
    private static async Task RunSqlFileAsync(MySqlConnection conn, string path)
    {
        var text = Regex.Replace(await File.ReadAllTextAsync(path), "--[^\n]*", "");
        var statements = text.Split(';').Select(s => s.Trim()).Where(s => s.Length > 0).ToList();
        foreach (var stmt in statements) await conn.ExecuteAsync(stmt);
        Console.WriteLine($"Executed {Path.GetFileName(path)} ({statements.Count} statements).");
    }

    private static async Task<int> InsertUserAsync(MySqlConnection conn, MySqlTransaction tx, string name, string email,
        string passwordHash, string role, string? phone = null, string? room = null, string status = "active")
    {
        return await conn.ExecuteScalarAsync<int>(
            "INSERT INTO users (name, email, password_hash, role, phone, room_no, status) " +
            "VALUES (@name, @email, @passwordHash, @role, @phone, @room, @status); SELECT LAST_INSERT_ID();",
            new { name, email, passwordHash, role, phone, room, status }, tx);
    }

    private static int WeightedPick(Random rnd, int[] values, int[] weights)
    {
        var roll = rnd.Next(weights.Sum());
        for (var i = 0; i < values.Length; i++)
        {
            if (roll < weights[i]) return values[i];
            roll -= weights[i];
        }
        return values[^1];
    }

    private static async Task SeedAsync(MySqlConnection conn, bool withDemo)
    {
        var rnd = new Random(42);
        var now = DateTime.Now;
        var today = DateTime.Today;

        await using var transaction = await conn.BeginTransactionAsync();
        var tx = transaction;

        // Hash each demo password once (BCrypt is deliberately slow).
        var adminHash = BCrypt.Net.BCrypt.HashPassword("Admin@123");
        var adminId = await InsertUserAsync(conn, tx, "Admin", "admin@mealtrack.local", adminHash, Roles.Admin, "9000000001");
        if (!withDemo)
        {
            await transaction.CommitAsync();
            Console.WriteLine("Inserted the admin account.");
            return;
        }

        var sellerHash = BCrypt.Net.BCrypt.HashPassword("Seller@123");
        var customerHash = BCrypt.Net.BCrypt.HashPassword("Customer@123");
        var seller1 = await InsertUserAsync(conn, tx, "Ravi Kumar", "seller1@mealtrack.local", sellerHash, Roles.Seller, "9000000011");
        var seller2 = await InsertUserAsync(conn, tx, "Meena Devi", "seller2@mealtrack.local", sellerHash, Roles.Seller, "9000000012");

        var customerIds = new List<(int Id, string Status)>();
        for (var i = 1; i <= Customers.Length; i++)
        {
            var (name, room) = Customers[i - 1];
            var status = i == 12 ? "inactive" : "active";
            var id = await InsertUserAsync(conn, tx, name, $"customer{i}@mealtrack.local", customerHash, Roles.Customer,
                $"98{i:00000000}", room, status);
            customerIds.Add((id, status));
        }

        var meals = (await conn.QueryAsync<MealType>("SELECT * FROM meal_types ORDER BY sort_order", transaction: transaction))
            .ToDictionary(m => m.MealName);
        var foods = (await conn.QueryAsync<(int Id, string FoodName)>("SELECT id, food_name FROM food_items", transaction: transaction))
            .ToDictionary(f => f.FoodName, f => f.Id);

        // Seller responsibilities: Ravi -> Breakfast & Dinner (may see customer list), Meena -> Lunch
        var defaults = new[]
        {
            (SellerId: seller1, MealTypeId: meals["Breakfast"].Id, CanView: true),
            (SellerId: seller1, MealTypeId: meals["Dinner"].Id, CanView: true),
            (SellerId: seller2, MealTypeId: meals["Lunch"].Id, CanView: false),
        };
        foreach (var d in defaults)
            await conn.ExecuteAsync(
                "INSERT INTO seller_meal_defaults (seller_id, meal_type_id, can_view_customers) VALUES (@SellerId, @MealTypeId, @CanView)",
                new { d.SellerId, d.MealTypeId, d.CanView }, transaction);
        var sellerForMeal = defaults.ToDictionary(d => d.MealTypeId, d => (d.SellerId, d.CanView));

        // Schedules: previous week Monday .. next week Sunday
        var start = Infrastructure.Validators.WeekStart(today).AddDays(-7);
        var scheduleRows = new List<(int Id, DateTime Date, DateTime Cutoff, int SellerId)>();
        for (var offset = 0; offset < 21; offset++)
        {
            var d = start.AddDays(offset);
            var menu = WeeklyMenu[d.DayOfWeek];
            for (var k = 0; k < 3; k++)
            {
                var m = meals[MealOrder[k]];
                var cutoff = Cutoff.Compute(d, m);
                var sid = await conn.ExecuteScalarAsync<int>(
                    "INSERT INTO food_schedule (schedule_date, meal_type_id, food_item_id, cutoff_time, is_published, created_by) " +
                    "VALUES (@d, @mealTypeId, @foodId, @cutoff, 1, @adminId); SELECT LAST_INSERT_ID();",
                    new { d, mealTypeId = m.Id, foodId = foods[menu[k]], cutoff, adminId }, transaction);
                var (sellerId, canView) = sellerForMeal[m.Id];
                await conn.ExecuteAsync(
                    "INSERT INTO seller_assignments (seller_id, food_schedule_id, can_view_customers) VALUES (@sellerId, @sid, @canView)",
                    new { sellerId, sid, canView }, transaction);
                scheduleRows.Add((sid, d, cutoff, sellerId));
            }
        }

        var applied = new List<(int Sid, int Cid, DateTime Date, string Meal)>();
        // Customer counts
        foreach (var (sid, d, cutoff, _) in scheduleRows)
        {
            var closed = cutoff <= now;
            var participation = closed ? 0.92 : (d <= today.AddDays(2) ? 0.6 : 0.25);
            foreach (var (cid, _) in customerIds)
            {
                if (rnd.NextDouble() > participation) continue; // no submission (pending / missed)
                var qty = WeightedPick(rnd, [0, 1, 2, 3], [18, 55, 22, 5]);
                var status = closed ? "locked" : "submitted";
                var submittedAt = cutoff.AddHours(-rnd.Next(2, 31));
                if (submittedAt > now) submittedAt = now;
                await conn.ExecuteAsync(
                    "INSERT INTO customer_meal_counts (customer_id, food_schedule_id, quantity, submission_status, submitted_at, updated_at) " +
                    "VALUES (@cid, @sid, @qty, @status, @submittedAt, @submittedAt)",
                    new { cid, sid, qty, status, submittedAt }, transaction);
                if (qty > 0) applied.Add((sid, cid, d, MealOrder[scheduleRows.FindIndex(r => r.Id == sid) % 3]));
            }
        }

        // Preparation records for meals whose date is in the past (and today's closed meals)
        foreach (var (sid, d, cutoff, sellerId) in scheduleRows)
        {
            if (!(cutoff <= now && d <= today)) continue;
            var required = await conn.ExecuteScalarAsync<int>(
                "SELECT COALESCE(SUM(c.quantity),0) FROM customer_meal_counts c " +
                "JOIN users u ON u.id=c.customer_id AND u.status='active' " +
                "WHERE c.food_schedule_id=@sid AND c.submission_status IN ('submitted','locked')",
                new { sid }, transaction);
            if (d == today)
            {
                await conn.ExecuteAsync(
                    "INSERT INTO food_preparation (seller_id, food_schedule_id, required_quantity, prepared_quantity, preparation_status) " +
                    "VALUES (@sellerId, @sid, @required, @required, 'preparing')",
                    new { sellerId, sid, required }, transaction);
                continue;
            }
            var prepared = required + rnd.Next(0, Math.Max(1, required / 8) + 1);
            var consumed = Math.Max(0, required - rnd.Next(0, 3));
            var remaining = prepared - consumed;
            var wasted = rnd.Next(0, remaining + 1);
            await conn.ExecuteAsync(
                "INSERT INTO food_preparation (seller_id, food_schedule_id, required_quantity, prepared_quantity, " +
                "consumed_quantity, remaining_quantity, wasted_quantity, preparation_status, remarks, updated_at) " +
                "VALUES (@sellerId, @sid, @required, @prepared, @consumed, @remaining, @wasted, 'completed', NULL, @updatedAt)",
                new { sellerId, sid, required, prepared, consumed, remaining, wasted, updatedAt = d.AddHours(21) }, transaction);
        }

        // QR check-ins for past meals: most people who applied came; a few no-shows and walk-ins
        var servingHour = new Dictionary<string, int> { ["Breakfast"] = 8, ["Lunch"] = 13, ["Dinner"] = 20 };
        foreach (var (sid, cid, d, meal) in applied.Where(a => a.Date < today))
        {
            if (rnd.NextDouble() > 0.88) continue; // no-show
            await conn.ExecuteAsync(
                "INSERT INTO meal_attendance (food_schedule_id, customer_id, checked_in_at, method) VALUES (@sid, @cid, @at, @method)",
                new { sid, cid, at = d.AddHours(servingHour[meal]).AddMinutes(rnd.Next(0, 75)), method = rnd.NextDouble() < 0.9 ? "qr" : "code" },
                transaction);
        }
        foreach (var (sid, d, _, _) in scheduleRows.Where(r => r.Date < today))
        {
            if (rnd.NextDouble() > 0.3) continue; // occasional walk-in (came without applying)
            var walkIn = customerIds.Where(c => c.Status == "active" && !applied.Any(a => a.Sid == sid && a.Cid == c.Id))
                                    .Select(c => c.Id).FirstOrDefault();
            if (walkIn == 0) continue;
            await conn.ExecuteAsync(
                "INSERT INTO meal_attendance (food_schedule_id, customer_id, checked_in_at, method) VALUES (@sid, @walkIn, @at, 'qr')",
                new { sid, walkIn, at = d.AddHours(servingHour[MealOrder[scheduleRows.FindIndex(r => r.Id == sid) % 3]]).AddMinutes(20) }, transaction);
        }

        // Welcome notifications
        foreach (var (cid, status) in customerIds.Where(c => c.Status == "active"))
            await conn.ExecuteAsync(
                "INSERT INTO notifications (user_id, message, link) VALUES (@cid, @message, @link)",
                new { cid, message = "Welcome to MealTrack! This week's food schedule is published.", link = "/customer/schedule" },
                transaction);
        foreach (var sellerId in new[] { seller1, seller2 })
            await conn.ExecuteAsync(
                "INSERT INTO notifications (user_id, message, link) VALUES (@sellerId, @message, @link)",
                new { sellerId, message = "You have been assigned preparation schedules for this week.", link = "/seller" },
                transaction);

        await transaction.CommitAsync();
        Console.WriteLine("Inserted demo users, 3 weeks of schedules, counts and preparation records.");
    }
}
