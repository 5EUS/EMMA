ALTER TABLE media_chapters ADD COLUMN uploader_groups TEXT;

UPDATE media_chapters
SET uploader_groups = '[]'
WHERE uploader_groups IS NULL;
