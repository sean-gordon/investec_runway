-- Enable TimescaleDB extension
CREATE EXTENSION IF NOT EXISTS timescaledb;

-- Create users table
CREATE TABLE IF NOT EXISTS users (
    id SERIAL PRIMARY KEY,
    username TEXT UNIQUE NOT NULL,
    password_hash TEXT NOT NULL,
    role TEXT DEFAULT 'User',
    is_system BOOLEAN DEFAULT FALSE,
    last_weekly_report_sent TIMESTAMPTZ,
    created_at TIMESTAMPTZ DEFAULT NOW()
);

-- Create user_settings table
CREATE TABLE IF NOT EXISTS user_settings (
    user_id INT PRIMARY KEY REFERENCES users(id) ON DELETE CASCADE,
    config JSONB NOT NULL
);

-- Create chat_history table
CREATE TABLE IF NOT EXISTS chat_history (
    id SERIAL PRIMARY KEY,
    user_id INT NOT NULL REFERENCES users(id) ON DELETE CASCADE,
    message_text TEXT NOT NULL,
    is_user BOOLEAN NOT NULL,
    timestamp TIMESTAMPTZ DEFAULT NOW()
);
CREATE INDEX IF NOT EXISTS idx_chat_history_user_date ON chat_history(user_id, timestamp DESC);

-- Create transactions table
CREATE TABLE IF NOT EXISTS transactions (
    id UUID NOT NULL,
    user_id INT NOT NULL REFERENCES users(id) ON DELETE CASCADE,
    account_id TEXT,
    transaction_date TIMESTAMPTZ NOT NULL,
    description TEXT,
    amount DECIMAL(18, 2),
    balance DECIMAL(18, 2),
    category TEXT,
    is_ai_processed BOOLEAN DEFAULT FALSE,
    notes TEXT
);

-- Create unique index required for hypertables and sync deduplication
CREATE UNIQUE INDEX IF NOT EXISTS ux_transactions_id_date_user ON transactions (id, transaction_date, user_id);

-- Convert to Hypertable partitioned by transaction_date
SELECT create_hypertable('transactions', 'transaction_date', if_not_exists => TRUE);
