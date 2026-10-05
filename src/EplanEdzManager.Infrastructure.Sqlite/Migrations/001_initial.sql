CREATE TABLE indexed_directories (
    id INTEGER PRIMARY KEY,
    path TEXT NOT NULL COLLATE NOCASE UNIQUE,
    recursive INTEGER NOT NULL CHECK (recursive IN (0, 1)),
    enabled INTEGER NOT NULL DEFAULT 1 CHECK (enabled IN (0, 1)),
    added_utc TEXT NOT NULL,
    last_scan_utc TEXT NULL
);

CREATE TABLE edz_files (
    id INTEGER PRIMARY KEY,
    directory_id INTEGER NOT NULL REFERENCES indexed_directories(id) ON DELETE CASCADE,
    path TEXT NOT NULL COLLATE NOCASE UNIQUE,
    file_size INTEGER NOT NULL,
    last_write_utc_ticks INTEGER NOT NULL,
    indexed_utc TEXT NOT NULL,
    status TEXT NOT NULL CHECK (status IN ('Indexed', 'Failed')),
    error TEXT NULL
);

CREATE INDEX ix_edz_files_directory_id ON edz_files(directory_id);

CREATE TABLE parts (
    id INTEGER PRIMARY KEY,
    edz_file_id INTEGER NOT NULL REFERENCES edz_files(id) ON DELETE CASCADE,
    manufacturer TEXT NULL,
    part_number TEXT NULL,
    type_number TEXT NULL,
    order_number TEXT NULL,
    description TEXT NULL,
    product_group TEXT NULL,
    variant TEXT NULL,
    package_key TEXT NULL,
    raw_metadata_reference TEXT NOT NULL,
    resource_count INTEGER NOT NULL DEFAULT 0,
    existing_resource_count INTEGER NOT NULL DEFAULT 0
);

CREATE INDEX ix_parts_edz_file_id ON parts(edz_file_id);
CREATE INDEX ix_parts_manufacturer ON parts(manufacturer COLLATE NOCASE);
CREATE INDEX ix_parts_part_number ON parts(part_number COLLATE NOCASE);
CREATE INDEX ix_parts_type_number ON parts(type_number COLLATE NOCASE);

CREATE TABLE resources (
    id INTEGER PRIMARY KEY,
    part_id INTEGER NOT NULL REFERENCES parts(id) ON DELETE CASCADE,
    resource_type TEXT NULL,
    name TEXT NULL,
    raw_locator TEXT NULL,
    archive_path TEXT NULL,
    exists_in_archive INTEGER NOT NULL CHECK (exists_in_archive IN (0, 1)),
    size INTEGER NULL,
    reference_count INTEGER NOT NULL DEFAULT 0
);

CREATE INDEX ix_resources_part_id ON resources(part_id);
CREATE INDEX ix_resources_exists ON resources(exists_in_archive);

CREATE VIRTUAL TABLE parts_fts USING fts5(
    manufacturer,
    part_number,
    type_number,
    description,
    content='parts',
    content_rowid='id',
    tokenize='unicode61 remove_diacritics 2'
);

CREATE TRIGGER parts_fts_insert AFTER INSERT ON parts BEGIN
    INSERT INTO parts_fts(rowid, manufacturer, part_number, type_number, description)
    VALUES (new.id, new.manufacturer, new.part_number, new.type_number, new.description);
END;

CREATE TRIGGER parts_fts_delete AFTER DELETE ON parts BEGIN
    INSERT INTO parts_fts(parts_fts, rowid, manufacturer, part_number, type_number, description)
    VALUES ('delete', old.id, old.manufacturer, old.part_number, old.type_number, old.description);
END;

CREATE TRIGGER parts_fts_update AFTER UPDATE ON parts BEGIN
    INSERT INTO parts_fts(parts_fts, rowid, manufacturer, part_number, type_number, description)
    VALUES ('delete', old.id, old.manufacturer, old.part_number, old.type_number, old.description);
    INSERT INTO parts_fts(rowid, manufacturer, part_number, type_number, description)
    VALUES (new.id, new.manufacturer, new.part_number, new.type_number, new.description);
END;
