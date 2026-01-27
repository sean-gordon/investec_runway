-- Enable TimescaleDB extension
CREATE EXTENSION IF NOT EXISTS timescaledb;

-- Create transactions table
CREATE TABLE IF NOT EXISTS transactions (
    id UUID PRIMARY KEY,
    transaction_date TIMESTAMPTZ NOT NULL,
    description TEXT,
    amount DECIMAL(18, 2),
    balance DECIMAL(18, 2),
    category TEXT,
    is_ai_processed BOOLEAN DEFAULT FALSE
);

-- Convert to Hypertable partitioned by transaction_date
SELECT create_hypertable('transactions', 'transaction_date', if_not_exists => TRUE);
