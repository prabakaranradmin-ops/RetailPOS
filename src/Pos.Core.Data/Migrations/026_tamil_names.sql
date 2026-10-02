-- Finding an item by what the customer calls it: in Tamil, or in any of the ways a Tamil word is
-- spelled in English. The catalogue gains a Tamil name, and two keys the search reads instead of
-- the names themselves: how the English name sounds, and how the Tamil name sounds, each folded so
-- that paruppu, baruppu and பருப்பு are the same.
--
-- The keys are worked out by the application (SoundKey), not here: SQLite has no notion of Tamil
-- letters. Existing items get theirs the first time the till starts after this, which is the
-- moment it fills any key that is missing.
ALTER TABLE items ADD COLUMN name_ta    TEXT NULL;
ALTER TABLE items ADD COLUMN sound_name TEXT NULL;
ALTER TABLE items ADD COLUMN sound_ta   TEXT NULL;

-- Both keys in one index with the active flag, so the search scans the index alone, as the name
-- search scans its own, rather than the table.
CREATE INDEX ix_items_active_sound ON items (is_active, sound_name, sound_ta);
