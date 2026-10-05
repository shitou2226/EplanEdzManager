ALTER TABLE parts ADD COLUMN logical_identity TEXT NULL;
ALTER TABLE parts ADD COLUMN identity_kind TEXT NULL;

CREATE INDEX ix_parts_logical_identity ON parts(logical_identity);

CREATE TABLE my_saved_parts (
    id INTEGER PRIMARY KEY,
    stable_identity TEXT NOT NULL UNIQUE,
    identity_kind TEXT NOT NULL CHECK (identity_kind IN ('Primary', 'PackageFallback')),
    manufacturer TEXT NULL,
    part_number TEXT NULL,
    variant TEXT NULL,
    type_number_snapshot TEXT NULL,
    order_number_snapshot TEXT NULL,
    description_snapshot TEXT NULL,
    product_group_snapshot TEXT NULL,
    package_key_snapshot TEXT NULL,
    note TEXT NOT NULL DEFAULT '',
    favorite INTEGER NOT NULL DEFAULT 0 CHECK (favorite IN (0, 1)),
    created_utc TEXT NOT NULL,
    updated_utc TEXT NOT NULL
);

CREATE INDEX ix_my_saved_parts_favorite ON my_saved_parts(favorite);
CREATE INDEX ix_my_saved_parts_manufacturer ON my_saved_parts(manufacturer COLLATE NOCASE);
CREATE INDEX ix_my_saved_parts_part_number ON my_saved_parts(part_number COLLATE NOCASE);

CREATE TABLE my_saved_part_sources (
    id INTEGER PRIMARY KEY,
    saved_part_id INTEGER NOT NULL REFERENCES my_saved_parts(id) ON DELETE CASCADE,
    source_instance_identity TEXT NOT NULL,
    current_part_id INTEGER NULL REFERENCES parts(id) ON DELETE SET NULL,
    edz_path_snapshot TEXT NOT NULL,
    package_key_snapshot TEXT NULL,
    raw_metadata_reference_snapshot TEXT NOT NULL,
    edz_file_size_snapshot INTEGER NULL,
    edz_last_write_utc_ticks_snapshot INTEGER NULL,
    indexed_utc_snapshot TEXT NULL,
    resource_count_snapshot INTEGER NOT NULL DEFAULT 0,
    existing_resource_count_snapshot INTEGER NOT NULL DEFAULT 0,
    is_preferred INTEGER NOT NULL DEFAULT 0 CHECK (is_preferred IN (0, 1)),
    first_seen_utc TEXT NOT NULL,
    last_seen_utc TEXT NOT NULL,
    UNIQUE(saved_part_id, source_instance_identity)
);

CREATE INDEX ix_my_saved_part_sources_saved ON my_saved_part_sources(saved_part_id);
CREATE INDEX ix_my_saved_part_sources_current ON my_saved_part_sources(current_part_id);
CREATE INDEX ix_my_saved_part_sources_identity ON my_saved_part_sources(source_instance_identity);
CREATE UNIQUE INDEX ux_my_saved_part_sources_preferred
    ON my_saved_part_sources(saved_part_id)
    WHERE is_preferred = 1;

CREATE TABLE my_collections (
    id INTEGER PRIMARY KEY,
    name TEXT NOT NULL COLLATE NOCASE UNIQUE,
    created_utc TEXT NOT NULL,
    updated_utc TEXT NOT NULL
);

CREATE TABLE my_collection_items (
    collection_id INTEGER NOT NULL REFERENCES my_collections(id) ON DELETE CASCADE,
    saved_part_id INTEGER NOT NULL REFERENCES my_saved_parts(id) ON DELETE CASCADE,
    added_utc TEXT NOT NULL,
    PRIMARY KEY(collection_id, saved_part_id)
);

CREATE INDEX ix_my_collection_items_saved ON my_collection_items(saved_part_id);

CREATE TABLE my_tags (
    id INTEGER PRIMARY KEY,
    name TEXT NOT NULL COLLATE NOCASE UNIQUE,
    created_utc TEXT NOT NULL,
    updated_utc TEXT NOT NULL
);

CREATE TABLE my_saved_part_tags (
    tag_id INTEGER NOT NULL REFERENCES my_tags(id) ON DELETE CASCADE,
    saved_part_id INTEGER NOT NULL REFERENCES my_saved_parts(id) ON DELETE CASCADE,
    added_utc TEXT NOT NULL,
    PRIMARY KEY(tag_id, saved_part_id)
);

CREATE INDEX ix_my_saved_part_tags_saved ON my_saved_part_tags(saved_part_id);

CREATE VIRTUAL TABLE my_saved_parts_fts USING fts5(
    manufacturer,
    part_number,
    type_number,
    description,
    note,
    content='my_saved_parts',
    content_rowid='id',
    tokenize='unicode61 remove_diacritics 2'
);

CREATE TRIGGER my_saved_parts_fts_insert AFTER INSERT ON my_saved_parts BEGIN
    INSERT INTO my_saved_parts_fts(rowid, manufacturer, part_number, type_number, description, note)
    VALUES (new.id, new.manufacturer, new.part_number, new.type_number_snapshot, new.description_snapshot, new.note);
END;

CREATE TRIGGER my_saved_parts_fts_delete AFTER DELETE ON my_saved_parts BEGIN
    INSERT INTO my_saved_parts_fts(my_saved_parts_fts, rowid, manufacturer, part_number, type_number, description, note)
    VALUES ('delete', old.id, old.manufacturer, old.part_number, old.type_number_snapshot, old.description_snapshot, old.note);
END;

CREATE TRIGGER my_saved_parts_fts_update AFTER UPDATE ON my_saved_parts BEGIN
    INSERT INTO my_saved_parts_fts(my_saved_parts_fts, rowid, manufacturer, part_number, type_number, description, note)
    VALUES ('delete', old.id, old.manufacturer, old.part_number, old.type_number_snapshot, old.description_snapshot, old.note);
    INSERT INTO my_saved_parts_fts(rowid, manufacturer, part_number, type_number, description, note)
    VALUES (new.id, new.manufacturer, new.part_number, new.type_number_snapshot, new.description_snapshot, new.note);
END;
