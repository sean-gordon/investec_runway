-- Enable TimescaleDB extension
CREATE EXTENSION IF NOT EXISTS timescaledb;

-- Create transactions table
CREATE TABLE IF NOT EXISTS transactions (
    id UUID PRIMARY KEY,
    account_id TEXT,
    transaction_date TIMESTAMPTZ NOT NULL,
    description TEXT,
    amount DECIMAL(18, 2),
    balance DECIMAL(18, 2),
    category TEXT,
    is_ai_processed BOOLEAN DEFAULT FALSE
);

-- Convert to Hypertable partitioned by transaction_date
SELECT create_hypertable('transactions', 'transaction_date', if_not_exists => TRUE);

-- Create System Config table (Singleton pattern via check constraint)
CREATE TABLE IF NOT EXISTS system_config (
    id INT PRIMARY KEY DEFAULT 1 CHECK (id = 1),
    config JSONB NOT NULL
);
