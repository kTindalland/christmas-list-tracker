INSERT INTO users (display_name)
VALUES
    ('Kai'),
    ('Stacey'),
    ('Sarah'),
    ('Jessie')
ON CONFLICT (display_name) DO NOTHING;
