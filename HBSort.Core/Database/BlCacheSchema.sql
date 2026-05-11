-- BL-Cache-Schema (bl_cache.db)
-- Wird beim ersten Start vom BlCacheRepository angewendet.

CREATE TABLE IF NOT EXISTS bl_items (
    item_type TEXT NOT NULL,
    item_no TEXT NOT NULL,
    name TEXT NOT NULL,
    year_released INTEGER,
    image_url TEXT,
    weight REAL,
    dim_x REAL,
    dim_y REAL,
    dim_z REAL,
    category_id INTEGER,
    json_full TEXT,
    data_completeness TEXT NOT NULL,
    fetched_at TEXT NOT NULL,
    PRIMARY KEY (item_type, item_no)
);

CREATE TABLE IF NOT EXISTS bl_subsets (
    parent_type TEXT NOT NULL,
    parent_no TEXT NOT NULL,
    item_type TEXT NOT NULL,
    item_no TEXT NOT NULL,
    color_id INTEGER NOT NULL,
    quantity INTEGER NOT NULL,
    extra_quantity INTEGER NOT NULL DEFAULT 0,
    is_alternate INTEGER NOT NULL DEFAULT 0,
    is_counterpart INTEGER NOT NULL DEFAULT 0,
    match_id INTEGER NOT NULL DEFAULT 0,
    fetched_at TEXT NOT NULL,
    is_from_supersets INTEGER NOT NULL DEFAULT 0,
    PRIMARY KEY (parent_type, parent_no, item_type, item_no, color_id, match_id)
);

CREATE TABLE IF NOT EXISTS bl_colors (
    color_id INTEGER PRIMARY KEY,
    name TEXT NOT NULL,
    rgb TEXT,
    type TEXT,
    fetched_at TEXT NOT NULL
);

CREATE INDEX IF NOT EXISTS idx_bl_subsets_parent ON bl_subsets(parent_type, parent_no);
CREATE INDEX IF NOT EXISTS idx_bl_subsets_item ON bl_subsets(item_type, item_no, color_id);

-- Phase 5: Welche Farben gibt es ein Teil ueberhaupt? (BL: GetKnownColors)
-- Cached pro Teil die Liste der bekannten BL-Color-IDs.
CREATE TABLE IF NOT EXISTS bl_known_colors (
    part_no TEXT NOT NULL,
    color_id INTEGER NOT NULL,
    fetched_at TEXT NOT NULL,
    PRIMARY KEY (part_no, color_id)
);
CREATE INDEX IF NOT EXISTS idx_bl_known_colors_part ON bl_known_colors(part_no);

-- API-Call-Logging fuer eigenen Rate-Limiter (Phase R2.5).
-- Eintrag pro echtem BL-Call (Brickognize zaehlt NICHT mit).
CREATE TABLE IF NOT EXISTS api_call_log (
    id INTEGER PRIMARY KEY AUTOINCREMENT,
    timestamp TEXT NOT NULL,
    method TEXT NOT NULL,
    item_type TEXT,
    item_no TEXT,
    response_time_ms INTEGER,
    status_code INTEGER,
    success INTEGER NOT NULL
);
CREATE INDEX IF NOT EXISTS idx_api_call_log_timestamp ON api_call_log(timestamp);

-- Phase 8: Preis-Cache. PRIMARY KEY ueber alle Filter-Felder, damit
-- verschiedene Region/Currency/Condition-Kombis nebeneinander gecached werden.
--
-- UX X.34 v0.1.20: vat_mode-Spalte angehaengt (NICHT in PK aus Hotfix-
-- Pragmatismus - kein riskanter SQLite-Tabellen-Umbau). Bestand-Eintraege
-- bekommen via ALTER TABLE Default 'N' (= alter Netto-Default). Neue
-- Lookups mit vat='Y' filtern im WHERE und bekommen Cache-Miss bei
-- Mismatch -> frischer API-Call ersetzt den alten Eintrag (INSERT OR
-- REPLACE auf alter PK). User kann nicht Y und N parallel cachen,
-- aber Wechsel funktioniert (Refresh per Lookup).
CREATE TABLE IF NOT EXISTS bl_prices (
    item_type TEXT NOT NULL,
    item_no TEXT NOT NULL,
    color_id INTEGER NOT NULL DEFAULT 0,
    guide_type TEXT NOT NULL,        -- 'sold' | 'stock'
    new_or_used TEXT NOT NULL,       -- 'N' | 'U'
    region TEXT NOT NULL DEFAULT '',
    currency TEXT NOT NULL DEFAULT 'EUR',
    vat_mode TEXT NOT NULL DEFAULT 'N',  -- UX X.34: 'Y' (brutto, neuer Default), 'N' (netto, alt), 'O' (NO)
    min_price REAL,
    avg_price REAL,
    qty_avg_price REAL,
    max_price REAL,
    unit_quantity INTEGER DEFAULT 0,
    total_quantity INTEGER DEFAULT 0,
    fetched_at TEXT NOT NULL,
    PRIMARY KEY (item_type, item_no, color_id, guide_type, new_or_used, region, currency)
);
CREATE INDEX IF NOT EXISTS idx_bl_prices_fetched ON bl_prices(fetched_at);
