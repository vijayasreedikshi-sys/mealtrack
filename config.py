"""Application configuration (values come from the .env file)."""
import os
from datetime import timedelta

from dotenv import load_dotenv

BASE_DIR = os.path.abspath(os.path.dirname(__file__))
load_dotenv(os.path.join(BASE_DIR, ".env"))


class Config:
    SECRET_KEY = os.getenv("SECRET_KEY", "dev-only-change-me")

    DB_HOST = os.getenv("DB_HOST", "127.0.0.1")
    DB_PORT = int(os.getenv("DB_PORT", "3306"))
    DB_USER = os.getenv("DB_USER", "root")
    DB_PASSWORD = os.getenv("DB_PASSWORD", "")
    DB_NAME = os.getenv("DB_NAME", "mealtrack")

    DEBUG = os.getenv("FLASK_DEBUG", "1") == "1"

    # Session / cookie security
    SESSION_COOKIE_HTTPONLY = True
    SESSION_COOKIE_SAMESITE = "Lax"
    PERMANENT_SESSION_LIFETIME = timedelta(hours=8)
    WTF_CSRF_TIME_LIMIT = None  # token valid for the whole session

    # Business rules
    MAX_QTY_PER_MEAL = 20                 # must match CHECK constraint in schema.sql
    REMINDER_HOURS_BEFORE_CUTOFF = 2      # deadline reminder window
    SWEEP_INTERVAL_SECONDS = 60           # how often the lock/notify sweep may run
    PASSWORD_RESET_MINUTES = 30
