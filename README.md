# Christmas List Tracker

A mobile-first family app for sharing Christmas wish lists made from screenshot
images. The hosted Blazor server reads and writes PostgreSQL with Dapper,
uploads originals directly from the browser to private Cloudflare R2, and
compresses them in a durable background queue.

## Database setup

Set `ConnectionStrings__DefaultConnection` to the Supabase PostgreSQL connection
string, preferably Supabase's **Session pooler** connection string (port 5432),
then run the migration runner from the repository root. The Render Terraform
configuration uses the session pooler because Render cannot reliably reach the
direct database endpoint over IPv6, and the migration runner requires a
persistent session for its advisory lock.

```powershell
dotnet run --project src\ChristmasListTracker -- --migrate
```

The runner discovers `database/migrations/*.sql`, creates the
`schema_migrations` version record if needed, and applies only migrations newer
than the current database version. It is safe to run repeatedly. Add family
members to the new `users` table using the Supabase SQL editor, for example:

```sql
INSERT INTO users (display_name)
VALUES ('Family member name');
```

Run the migrations before deploying code that uses the new upload and
compression-job tables.

## Run the application

Configure these server-side environment variables before starting the app:

- `ConnectionStrings__DefaultConnection`
- `R2__ServiceUrl`
- `R2__BucketName`
- `R2__AccessKeyId`
- `R2__SecretAccessKey`

Then run:

```powershell
dotnet run --project src\ChristmasListTracker
```

The identity picker is not authentication: anyone with access to the app can
choose any family member. The selected user ID is stored in a browser cookie.
Do not store sensitive information in this application.

## Terraform configuration

`deploy/terraform/main.tf` passes the Supabase connection string and R2
configuration to Render. It uses this project's Session pooler at
`aws-1-eu-west-1.pooler.supabase.com`, with the project ID as the username
suffix. Also add `CLOUDFLARE_R2_ACCESS_KEY_ID` and
`CLOUDFLARE_R2_SECRET_ACCESS_KEY` to `.env`. These must be R2 S3 API credentials
scoped to the application bucket; do not commit them. Terraform configures R2
CORS to allow PUT uploads from the Render app origin. For local browser uploads
or a custom domain, add comma-separated origins to
`R2_EXTRA_ALLOWED_ORIGINS` in `.env`, then apply Terraform.

Uploads use short-lived, object-specific presigned PUT URLs. The R2 credentials
remain server-side. After an original is stored, the app records a list item and
a durable compression job. The background worker replaces the original with a
compressed image and removes the original after a short grace period for
in-flight image reads.

Render's free service may suspend while queued jobs remain. PostgreSQL retains
the jobs and they resume when the app starts again, but prompt compression
while the service is idle is not guaranteed. An always-on worker is required
for that guarantee.

## OpenTelemetry

The server exports traces and metrics through OTLP. Set
`OTEL_SERVICE_NAME`, `OTEL_EXPORTER_OTLP_ENDPOINT`,
`OTEL_EXPORTER_OTLP_PROTOCOL`, and `OTEL_EXPORTER_OTLP_HEADERS` in the server
environment. For Honeycomb, the headers should contain the Honeycomb API key;
keep that key server-side.

Traces include ASP.NET Core requests, outgoing HTTP calls, list/database
operations, and nested image-compression, direct browser-to-R2 upload, and
background-job spans. The
`christmas_list.direct_upload.duration` metric records the browser-reported
direct upload duration. The custom
`christmas_list.operation.duration` and `christmas_list.image.size` metrics
help compare time spent in compression, storage, and database operations.
Image telemetry records format, dimensions, and byte sizes, but not family
member names or storage object keys.
