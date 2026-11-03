-- =====================================================================
-- MealTrack – Food Count Tracking & Meal Planning System
-- MySQL 8.0 schema. Run through database/init_db.py (creates the DB).
-- =====================================================================

SET NAMES utf8mb4;
SET FOREIGN_KEY_CHECKS = 0;

-- ---------------------------------------------------------------------
-- users: customers, admins and sellers share one table (role column)
-- ---------------------------------------------------------------------
CREATE TABLE users (
    id              INT UNSIGNED AUTO_INCREMENT PRIMARY KEY,
    name            VARCHAR(100)  NOT NULL,
    email           VARCHAR(150)  NOT NULL,
    password_hash   VARCHAR(255)  NOT NULL,
    role            ENUM('customer','admin','seller') NOT NULL DEFAULT 'customer',
    phone           VARCHAR(15)   NULL,
    room_no         VARCHAR(20)   NULL,
    status          ENUM('active','inactive') NOT NULL DEFAULT 'active',
    created_at      DATETIME      NOT NULL DEFAULT CURRENT_TIMESTAMP,
    updated_at      DATETIME      NULL ON UPDATE CURRENT_TIMESTAMP,
    CONSTRAINT uq_users_email UNIQUE (email),
    INDEX idx_users_role_status (role, status)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_unicode_ci;

-- ---------------------------------------------------------------------
-- meal_types: Breakfast / Lunch / Dinner + default cutoff rule
--   cutoff = (schedule_date + default_cutoff_day_offset) at default_cutoff_time
-- ---------------------------------------------------------------------
CREATE TABLE meal_types (
    id                         TINYINT UNSIGNED AUTO_INCREMENT PRIMARY KEY,
    meal_name                  VARCHAR(30) NOT NULL,
    sort_order                 TINYINT     NOT NULL DEFAULT 0,
    default_cutoff_day_offset  TINYINT     NOT NULL DEFAULT 0,
    default_cutoff_time        TIME        NOT NULL DEFAULT '09:00:00',
    CONSTRAINT uq_meal_types_name UNIQUE (meal_name),
    CONSTRAINT chk_meal_offset CHECK (default_cutoff_day_offset BETWEEN -7 AND 0)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_unicode_ci;

-- ---------------------------------------------------------------------
-- food_items: master list of dishes
-- ---------------------------------------------------------------------
CREATE TABLE food_items (
    id           INT UNSIGNED AUTO_INCREMENT PRIMARY KEY,
    food_name    VARCHAR(100)  NOT NULL,
    description  VARCHAR(255)  NULL,
    unit         VARCHAR(20)   NOT NULL DEFAULT 'plate',
    price        DECIMAL(8,2)  NULL,
    status       ENUM('active','inactive') NOT NULL DEFAULT 'active',
    created_at   DATETIME      NOT NULL DEFAULT CURRENT_TIMESTAMP,
    CONSTRAINT uq_food_items_name UNIQUE (food_name),
    CONSTRAINT chk_food_price CHECK (price IS NULL OR price >= 0)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_unicode_ci;

-- ---------------------------------------------------------------------
-- food_schedule: one food per meal per date (published by admin)
-- ---------------------------------------------------------------------
CREATE TABLE food_schedule (
    id             INT UNSIGNED AUTO_INCREMENT PRIMARY KEY,
    schedule_date  DATE              NOT NULL,
    meal_type_id   TINYINT UNSIGNED  NOT NULL,
    food_item_id   INT UNSIGNED      NOT NULL,
    cutoff_time    DATETIME          NOT NULL,
    is_published   TINYINT(1)        NOT NULL DEFAULT 1,
    notes          VARCHAR(255)      NULL,
    qr_secret      CHAR(32)          NULL,   -- key for this meal's check-in QR (every meal-day is a new row, so a new QR daily)
    created_by     INT UNSIGNED      NULL,
    created_at     DATETIME          NOT NULL DEFAULT CURRENT_TIMESTAMP,
    updated_at     DATETIME          NULL ON UPDATE CURRENT_TIMESTAMP,
    CONSTRAINT uq_schedule_date_meal UNIQUE (schedule_date, meal_type_id),
    CONSTRAINT fk_schedule_meal  FOREIGN KEY (meal_type_id) REFERENCES meal_types(id),
    CONSTRAINT fk_schedule_food  FOREIGN KEY (food_item_id) REFERENCES food_items(id),
    CONSTRAINT fk_schedule_admin FOREIGN KEY (created_by)   REFERENCES users(id) ON DELETE SET NULL,
    INDEX idx_schedule_date (schedule_date),
    INDEX idx_schedule_cutoff (cutoff_time)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_unicode_ci;

-- ---------------------------------------------------------------------
-- customer_meal_counts: a customer's required quantity for a scheduled meal
--   quantity 0 = submitted, meal not required (still a valid submission)
--   UNIQUE(customer, schedule) => updates never create duplicates
-- ---------------------------------------------------------------------
CREATE TABLE customer_meal_counts (
    id                 INT UNSIGNED AUTO_INCREMENT PRIMARY KEY,
    customer_id        INT UNSIGNED NOT NULL,
    food_schedule_id   INT UNSIGNED NOT NULL,
    quantity           TINYINT UNSIGNED NOT NULL DEFAULT 0,
    submission_status  ENUM('pending','submitted','locked','cancelled') NOT NULL DEFAULT 'pending',
    submitted_at       DATETIME NULL,
    updated_at         DATETIME NULL,
    CONSTRAINT uq_count_customer_schedule UNIQUE (customer_id, food_schedule_id),
    CONSTRAINT fk_count_customer FOREIGN KEY (customer_id)      REFERENCES users(id) ON DELETE CASCADE,
    CONSTRAINT fk_count_schedule FOREIGN KEY (food_schedule_id) REFERENCES food_schedule(id) ON DELETE CASCADE,
    CONSTRAINT chk_count_qty CHECK (quantity BETWEEN 0 AND 1),   -- 1 = eating, 0 = not eating (one portion per person per meal)
    INDEX idx_count_schedule_status (food_schedule_id, submission_status)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_unicode_ci;

-- Audit trail of quantity changes made before the cutoff
CREATE TABLE count_change_log (
    id          INT UNSIGNED AUTO_INCREMENT PRIMARY KEY,
    count_id    INT UNSIGNED NOT NULL,
    old_qty     TINYINT UNSIGNED NULL,
    new_qty     TINYINT UNSIGNED NOT NULL,
    changed_at  DATETIME NOT NULL,
    CONSTRAINT fk_log_count FOREIGN KEY (count_id) REFERENCES customer_meal_counts(id) ON DELETE CASCADE,
    INDEX idx_log_changed (changed_at)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_unicode_ci;

-- ---------------------------------------------------------------------
-- Seller assignments
-- ---------------------------------------------------------------------
CREATE TABLE seller_assignments (
    id                  INT UNSIGNED AUTO_INCREMENT PRIMARY KEY,
    seller_id           INT UNSIGNED NOT NULL,
    food_schedule_id    INT UNSIGNED NOT NULL,
    can_view_customers  TINYINT(1)   NOT NULL DEFAULT 0,
    assigned_at         DATETIME     NOT NULL DEFAULT CURRENT_TIMESTAMP,
    CONSTRAINT uq_assignment UNIQUE (seller_id, food_schedule_id),
    CONSTRAINT fk_assign_seller   FOREIGN KEY (seller_id)        REFERENCES users(id) ON DELETE CASCADE,
    CONSTRAINT fk_assign_schedule FOREIGN KEY (food_schedule_id) REFERENCES food_schedule(id) ON DELETE CASCADE,
    INDEX idx_assign_schedule (food_schedule_id)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_unicode_ci;

-- Default responsibility: e.g. seller X prepares every Lunch.
-- New schedules for that meal type are auto-assigned to these sellers.
CREATE TABLE seller_meal_defaults (
    seller_id           INT UNSIGNED     NOT NULL,
    meal_type_id        TINYINT UNSIGNED NOT NULL,
    can_view_customers  TINYINT(1)       NOT NULL DEFAULT 0,
    PRIMARY KEY (seller_id, meal_type_id),
    CONSTRAINT fk_smd_seller FOREIGN KEY (seller_id)    REFERENCES users(id) ON DELETE CASCADE,
    CONSTRAINT fk_smd_meal   FOREIGN KEY (meal_type_id) REFERENCES meal_types(id) ON DELETE CASCADE
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_unicode_ci;

-- ---------------------------------------------------------------------
-- food_preparation: what the seller actually cooked vs. required
-- ---------------------------------------------------------------------
CREATE TABLE food_preparation (
    id                  INT UNSIGNED AUTO_INCREMENT PRIMARY KEY,
    seller_id           INT UNSIGNED NOT NULL,
    food_schedule_id    INT UNSIGNED NOT NULL,
    required_quantity   INT UNSIGNED NOT NULL DEFAULT 0,
    prepared_quantity   INT UNSIGNED NOT NULL DEFAULT 0,
    consumed_quantity   INT UNSIGNED NOT NULL DEFAULT 0,
    remaining_quantity  INT UNSIGNED NOT NULL DEFAULT 0,
    wasted_quantity     INT UNSIGNED NOT NULL DEFAULT 0,
    preparation_status  ENUM('not_started','preparing','completed') NOT NULL DEFAULT 'not_started',
    remarks             VARCHAR(255) NULL,
    updated_at          DATETIME NOT NULL DEFAULT CURRENT_TIMESTAMP ON UPDATE CURRENT_TIMESTAMP,
    CONSTRAINT uq_prep_schedule_seller UNIQUE (food_schedule_id, seller_id),
    CONSTRAINT fk_prep_seller   FOREIGN KEY (seller_id)        REFERENCES users(id) ON DELETE CASCADE,
    CONSTRAINT fk_prep_schedule FOREIGN KEY (food_schedule_id) REFERENCES food_schedule(id) ON DELETE CASCADE,
    CONSTRAINT chk_prep_balance CHECK (consumed_quantity + remaining_quantity <= prepared_quantity
                                       AND wasted_quantity <= remaining_quantity),
    INDEX idx_prep_updated (updated_at)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_unicode_ci;

-- ---------------------------------------------------------------------
-- meal_attendance: who actually came to eat (QR scan, typed code, or marked by staff)
--   applied (count > 0) but no row here => no-show (food cooked for nobody)
--   row here but no count > 0           => walk-in
-- ---------------------------------------------------------------------
CREATE TABLE meal_attendance (
    id                INT UNSIGNED AUTO_INCREMENT PRIMARY KEY,
    food_schedule_id  INT UNSIGNED NOT NULL,
    customer_id       INT UNSIGNED NOT NULL,
    checked_in_at     DATETIME     NOT NULL,
    method            ENUM('qr','code','manual') NOT NULL DEFAULT 'qr',
    marked_by         INT UNSIGNED NULL,
    CONSTRAINT uq_attendance UNIQUE (food_schedule_id, customer_id),
    CONSTRAINT fk_att_schedule FOREIGN KEY (food_schedule_id) REFERENCES food_schedule(id) ON DELETE CASCADE,
    CONSTRAINT fk_att_customer FOREIGN KEY (customer_id)      REFERENCES users(id) ON DELETE CASCADE,
    CONSTRAINT fk_att_marker   FOREIGN KEY (marked_by)        REFERENCES users(id) ON DELETE SET NULL,
    INDEX idx_att_customer (customer_id)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_unicode_ci;

-- ---------------------------------------------------------------------
-- notifications (in-app). dedupe_key prevents repeat reminders.
-- ---------------------------------------------------------------------
CREATE TABLE notifications (
    id          INT UNSIGNED AUTO_INCREMENT PRIMARY KEY,
    user_id     INT UNSIGNED NOT NULL,
    message     VARCHAR(500) NOT NULL,
    link        VARCHAR(255) NULL,
    dedupe_key  VARCHAR(100) NULL,
    is_read     TINYINT(1)   NOT NULL DEFAULT 0,
    created_at  DATETIME     NOT NULL DEFAULT CURRENT_TIMESTAMP,
    CONSTRAINT uq_notification_dedupe UNIQUE (dedupe_key),
    CONSTRAINT fk_notification_user FOREIGN KEY (user_id) REFERENCES users(id) ON DELETE CASCADE,
    INDEX idx_notification_user (user_id, is_read, created_at)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_unicode_ci;

-- ---------------------------------------------------------------------
-- password_resets: one-time hashed tokens
-- ---------------------------------------------------------------------
CREATE TABLE password_resets (
    id          INT UNSIGNED AUTO_INCREMENT PRIMARY KEY,
    user_id     INT UNSIGNED NOT NULL,
    token_hash  CHAR(64)     NOT NULL,
    expires_at  DATETIME     NOT NULL,
    used        TINYINT(1)   NOT NULL DEFAULT 0,
    created_at  DATETIME     NOT NULL DEFAULT CURRENT_TIMESTAMP,
    CONSTRAINT uq_reset_token UNIQUE (token_hash),
    CONSTRAINT fk_reset_user FOREIGN KEY (user_id) REFERENCES users(id) ON DELETE CASCADE
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_unicode_ci;

SET FOREIGN_KEY_CHECKS = 1;
