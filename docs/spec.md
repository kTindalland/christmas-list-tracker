# Christmas List Tracker - Product Specification

## Purpose

A mobile-first family application for sharing Christmas wish lists made from
uploaded screenshots. Family members can browse each other's lists and
coordinate purchases without revealing purchase status to the owner of a list.

This application does not provide proper authentication. Selecting a predefined
user is a convenience for identifying a family member, not proof of identity.
Do not use it to store sensitive information.

## Users and identity

- The user picker loads available people from the database `users` table.
- Selecting a user sets the current family-member identity and persists its
  user ID in a browser cookie so it survives reloads and return visits.
- On startup, the application validates the saved ID against `users`. If it is
  missing or no longer valid, show the user picker.
- A sign-out action remains available in the pull-out menu. It clears the
  identity cookie and returns to the user picker.
- The server must treat the cookie as untrusted input and validate that its ID
  exists. Because there is no authentication, anyone with access to the
  application can select another user.

## Christmas lists

- A signed-in family member can view their own list and choose another user's
  list to browse.
- A list item is a screenshot image. Each uploaded image creates one item.
- Users can select and upload multiple images in one operation.
- On the owner's list, do not display whether an item has been purchased or
  who purchased it.
- On another user's list, show whether each item has been purchased and, when
  purchased, the purchasing user's display name.
- A user can mark an item on another person's list as purchased and undo their
  own purchase mark. The database should allow at most one current purchaser
  per item; the UI must clearly handle an item that another user has already
  marked.

## Image upload and storage

- Accept common screenshot image formats (at minimum JPEG and PNG); reject
  unsupported or invalid files with a visible per-file error.
- Compress each image before storing it in Cloudflare R2. Compression must
  retain enough detail for text in screenshots to remain readable.
- Store each image in R2 and create a corresponding row in
  `christmas_list_items`, associating the R2 object ID/key with the uploading
  user's ID.
- For a multi-image upload, report success or failure for each image. Do not
  create a database row for an image that failed to upload; clean up an uploaded
  object if its database record cannot be created.
- Keep R2 objects private and serve images through the application or
  short-lived signed URLs. Do not expose R2 credentials to the browser.
- Define configurable upload-size limits and compression settings during
  implementation; no specific limits were provided.

## Purchase tracking and privacy

- Store purchase state against the list item, including the purchaser's user ID
  and the time it was marked purchased.
- Only show this state to users browsing someone else's list. The owner's list
  response and UI must omit purchase state, including the purchaser identity.
- The server must enforce this visibility rule; hiding a field only in the UI is
  insufficient.
- Persist purchase changes in the database so list status is consistent across
  users and devices.

## Mobile experience

- Design for narrow mobile screens first, with controls and text sized for
  touch interaction.
- Place a wide, rectangular **Add** button at the bottom of the current user's
  list. Center the text "Add" in the button. It should take up most of the
  available list width and open a multi-image picker/camera-capable upload
  flow.
- Keep the pull-out menu available for navigating lists and signing out.
- Provide clear loading, empty-list, upload-progress, and error states.

## Technical requirements

- Use the existing hosted Blazor application. All database and object-storage
  operations must run on the server; the browser must not connect directly to
  Postgres or receive storage secrets.
- Use Dapper for PostgreSQL queries, with parameterized SQL.
- Read the PostgreSQL connection string from
  `ConnectionStrings:DefaultConnection`. `deploy/terraform/main.tf` currently
  configures it for the Render service as
  `ConnectionStrings__DefaultConnection`.
- Use Cloudflare R2's S3-compatible API. `deploy/terraform/main.tf` currently
  configures `R2__ServiceUrl` and `R2__BucketName`. It does not currently
  configure R2 access credentials; provide those as server-only environment
  settings/managed secrets and wire them through Terraform without committing
  secret values.
- Never include actual connection strings, API tokens, passwords, or storage
  credentials in source control or client configuration.

## Data model expectations

The database tables do not exist yet. Create a `database/migrations/` folder
containing ordered, versioned PostgreSQL `.sql` migration files that create the
required schema, including `users` and `christmas_list_items`.

The `users` table supplies selectable family members. The
`christmas_list_items` table must at minimum associate an item ID, its R2 object
ID/key, and the owning user's ID. It also needs purchase tracking (nullable
`purchased_by_user_id` referencing `users`, plus a purchase timestamp). Enforce
referential integrity and useful indexes for fetching a user's list.

Provide a migration runner that can be run repeatedly against the database with
all migration files present:

- Each migration filename has a unique, monotonically increasing numeric
  version, such as `0001_create_users.sql`.
- A database migration-version table records the current schema version. On a
  new database, create and initialize this table at version `0`.
- On each run, discover and sort all migration files, then apply only files
  whose version is greater than the recorded current version.
- Apply migrations in ascending order. Advance the recorded version only after
  that migration succeeds; run each migration and its version update in a
  transaction so a failure leaves the version unchanged and can be retried.
- If no migration has a version higher than the recorded version, make no
  schema changes.
- Keep previously applied migration files unchanged; add a new, higher-version
  file for later schema changes.

## Acceptance criteria

1. A family member can select a database-backed user, return later without
   selecting again, and sign out from the pull-out menu.
2. A user can upload several screenshots in one operation; images are compressed
   and stored in R2, with one correctly owned database item per successful
   upload.
3. A user can browse another family member's screenshots, mark an item
   purchased, and see the purchaser's name and purchase status.
4. The owner of a list cannot see purchase status or purchaser identity for
   items on their own list.
5. The Add button appears at the bottom of the list, is rectangular and wide,
   and has centered "Add" text; the layout remains usable on a phone.
