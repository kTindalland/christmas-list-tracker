CREATE TABLE users (
    id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    display_name TEXT NOT NULL UNIQUE,
    created_at TIMESTAMPTZ NOT NULL DEFAULT now(),
    CONSTRAINT users_display_name_not_blank CHECK (length(trim(display_name)) > 0)
);

CREATE TABLE christmas_list_items (
    id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    user_id UUID NOT NULL REFERENCES users(id),
    object_id TEXT NOT NULL UNIQUE,
    created_at TIMESTAMPTZ NOT NULL DEFAULT now(),
    purchased_by_user_id UUID REFERENCES users(id),
    purchased_at TIMESTAMPTZ,
    CONSTRAINT christmas_list_items_object_id_not_blank CHECK (length(trim(object_id)) > 0),
    CONSTRAINT christmas_list_items_purchase_state_consistent CHECK (
        (purchased_by_user_id IS NULL AND purchased_at IS NULL)
        OR (purchased_by_user_id IS NOT NULL AND purchased_at IS NOT NULL)
    ),
    CONSTRAINT christmas_list_items_owner_cannot_purchase CHECK (
        purchased_by_user_id IS NULL OR purchased_by_user_id <> user_id
    )
);

CREATE INDEX christmas_list_items_user_created_idx
    ON christmas_list_items (user_id, created_at DESC);
