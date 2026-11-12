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
- An owner can remove an item from their own list. Removing it also deletes its
  associated R2 images; failed storage cleanup is retried from a durable queue.
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
- The browser requests a short-lived, object-specific presigned PUT URL from
  the server, then uploads the selected original image unchanged directly to
  Cloudflare R2. Do not perform image decoding or compression as part of the
  upload request. The browser never receives R2 credentials.
- After verifying the stored object's size and content type, create a
  `christmas_list_items` row pointing at that original object and a queued
  `compression_jobs` row for that item. Return upload success without waiting
  for compression to finish.
- For a multi-image upload, report upload success or failure per file. A file
  is not considered uploaded until its original object and corresponding item
  and job records exist. Remove the object if the database write fails.
- Keep R2 objects private and serve images through the application or
  short-lived signed URLs. Do not expose R2 credentials to the browser.
- Configure bucket CORS to allow PUT only from the app's configured origin(s)
  and only the required content-type header.
- Compression must retain enough detail for screenshot text to remain readable.
  Keep the original visible while its job is pending or retrying, and show a
  clear processing or failed state to the list owner.
- Define configurable upload-size limits and compression settings during
  implementation; no specific limits were provided.

## Background image compression

- Implement compression as a durable database-backed queue, not as an
  untracked fire-and-forget task or an in-memory-only channel.
- Add one `compression_jobs` row per uploaded list item. At minimum it records
  the item ID, job status, attempt count, availability time, lease expiry,
  last error, and timestamps. Use explicit states such as `queued`,
  `processing`, `cleanup_pending`, `completed`, and `failed`.
- A hosted background worker polls PostgreSQL and atomically claims eligible
  jobs in a short transaction, using row locking (for example
  `FOR UPDATE SKIP LOCKED`) and a time-limited lease. The transaction must not
  remain open while the worker downloads or processes image data.
- The worker downloads the original object from R2, compresses it, and uploads
  the result under a different object key. It then uses a database transaction
  to point the list item at the compressed object and move the job to
  `cleanup_pending`. Only after that transaction commits does it remove the
  original from R2 and mark the job `completed`.
- Keep the original object until the item points to the compressed object. If
  processing fails, leave the original and item intact. Retry transient
  failures with bounded exponential backoff; after a configurable attempt
  limit, mark the job `failed` and retain a useful error for diagnosis/retry.
- Make retries safe: use stable job/item identities and predictable object
  keys or otherwise clean up objects left by a crash. A crash after switching
  the item pointer but before deleting the original must be recoverable from
  `cleanup_pending`.
- Workers must be safe to run on multiple app instances concurrently. A job
  whose lease expires after an instance stops must become claimable again.
- Refresh list status while compression is pending, or at minimum show the
  original immediately and update to the compressed image on the next list
  refresh. The owner must not see purchase state as part of this status.

### Render free-service behavior

- The app currently runs as a Render web service on a free plan. Render may
  suspend that process after a period without inbound requests. An in-process
  hosted worker cannot prevent suspension and is not a durable queue.
- Persisting jobs in PostgreSQL means suspension does not lose work. On app
  startup and while running, the worker should poll and resume queued or
  expired-lease jobs. If Render suspends mid-job, the lease expires and a later
  worker retries it.
- On the free plan, a queued job is only guaranteed to run while the service
  is awake. If the service suspends before the worker finishes, compression may
  wait until the next request wakes the service. This is acceptable for
  eventual processing, but does not guarantee prompt completion while idle.
- If prompt processing is required even when the web app is idle, run the
  worker on an always-on service or a separately scheduled worker that can
  reach the database and R2. Do not rely on a periodic in-process timer to
  wake a suspended Render service.

### Upload latency note

Direct-to-R2 upload avoids relaying the original through the app server, but
still takes as long as the browser-to-R2 network transfer. Record the browser
upload duration for comparison with the R2 and compression spans. Do not
include presigned URLs or object keys in telemetry.

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

- Use the existing hosted Blazor application. All database operations and
  privileged object-storage operations must run on the server. If direct
  browser-to-R2 upload is later used to reduce transfer latency, authorize it
  only with short-lived, object-specific presigned URLs; never expose storage
  credentials. The browser must not connect directly to Postgres.
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
ID/key, and the owning user's ID. It also needs a way to distinguish the
original, processing, and compressed image state (for example, an image
status), plus purchase tracking (nullable `purchased_by_user_id` referencing
`users`, plus a purchase timestamp). Enforce referential integrity and useful
indexes for fetching a user's list.

The `compression_jobs` table must reference `christmas_list_items` and include
the durable queue, retry, and lease fields described above. Index it for
efficiently finding queued jobs and expired leases.

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
2. A user can upload several screenshots in one operation; each successful
   original upload is stored unchanged in R2 and has one correctly owned list
   item and one durable compression job. The upload UI does not wait for
   compression.
3. Compression jobs survive app restarts/suspension, retry safely, update the
   item to the compressed object, and only then remove the original.
4. A user can browse another family member's screenshots, mark an item
   purchased, and see the purchaser's name and purchase status.
5. The owner of a list cannot see purchase status or purchaser identity for
   items on their own list.
6. The Add button remains fixed at the bottom of the viewport, is rectangular
   and wide, and has centered "Add" text; the layout remains usable on a phone.
7. An owner can filter another user's list to items not yet bought and remove
   items from their own list.
